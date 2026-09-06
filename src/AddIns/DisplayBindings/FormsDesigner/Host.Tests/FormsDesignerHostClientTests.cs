using System.ComponentModel.Design;

using ICSharpCode.FormsDesigner.OutOfProcess;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

public sealed class FormsDesignerHostClientTests
{
	[Theory]
	[InlineData("true", "", FormsDesignerBackend.MicrosoftWinForms)]
	[InlineData("false", "", FormsDesignerBackend.LibreWinForms)]
	[InlineData("", "", FormsDesignerBackend.LibreWinForms)]
	[InlineData("true", "libre", FormsDesignerBackend.LibreWinForms)]
	[InlineData("false", "microsoft", FormsDesignerBackend.MicrosoftWinForms)]
	public void ResolveBackend_UsesProjectPropertyUnlessExplicitlyOverridden(
		string useMicrosoftDesktopRuntime, string runtimeOverride, FormsDesignerBackend expected)
	{
		Assert.Equal(expected, FormsDesignerHostClient.ResolveBackend(useMicrosoftDesktopRuntime, runtimeOverride));
	}

	/// <summary>
	/// Regression test: opening a plain "Microsoft.NET.Sdk" WinForms project (e.g. JexusManager -
	/// UseWindowsForms=true, TargetFramework net9.0-windows...) never sets the bespoke
	/// UseMicrosoftDesktopRuntime property, which only three projects in this repo's own src/ tree
	/// set. Before this fix, ResolveBackend defaulted every such project to LibreWinForms even on
	/// Windows, contradicting doc/technotes/winforms-designer.md's documented "explicit by target
	/// framework and platform" selection and routing real desktop projects through the portable
	/// fork's out-of-process host, which has its own unrelated packaging gaps. On Windows, a TFM
	/// that targets Windows specifically must resolve to the real Microsoft backend unless something
	/// more specific overrides it; a TFM with no Windows suffix keeps the previous Libre default.
	/// </summary>
	[Theory]
	[InlineData("net9.0-windows10.0.17763.0", FormsDesignerBackend.MicrosoftWinForms)]
	[InlineData("net10.0-windows", FormsDesignerBackend.MicrosoftWinForms)]
	[InlineData("net8.0", FormsDesignerBackend.LibreWinForms)]
	[InlineData("", FormsDesignerBackend.LibreWinForms)]
	public void ResolveBackend_WithNoExplicitChoice_PicksByTargetFrameworkOnWindows(
		string targetFramework, FormsDesignerBackend expected)
	{
		Assert.Equal(expected, FormsDesignerHostClient.ResolveBackend("", "", targetFramework));
	}

	[Fact]
	public void ResolveBackend_ExplicitPropertyOrOverrideStillWinsOverTargetFramework()
	{
		Assert.Equal(FormsDesignerBackend.LibreWinForms,
			FormsDesignerHostClient.ResolveBackend("false", "", "net9.0-windows10.0.17763.0"));
		Assert.Equal(FormsDesignerBackend.LibreWinForms,
			FormsDesignerHostClient.ResolveBackend("", "libre", "net9.0-windows10.0.17763.0"));
	}

	/// <summary>The child host binary under test. Defaults to the LibreWinForms host; setting
	/// OPENDEVELOP_FORMSDESIGNER_HOST_DLL points this same suite at the Microsoft WindowsDesktop
	/// host (FormsDesigner/MicrosoftHost), which source-links the same host implementation. The
	/// DDP contract is what is being verified and it is identical for both, so the tests are
	/// shared rather than duplicated - only the child binary changes.</summary>
	static string HostDll() =>
		Environment.GetEnvironmentVariable("OPENDEVELOP_FORMSDESIGNER_HOST_DLL") is { Length: > 0 } overridden
			? Path.GetFullPath(overridden)
		#if MICROSOFT_FORMS_DESIGNER_HOST
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../Host/bin/Debug/net10.0-windows/MicrosoftFormsDesigner.Host.dll"));
		#else
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../Host/bin/Debug/net10.0-windows/FormsDesigner.Host.dll"));
		#endif

	/// <summary>The fixture assembly whose types the CHILD must resolve. Overridable alongside
	/// HostDll: the Microsoft run needs the fixture compiled against Microsoft WindowsDesktop, or
	/// the child cannot load it. The assembly name stays "FormsDesigner.CustomControlFixture" in
	/// both builds, so the snapshots in these tests are unchanged.</summary>
	static string CustomControlFixtureDll() =>
		Environment.GetEnvironmentVariable("OPENDEVELOP_FORMSDESIGNER_FIXTURE_DLL") is { Length: > 0 } overridden
			? Path.GetFullPath(overridden)
		#if MICROSOFT_FORMS_DESIGNER_HOST
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../Host.Tests/CustomControl/bin/Debug/net10.0-windows/FormsDesigner.CustomControlFixture.dll"));
		#else
			: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
				"../../../../Host.Tests/CustomControl/bin/Debug/net10.0-windows/FormsDesigner.CustomControlFixture.dll"));
		#endif

	/// <summary>
	/// Regression test: a designer file that references an enum member through its fully-qualified
	/// static path - "System.Drawing.FontStyle.Bold", the style VS/OpenDevelop's own generator and
	/// most real-world hand-written .Designer.cs files use (e.g. JexusManager's MainForm.Designer.cs
	/// line 416: <c>new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold)</c>)
	/// - used to evaluate to null: EvaluateMember only recognized a MemberAccessExpressionSyntax
	/// chain as a type once recursive evaluation bottomed out at an already-resolved Type, but each
	/// intermediate segment ("System", "System.Drawing") is not a resolvable value, so the whole
	/// chain silently evaluated to null. Activator.CreateInstance then received a null third
	/// argument for Font's constructor and could not disambiguate between same-arity overloads that
	/// differ only in that parameter's enum type (FontStyle vs GraphicsUnit), throwing
	/// AmbiguousMatchException instead of loading the designer.
	/// </summary>
	[Fact]
	public async Task ChildHost_ResolvesFullyQualifiedEnumMemberInObjectCreation()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = Snapshot(1, "button1");
		var designer = snapshot.Files.Single(item => item.Kind == "Designer");
		designer.Text = designer.Text.Replace("this.button1.Text = \"button1\";",
			"this.button1.Text = \"button1\";\n        this.button1.Font = new System.Drawing.Font(\"Microsoft Sans Serif\", 8.25F, System.Drawing.FontStyle.Bold);",
			StringComparison.Ordinal);

		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		var button1 = Assert.Single(opened.Components, component => component.Name == "button1");
		Assert.Contains(button1.Properties, property => property.Name == "Font" && !property.IsNull);
	}

	/// <summary>
	/// Regression test: JexusManager's MainForm.Designer.cs populates its MenuStrip via
	/// "menuStrip1.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, ... });" and adds the
	/// strip to the form via a bare "Controls.Add(menuStrip1);" (no "this."/"Me." prefix) - both
	/// exact patterns Visual Studio's own WinForms designer emits, not something JexusManager wrote
	/// by hand. SnapshotDesignerLoader.Execute only recognized single-item "x.Controls.Add(y)"
	/// invocations; every AddRange call (Items, DropDownItems, Controls alike) was silently
	/// dropped, so menuStrip1 loaded with zero menu items and, being a bare "Controls.Add" with no
	/// owner prefix, never even got added to the form itself - both the flat Components list and
	/// the Document Outline pad's Tree came back structurally wrong with no exception raised.
	/// </summary>
	[Fact]
	public async Task ChildHost_AddRangeAndBareControlsAddPopulateComponentTree()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
						        this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
						        this.viewToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
						        this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.fileToolStripMenuItem, this.viewToolStripMenuItem });
						        this.menuStrip1.Name = "menuStrip1";
						        Controls.Add(menuStrip1);
						    }
						    private System.Windows.Forms.MenuStrip menuStrip1;
						    private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;
						    private System.Windows.Forms.ToolStripMenuItem viewToolStripMenuItem;
						}
						"""
				}
			}
		};

		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

		// AddRange working is provable on both backends just by the items existing at all - before
		// this fix, an unrecognized AddRange invocation was silently skipped, so the loader never
		// created fileToolStripMenuItem/viewToolStripMenuItem in the first place and the bare
		// "Controls.Add(menuStrip1)" (no owner prefix) never sited menuStrip1 under the form.
		var menuStrip = Assert.Single(opened.Components, component => component.Name == "menuStrip1");
		Assert.Contains(opened.Components, component => component.Name == "fileToolStripMenuItem");
		Assert.Contains(opened.Components, component => component.Name == "viewToolStripMenuItem");
#if MICROSOFT_FORMS_DESIGNER_HOST
		// ToolStripItem.Owner/OwnerItem - and therefore Parent for the flat list - only exist on
		// the real Microsoft WinForms backend (see FormsDesignerHostClient.cs's ToolStripItem
		// gating); the portable LibreWinForms ToolStripItem does not expose them at all.
		Assert.Equal("Form1", menuStrip.Parent);
		Assert.Contains(opened.Components, component => component.Name == "fileToolStripMenuItem" && component.Parent == "menuStrip1");
		Assert.Contains(opened.Components, component => component.Name == "viewToolStripMenuItem" && component.Parent == "menuStrip1");
#endif
	}

	/// <summary>
	/// Per-strip item-insertion style, mirroring ToolStripTemplateNode.SetupNewEditNode's own
	/// branch: a MenuStrip (like any dropdown) gets SetUpMenuTemplateNode's editable "Type Here"
	/// cell, while ToolStrip/StatusStrip - and ContextMenuStrip, which is easy to assume goes with
	/// the menus but does not - get SetUpToolTemplateNode's split button. The reported type list
	/// is ToolStripDesignerUtils.GetStandardItemTypes' own order, whose FIRST entry is what the
	/// client commits when the user types a name without picking a type.
	/// </summary>
	[Fact]
	public async Task ChildHost_ReportsPerStripItemInsertionStyleAndDefaultItemType()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private System.ComponentModel.IContainer components;
						    private void InitializeComponent()
						    {
						        this.components = new System.ComponentModel.Container();
						        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
						        this.toolStrip1 = new System.Windows.Forms.ToolStrip();
						        this.statusStrip1 = new System.Windows.Forms.StatusStrip();
						        this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
						        this.button1 = new System.Windows.Forms.Button();
						        this.Controls.Add(this.menuStrip1);
						        this.Controls.Add(this.toolStrip1);
						        this.Controls.Add(this.statusStrip1);
						        this.Controls.Add(this.button1);
						    }
						    private System.Windows.Forms.MenuStrip menuStrip1;
						    private System.Windows.Forms.ToolStrip toolStrip1;
						    private System.Windows.Forms.StatusStrip statusStrip1;
						    private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
						    private System.Windows.Forms.Button button1;
						}
						"""
				}
			}
		};

		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		DesignerComponentInfo Component(string name) =>
			Assert.Single(opened.Components, component => component.Name == name);
		// A plain control has no insertion affordance at all.
		Assert.Equal("", Component("button1").ItemInsertionStyle);
		Assert.Empty(Component("button1").NewItemTypeNames);
#if MICROSOFT_FORMS_DESIGNER_HOST
		var menuStrip = Component("menuStrip1");
		Assert.Equal(DesignerItemInsertionStyles.TypeHere, menuStrip.ItemInsertionStyle);
		Assert.Equal("System.Windows.Forms.ToolStripMenuItem", menuStrip.NewItemTypeNames.First());

		var toolStrip = Component("toolStrip1");
		Assert.Equal(DesignerItemInsertionStyles.SplitButton, toolStrip.ItemInsertionStyle);
		Assert.Equal("System.Windows.Forms.ToolStripButton", toolStrip.NewItemTypeNames.First());

		var statusStrip = Component("statusStrip1");
		Assert.Equal(DesignerItemInsertionStyles.SplitButton, statusStrip.ItemInsertionStyle);
		Assert.Equal("System.Windows.Forms.ToolStripStatusLabel", statusStrip.NewItemTypeNames.First());

		// ContextMenuStrip is a ToolStripDropDown, so it takes the "Type Here" branch like the
		// menus - and its list is the dropdown one, which uniquely includes a separator (the type
		// a lone "-" commits to).
		var contextMenu = Component("contextMenuStrip1");
		Assert.Equal(DesignerItemInsertionStyles.TypeHere, contextMenu.ItemInsertionStyle);
		Assert.Contains("System.Windows.Forms.ToolStripSeparator", contextMenu.NewItemTypeNames);
#else
		// The portable fork's strips report no insertion affordance: the client's "Type Here" cell
		// and split button are both Microsoft-backend features.
		Assert.Equal("", Component("menuStrip1").ItemInsertionStyle);
		Assert.Equal("", Component("toolStrip1").ItemInsertionStyle);
#endif
	}

	/// <summary>
	/// The component tray's membership rule, ported from
	/// System.Windows.Forms.Design.DocumentDesigner.OnComponentAdded ("If the component is a
	/// toolstrip or a top level form, we should add to the tray"): a component gets a tray entry
	/// when its designer IS a ToolStripDesigner, OR its designer is not a ControlDesigner at all,
	/// OR it is a top-level Form - provided the type is design-time visible.
	///
	/// Both clauses are asserted here because each has a counter-intuitive case: a MenuStrip gets
	/// a tray entry even though it is a perfectly visible Control laid out on the surface (first
	/// clause - real VS shows strips in both places), while a ContextMenuStrip gets one because
	/// its ToolStripDropDownDesigner is a ComponentDesigner rather than a ControlDesigner (second
	/// clause). A ToolStripContainer gets NO entry - its designer is a ControlDesigner that is not
	/// a ToolStripDesigner - which is what keeps the first clause from over-matching.
	/// </summary>
	[Fact]
	public async Task ChildHost_ReportsTrayComponentsByDesignerKindNotJustControlness()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private System.ComponentModel.IContainer components;
						    private void InitializeComponent()
						    {
						        this.components = new System.ComponentModel.Container();
						        this.timer1 = new System.Windows.Forms.Timer(this.components);
						        this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
						        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
						        this.toolStripContainer1 = new System.Windows.Forms.ToolStripContainer();
						        this.button1 = new System.Windows.Forms.Button();
						        this.Controls.Add(this.menuStrip1);
						        this.Controls.Add(this.toolStripContainer1);
						        this.Controls.Add(this.button1);
						    }
						    private System.Windows.Forms.Timer timer1;
						    private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
						    private System.Windows.Forms.MenuStrip menuStrip1;
						    private System.Windows.Forms.ToolStripContainer toolStripContainer1;
						    private System.Windows.Forms.Button button1;
						}
						"""
				}
			}
		};

		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		// The root form is never a tray component, whatever its designer says.
		Assert.All(opened.Components.Where(component => component.Name == "Form1"),
			component => Assert.False(component.IsTrayComponent));
#if MICROSOFT_FORMS_DESIGNER_HOST
		// Not a Control at all -> tray.
		Assert.Contains(opened.Components, component => component.Name == "timer1" && component.IsTrayComponent);
		// A Control, but ToolStripDropDownDesigner is a ComponentDesigner -> tray.
		Assert.Contains(opened.Components, component => component.Name == "contextMenuStrip1" && component.IsTrayComponent);
		// A visible Control laid out on the surface that STILL gets a tray entry, because its
		// designer is a ToolStripDesigner. It keeps its surface presence too - Parent is set -
		// which is what the client uses to decide it still deserves canvas adorners.
		var trayMenuStrip = Assert.Single(opened.Components, component => component.Name == "menuStrip1");
		Assert.True(trayMenuStrip.IsTrayComponent);
		Assert.Equal("Form1", trayMenuStrip.Parent);
		// A ControlDesigner that is NOT a ToolStripDesigner -> no tray entry (guards the first
		// clause against over-matching every ToolStrip-adjacent control).
		Assert.Contains(opened.Components, component => component.Name == "toolStripContainer1" && !component.IsTrayComponent);
		Assert.Contains(opened.Components, component => component.Name == "button1" && !component.IsTrayComponent);
#else
		// The portable LibreWinForms fork ships none of the System.Windows.Forms.Design designer
		// types these DesignerAttributes name, so the designer-kind clauses cannot be evaluated
		// there: only "not a Control at all" reaches the tray, every Control stays on the surface.
		Assert.Contains(opened.Components, component => component.Name == "timer1" && component.IsTrayComponent);
		Assert.Contains(opened.Components, component => component.Name == "menuStrip1" && !component.IsTrayComponent);
		Assert.Contains(opened.Components, component => component.Name == "button1" && !component.IsTrayComponent);
#endif
	}

	/// <summary>
	/// Regression test: a real click on the design surface sends SURFACE (rendered-bitmap)
	/// coordinates to design/hit-test - the same space DesignerComponentInfo.SurfaceX/SurfaceY
	/// report a component's position in. On the Microsoft backend, the rendered bitmap is the
	/// whole native window (Form.DrawToBitmap paints border + caption too), so surface space and
	/// each Control's own client-space Bounds differ by the root form's non-client offset.
	/// HitTest compared an incoming surface point directly against client-space Bounds without
	/// ever subtracting that offset, so every click landed on whatever control happened to sit
	/// "offset pixels" above the real target - clicking a control near the bottom of a tall form
	/// could select nothing, or the wrong control, while the Document Outline pad (which never
	/// hit-tests) kept working, hiding the bug from every non-mouse test.
	/// </summary>
	[Fact]
	public async Task ChildHost_HitTestAccountsForRootNonClientOffsetAtReportedSurfaceCoordinates()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.buttonTop = new System.Windows.Forms.Button();
						        this.buttonTop.Location = new System.Drawing.Point(10, 10);
						        this.buttonTop.Size = new System.Drawing.Size(80, 30);
						        this.buttonBottom = new System.Windows.Forms.Button();
						        this.buttonBottom.Location = new System.Drawing.Point(10, 260);
						        this.buttonBottom.Size = new System.Drawing.Size(80, 30);
						        this.ClientSize = new System.Drawing.Size(300, 320);
						        this.Controls.Add(this.buttonTop);
						        this.Controls.Add(this.buttonBottom);
						    }
						    private System.Windows.Forms.Button buttonTop;
						    private System.Windows.Forms.Button buttonBottom;
						}
						"""
				}
			}
		};

		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		var bottom = Assert.Single(opened.Components, component => component.Name == "buttonBottom");

		var centerX = bottom.SurfaceX + bottom.Width / 2;
		var centerY = bottom.SurfaceY + bottom.Height / 2;
		var hit = await client.HitTestAsync(1, centerX, centerY, timeout.Token);
		Assert.Equal("buttonBottom", hit.ComponentName);
#if MICROSOFT_FORMS_DESIGNER_HOST
		// Guard against a vacuously-passing test: on the Microsoft backend the rendered bitmap
		// includes the form's caption/border, so SurfaceY must differ from the client-space Y the
		// designer source set (260) - if this ever reads equal, the offset stopped being applied
		// at all and the assertion above would pass for the wrong reason.
		Assert.NotEqual(260, bottom.SurfaceY);
#endif
	}

	[Fact]
	public async Task ChildHost_HandshakesRejectsStaleVersionsAndFlushesCurrentSnapshot()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		Assert.True(client.IsAlive);
		Assert.NotEqual(Environment.ProcessId, client.ProcessId);

		var first = Snapshot(7, "button1");
		var opened = await client.OpenAsync(first, timeout.Token);
		Assert.True(opened.Accepted);
		Assert.Equal(client.SessionId, opened.SessionId);
		Assert.Equal(client.DocumentId, opened.DocumentId);
		Assert.Equal(7, opened.Version);
		Assert.Equal("System.Windows.Forms.Form", opened.RootType);
		Assert.True(opened.ComponentCount >= 1);
		Assert.NotNull(opened.Render);
		Assert.True(opened.Render.Sequence > 0);
		Assert.True(opened.Render.Dpi >= 1);
		Assert.True(opened.Render.Width > 0 && opened.Render.Height > 0);
		Assert.True(!String.IsNullOrEmpty(opened.Render.PngBase64) || !String.IsNullOrEmpty(opened.Render.Data));
		var png = String.IsNullOrEmpty(opened.Render.PngBase64)
			? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+bpWnAAAAAElFTkSuQmCC")
			: Convert.FromBase64String(opened.Render.PngBase64);
		if (!String.IsNullOrEmpty(opened.Render.PngBase64)) {
			Assert.True(png.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
		} else {
			Assert.Equal(opened.Render.Width * opened.Render.Height * 4, DesignerFrameCodec.DecodeBgra32(opened.Render).Length);
			Assert.Contains(opened.Diagnostics, diagnostic => diagnostic.Severity == "Warning" && diagnostic.Message.Contains("GPU frame readback", StringComparison.Ordinal));
		}
		Assert.Contains(opened.Components, component => component.Name == "button1"
			&& component.Type == "System.Windows.Forms.Button"
			&& component.Parent == "Form1"
			&& component.Text == "button1"
			&& component.X == 12 && component.Y == 20
			&& component.Width == 90 && component.Height == 30);
		var openedButton = Assert.Single(opened.Components, component => component.Name == "button1");
		Assert.Equal("button1", openedButton.AccessibleName);
		Assert.Equal("Button", openedButton.AccessibleRole);
		Assert.Contains(openedButton.Properties, property => property.Name == "Text" && property.DisplayName == "Text"
			&& property.TypeName == "System.String");
		Assert.Contains(openedButton.Properties, property => property.Name == "Enabled" && property.TypeName == "System.Boolean");
		// design/hit-test takes SURFACE (rendered-bitmap) coordinates - openedButton.SurfaceX/Y,
		// not its client-space X/Y (12, 20) - the two differ on the Microsoft backend by the root
		// form's non-client border/caption offset (see ChildHost_HitTestAccountsFor... below).
		var hit = await client.HitTestAsync(7, openedButton.SurfaceX + 8, openedButton.SurfaceY + 5, timeout.Token);
		Assert.Equal("button1", hit.ComponentName);
		Assert.Equal("System.Windows.Forms.Button", hit.ComponentType);
		var resizedRoot = await client.SetBoundsAsync(7, "Form1", 0, 0, 420, 260, timeout.Token);
		Assert.Contains(resizedRoot.Components, component => component.Name == "Form1"
			&& component.Width >= 420 && component.Height >= 260);
		Assert.Contains("Size = new System.Drawing.Size(420, 260);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var scaledRoot = await client.SetPropertyAsync(7, "Form1", "AutoScaleDimensions", "8, 16", timeout.Token);
		Assert.True(scaledRoot.Accepted);
		// The reported Properties are the property GRID's contents, so they follow each runtime's
		// own [Browsable] decisions: Microsoft WinForms marks ContainerControl.AutoScaleDimensions
		// Browsable(false) (hidden from the grid but still designer-serialized), LibreWinForms does
		// not. Assert the value only where the runtime exposes it; the generated-designer-code
		// assertion below is the runtime-neutral proof that the set actually took effect, and it
		// runs unconditionally on both.
		var scaledForm = scaledRoot.Components.Single(component => component.Name == "Form1");
		if (scaledForm.Properties.Any(property => property.Name == "AutoScaleDimensions"))
			Assert.Contains(scaledForm.Properties,
				property => property.Name == "AutoScaleDimensions" && property.Value == "8, 16");
		Assert.Contains("AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var edited = await client.SetPropertyAsync(7, "button1", "Text", "edited in child", timeout.Token);
		Assert.True(edited.Accepted);
		Assert.True(edited.Render!.Sequence > opened.Render.Sequence);
		Assert.Contains(edited.Components, component => component.Name == "button1" && component.Text == "edited in child");
		Assert.Contains(edited.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Text" && property.ShouldSerialize);
		var editedFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("edited in child", DesignerText(editedFiles), StringComparison.Ordinal);
		var disabled = await client.SetPropertyAsync(7, "button1", "Enabled", "False", timeout.Token);
		Assert.Contains(disabled.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Enabled" && property.Value.Equals("False", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("button1.Enabled = false;", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var anchored = await client.SetPropertyAsync(7, "button1", "Anchor", "Top, Left", timeout.Token);
		Assert.Contains(anchored.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Anchor" && property.IsEnum);
		Assert.Contains("button1.Anchor = (System.Windows.Forms.AnchorStyles)5;",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var padded = await client.SetPropertyAsync(7, "button1", "Padding", "1, 2, 3, 4", timeout.Token);
		Assert.Contains(padded.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Padding" && property.Value.Contains("1", StringComparison.Ordinal));
		Assert.Contains("button1.Padding = new System.Windows.Forms.Padding(1, 2, 3, 4);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var resetEnabled = await client.ResetPropertyAsync(7, "button1", "Enabled", timeout.Token);
		Assert.Contains(resetEnabled.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Enabled"
				&& property.Value.Equals("True", StringComparison.OrdinalIgnoreCase)
				&& !property.ShouldSerialize);
		Assert.DoesNotContain("button1.Enabled =", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);
		Assert.Contains(openedButton.Events, item => item.Name == "Click" && item.HandlerTypeName == "System.EventHandler");
		var eventBound = await client.SetEventAsync(7, "button1", "Click", "button1_Click", timeout.Token);
		Assert.Contains(eventBound.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "button1_Click");
		var eventFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("button1.Click += button1_Click;", eventFiles.Files.Single(item => item.Kind == "Designer").Text, StringComparison.Ordinal);
		Assert.Contains("private void button1_Click(System.Object sender, System.EventArgs e)", eventFiles.Files.Single(item => item.Kind == "Source").Text, StringComparison.Ordinal);
		var eventCleared = await client.SetEventAsync(7, "button1", "Click", "", timeout.Token);
		Assert.Contains(eventCleared.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "");
		var clearedFiles = await client.FlushAsync(7, timeout.Token);
		Assert.DoesNotContain("button1.Click +=", clearedFiles.Files.Single(item => item.Kind == "Designer").Text, StringComparison.Ordinal);
		Assert.Contains("private void button1_Click", clearedFiles.Files.Single(item => item.Kind == "Source").Text, StringComparison.Ordinal);
		var defaultEvent = await client.ActivateDefaultEventAsync(7, "button1", timeout.Token);
		Assert.Contains(defaultEvent.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "button1_Click");
		Assert.Contains("button1.Click += button1_Click;",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var added = await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "System.Windows.Forms.Label" }, "label1", 30, 70, timeout.Token);
		Assert.Contains(added.Components, component => component.Name == "label1"
			&& component.Type == "System.Windows.Forms.Label" && component.Parent == "Form1"
			&& component.X == 30 && component.Y == 70);
		var addedFiles = await client.FlushAsync(7, timeout.Token);
		var addedSource = DesignerText(addedFiles);
		Assert.Contains("label1 = new System.Windows.Forms.Label();", addedSource, StringComparison.Ordinal);
		Assert.Contains("Controls.Add(label1);", addedSource, StringComparison.Ordinal);
		Assert.Contains("private System.Windows.Forms.Label label1;", addedSource, StringComparison.Ordinal);
		Assert.Contains("label1.Size = new System.Drawing.Size(", addedSource, StringComparison.Ordinal);
		var labeled = await client.SetPropertyAsync(7, "label1", "Text", "new label", timeout.Token);
		Assert.Contains(labeled.Components, component => component.Name == "label1" && component.Text == "new label");
		Assert.Contains("label1.Text = \"new label\";", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var moved = await client.SetBoundsAsync(7, "button1", 40, 50, 120, 35, timeout.Token);
		Assert.Contains(moved.Components, component => component.Name == "button1"
			&& component.X == 40 && component.Y == 50 && component.Width == 120 && component.Height == 35);
		var movedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("new System.Drawing.Point(40, 50)", movedSource, StringComparison.Ordinal);
		Assert.Contains("new System.Drawing.Size(120, 35)", movedSource, StringComparison.Ordinal);

		var withPanel = await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "Panel" }, "panel1", 10, 100, timeout.Token);
		var panel1 = Assert.Single(withPanel.Components, component => component.Name == "panel1");
		var nested = await client.AddElementAsync(7, "panel1", new DesignerToolboxItemInfo { TypeName = "Button" }, "nestedButton", 5, 6, timeout.Token);
		// SurfaceX/Y are relative to panel1's own reported surface position (itself offset by the
		// root form's non-client border on the Microsoft backend) plus the button's LOCAL (client,
		// panel-relative) offset - not a value hardcoded against a zero root offset.
		Assert.Contains(nested.Components, component => component.Name == "nestedButton"
			&& component.Parent == "panel1" && component.X == 5 && component.Y == 6
			&& component.SurfaceX == panel1.SurfaceX + 5 && component.SurfaceY == panel1.SurfaceY + 6);
		Assert.Contains("panel1.Controls.Add(nestedButton);",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var beforeAdvancedControlRender = FramePayload(nested.Render!);
		var advanced = await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "DataGridView" }, "dataGridView1", 145, 10, timeout.Token);
		Assert.Contains(advanced.Components, component => component.Name == "dataGridView1"
			&& component.Type == "System.Windows.Forms.DataGridView");
		Assert.NotEqual(beforeAdvancedControlRender, FramePayload(advanced.Render!));
		Assert.Contains("System.Windows.Forms.DataGridView dataGridView1", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "Button" }, "renameMe", 5, 5, timeout.Token);
		var renamed = await client.RenameAsync(7, "renameMe", "renamedButton", timeout.Token);
		Assert.Contains(renamed.Components, component => component.Name == "renamedButton");
		Assert.DoesNotContain(renamed.Components, component => component.Name == "renameMe");
		var renamedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("renamedButton = new System.Windows.Forms.Button();", renamedSource, StringComparison.Ordinal);
		Assert.Contains("renamedButton.Name = \"renamedButton\";", renamedSource, StringComparison.Ordinal);
		Assert.DoesNotContain("renameMe", renamedSource, StringComparison.Ordinal);
		var sentBack = await client.SetZOrderAsync(7, "button1", false, timeout.Token);
		Assert.True(sentBack.Accepted);
		Assert.Contains("Controls.SetChildIndex(button1,", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var broughtFront = await client.SetZOrderAsync(7, "button1", true, timeout.Token);
		Assert.True(broughtFront.Accepted);
		Assert.Contains("Controls.SetChildIndex(button1, 0);", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var aligned = await client.ApplyLayoutAsync(7, "align-left", new[] { "button1", "label1" }, 0, 0, timeout.Token);
		Assert.Equal(40, aligned.Components.Single(item => item.Name == "button1").X);
		Assert.Equal(40, aligned.Components.Single(item => item.Name == "label1").X);
		var sameWidth = await client.ApplyLayoutAsync(7, "same-width", new[] { "button1", "label1" }, 0, 0, timeout.Token);
		Assert.Equal(120, sameWidth.Components.Single(item => item.Name == "label1").Width);
		var layoutSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("label1.Location = new System.Drawing.Point(40, 70)", layoutSource, StringComparison.Ordinal);
		Assert.Contains("label1.Size = new System.Drawing.Size(120,", layoutSource, StringComparison.Ordinal);
		var groupMoved = await client.ApplyLayoutAsync(7, "move", new[] { "button1", "label1" }, 3, 4, timeout.Token);
		Assert.Contains(groupMoved.Components, item => item.Name == "button1" && item.X == 43 && item.Y == 54);
		Assert.Contains(groupMoved.Components, item => item.Name == "label1" && item.X == 43 && item.Y == 74);
		Assert.Contains("label1.Location = new System.Drawing.Point(43, 74)",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		await client.DeleteElementsAsync(7, new[] { "renamedButton" }, timeout.Token);
		var deleted = await client.DeleteElementsAsync(7, new[] { "label1" }, timeout.Token);
		Assert.DoesNotContain(deleted.Components, component => component.Name == "label1");
		Assert.DoesNotContain("label1", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);
		var stale = await client.UpdateAsync(Snapshot(7, "stale"), timeout.Token);
		Assert.False(stale.Accepted);
		Assert.Contains("Stale", stale.Error, StringComparison.Ordinal);

		var nextSnapshot = Snapshot(8, "button2");
		var nextDesigner = nextSnapshot.Files.Single(item => item.Kind == "Designer");
		nextDesigner.Text = nextDesigner.Text.Replace("this.button1.Size =",
			"this.button1.Padding = new System.Windows.Forms.Padding(4, 3, 2, 1);\n        this.button1.Size =", StringComparison.Ordinal);
		nextDesigner.Text = nextDesigner.Text.Replace("this.button1 = new System.Windows.Forms.Button();",
			"this.button1 = new System.Windows.Forms.Button();\n        this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);\n        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;", StringComparison.Ordinal);
		var current = await client.UpdateAsync(nextSnapshot, timeout.Token);
		Assert.True(current.Accepted);
		Assert.Contains(current.Components, component => component.Name == "button1" && component.Text == "button2");
		Assert.Contains(current.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Padding" && property.Value == "4, 3, 2, 1");
		// Grid-visibility of AutoScaleDimensions is runtime-specific - see the note on the earlier
		// AutoScaleDimensions assertion. Where the runtime exposes it, it must carry the value the
		// updated designer code declares.
		var loadedForm = current.Components.Single(component => component.Name == "Form1");
		if (loadedForm.Properties.Any(property => property.Name == "AutoScaleDimensions"))
		{
			var loadedScale = Assert.Single(loadedForm.Properties,
				property => property.Name == "AutoScaleDimensions");
			Assert.Equal("7, 15", loadedScale.Value);
		}
		var edits = await client.FlushAsync(8, timeout.Token);
		Assert.Equal(client.SessionId, edits.SessionId);
		Assert.Equal(client.DocumentId, edits.DocumentId);
		Assert.Equal(8, edits.BaseVersion);
		Assert.Contains("button2", DesignerText(edits), StringComparison.Ordinal);

		var resourceSnapshot = Snapshot(9, "fallback");
		var resourceDesigner = resourceSnapshot.Files.Single(item => item.Kind == "Designer");
		resourceDesigner.Text = resourceDesigner.Text.Replace(
			"this.button1.Text = \"fallback\";",
			"resources.ApplyResources(this.button1, nameof(button1));\n        this.button1.Image = (System.Drawing.Image)resources.GetObject(\"button1.Image\");",
			StringComparison.Ordinal);
		resourceSnapshot.Files.Add(new DesignerSourceFileSnapshot {
			FileName = "/project/Form1.resx", Kind = "Resource",
			Base64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
				$"<root><data name=\"button1.Text\"><value>localized text</value></data>" +
				$"<data name=\"button1.Image\" type=\"System.Drawing.Bitmap, System.Drawing.Common\" mimetype=\"application/x-microsoft.net.object.bytearray.base64\"><value>{Convert.ToBase64String(png)}</value></data></root>"))
		});
		var resourceLoaded = await client.UpdateAsync(resourceSnapshot, timeout.Token);
		Assert.Contains(resourceLoaded.Components, component => component.Name == "button1" && component.Text == "localized text");
		Assert.Contains(resourceLoaded.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Image" && !property.IsNull && property.Value == "[binary]");
		Assert.Contains((await client.FlushAsync(9, timeout.Token)).Files, item => item.Kind == "Resource" && !String.IsNullOrEmpty(item.Base64));

		var fixtureAssembly = CustomControlFixtureDll();
		Assert.True(File.Exists(fixtureAssembly));
		Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "FormsDesigner.CustomControlFixture");
		var customSnapshot = Snapshot(10, "custom");
		customSnapshot.ProjectAssemblyPath = fixtureAssembly;
		var customDesigner = customSnapshot.Files.Single(item => item.Kind == "Designer");
		customDesigner.Text = customDesigner.Text
			.Replace("System.Windows.Forms.Button", "FormsDesigner.CustomControlFixture.FancyButton", StringComparison.Ordinal);
		var custom = await client.UpdateAsync(customSnapshot, timeout.Token);
		Assert.True(custom.Accepted);
		Assert.Contains(custom.Components, component => component.Name == "button1"
			&& component.Type == "FormsDesigner.CustomControlFixture.FancyButton");
		Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == "FormsDesigner.CustomControlFixture");

		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		client.HostExited += (sender, args) => exited.TrySetResult();
		await Assert.ThrowsAsync<TimeoutException>(() => client.DelayAsync(5000, timeout.Token));
		await exited.Task.WaitAsync(timeout.Token);
		Assert.False(client.IsAlive);
	}

	/// <summary>
	/// Regression test for the real-WinForms-toolbox-icon RPC (design/get-type-icon) that backs
	/// the smart-tag popup / ToolStrip insert-item dropdown's icon rows. Both backends carry the
	/// same System.Drawing.Common ToolboxBitmapAttribute machinery, so
	/// System.Windows.Forms.Button's embedded 16x16 icon should resolve on either host - this
	/// runs unconditionally (no MICROSOFT_FORMS_DESIGNER_HOST gate), asserting only that the
	/// bytes decode as a valid, non-trivial PNG when present.
	/// </summary>
	[Fact]
	public async Task ChildHost_ResolvesRealWinFormsToolboxIconForKnownType()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var opened = await client.OpenAsync(Snapshot(1, "icon"), timeout.Token);
		Assert.True(opened.Accepted);

		var icon = await client.GetTypeIconAsync("System.Windows.Forms.Button", timeout.Token);
		if (String.IsNullOrEmpty(icon)) {
			// Acceptable outcome on a backend whose fork does not embed the same toolbox
			// resource - the client falls back to its own placeholder glyph rather than erroring.
			return;
		}
		var bytes = Convert.FromBase64String(icon);
		Assert.True(bytes.Length > 16, "Decoded icon PNG is implausibly small: " + bytes.Length + " bytes");
		// PNG magic number: 0x89 'P' 'N' 'G' \r \n \x1A \n
		Assert.Equal(0x89, bytes[0]);
		Assert.Equal((byte)'P', bytes[1]);
		Assert.Equal((byte)'N', bytes[2]);
		Assert.Equal((byte)'G', bytes[3]);

		// Repeat call should hit the host's own per-type cache and return the identical bytes.
		var iconAgain = await client.GetTypeIconAsync("System.Windows.Forms.Button", timeout.Token);
		Assert.Equal(icon, iconAgain);
	}

	/// <summary>
	/// Regression test for the WinForms smart-tag/DesignerActionList popup and the ToolStrip
	/// "insert item" chevron. Both are Microsoft-backend-only (LibreWinForms has no
	/// System.ComponentModel.Design.DesignerActionService support), so the smart-tag assertions
	/// are gated on MICROSOFT_FORMS_DESIGNER_HOST; add-toolstrip-item is asserted to at least fail
	/// clearly on the Libre host instead of silently no-opping.
	/// </summary>
	[Fact]
	public async Task ChildHost_SupportsSmartTagActionsAndToolStripItemInsertion()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
						        this.menuStrip1.Name = "menuStrip1";
						        this.Controls.Add(this.menuStrip1);
						    }
						    private System.Windows.Forms.MenuStrip menuStrip1;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		Assert.Contains(opened.Components, component => component.Name == "menuStrip1");

#if MICROSOFT_FORMS_DESIGNER_HOST
		// menuStrip1's registered ToolStripActionList exposes "Insert Standard Items" (a method
		// item) and RenderMode/Dock (property items) - real VS smart-tag content, not a fixture.
		var actions = await client.ListSmartTagActionsAsync(1, "menuStrip1", timeout.Token);
		Assert.True(actions.Accepted);
		Assert.NotEmpty(actions.Items);
		var insertStandardItems = Assert.Single(actions.Items,
			item => item.Kind == "Method" && item.DisplayName.Contains("Insert Standard Items", StringComparison.OrdinalIgnoreCase));
		var renderMode = Assert.Single(actions.Items, item => item.Kind == "Property" && item.MemberName == "RenderMode");
		Assert.True(renderMode.IsEnum);

		// Mechanical proof the method-item RPC path works end to end (a real
		// DesignerActionMethodItem resolved by (listIndex, itemIndex) and invoked inside a
		// transaction, no exception). ToolStripActionList.InsertStandardItems genuinely populates
		// real ToolStripMenuItems (File/Edit/Tools/Help, VS's own standard set) now that
		// CreateDesignSurface registers an INameCreationService - added for TabControlActionList's
		// "Add Tab"/"Remove Tab" verbs, but this method needed it too (not, as first assumed, a
		// BehaviorService: it never asks for one, it just couldn't name what it created). Not
		// asserting the exact standard-item set here, since that is Microsoft's own internal
		// implementation detail rather than this host's contract - but see the next block's use of
		// "customMenuItem"/"customSubMenuItem" rather than a name resembling a standard item, to
		// avoid colliding with whatever this call just created.
		var afterInsert = await client.InvokeSmartTagMethodAsync(1, "menuStrip1", insertStandardItems.ListIndex, insertStandardItems.ItemIndex, timeout.Token);
		Assert.True(afterInsert.Accepted);

		// The property-item edit path: a smart-tag property (RenderMode) round-trips through the
		// EXISTING design/set-property RPC via PropertyOwnerElementId + MemberName, proving the
		// popup's inline editor needs no new "commit" RPC of its own.
		var ownerElementId = String.IsNullOrEmpty(renderMode.PropertyOwnerElementId) ? "menuStrip1" : renderMode.PropertyOwnerElementId;
		var renderModeSet = await client.SetPropertyAsync(1, ownerElementId, renderMode.MemberName, "Professional", timeout.Token);
		Assert.True(renderModeSet.Accepted);
		Assert.Contains(renderModeSet.Components.Single(c => c.Name == "menuStrip1").Properties,
			property => property.Name == "RenderMode" && property.Value == "Professional");
#endif

#if MICROSOFT_FORMS_DESIGNER_HOST
		var withItem = await client.AddToolStripItemAsync(1, "menuStrip1", "ToolStripMenuItem", "", "customMenuItem", timeout.Token);
		Assert.True(withItem.Accepted);
		Assert.Contains(withItem.Components, component => component.Name == "customMenuItem"
			&& component.Type == "System.Windows.Forms.ToolStripMenuItem" && component.Parent == "menuStrip1");
		var flushed = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.Contains("customMenuItem = new System.Windows.Forms.ToolStripMenuItem();", flushed, StringComparison.Ordinal);
		// Flush's ThisQualifierRewriter drops the redundant "this." prefix (same convention as
		// every other RewriteAdded* helper in DesignerHostService.cs - see e.g. the plain
		// "Controls.Add(label1);" assertions above), so the emitted statement has none either.
		Assert.Contains("menuStrip1.Items.Add(customMenuItem);", flushed, StringComparison.Ordinal);

		// A submenu item nests into the parent's DropDownItems rather than the strip's own Items.
		var withSubItem = await client.AddToolStripItemAsync(1, "menuStrip1", "ToolStripMenuItem", "customMenuItem", "customSubMenuItem", timeout.Token);
		Assert.Contains(withSubItem.Components, component => component.Name == "customSubMenuItem" && component.Parent == "customMenuItem");
		Assert.Contains("customMenuItem.DropDownItems.Add(customSubMenuItem);",
			DesignerText(await client.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
#else
		// LibreWinForms: fail clearly (a thrown NotSupportedException over RPC) instead of
		// silently no-opping.
		await Assert.ThrowsAnyAsync<Exception>(() =>
			client.AddToolStripItemAsync(1, "menuStrip1", "ToolStripMenuItem", "", "customMenuItem", timeout.Token));
#endif
	}

#if MICROSOFT_FORMS_DESIGNER_HOST
	/// <summary>
	/// Regression test for TabControl's "Add Tab"/"Remove Tab" - real VS's TabControlDesigner
	/// exposes these as DESIGNER VERBS (ComponentDesigner.Verbs, the right-click context-menu
	/// mechanism), NOT smart-tag actions (ActionLists) - confirmed empirically: ListSmartTagActions
	/// returns zero items for a TabControl. design/list-verbs and design/invoke-verb are the generic
	/// verb-equivalent of the smart-tag RPCs, with no TabControl-specific server code. Unlike
	/// ToolStripActionList's "Insert Standard Items" (see
	/// ChildHost_SupportsSmartTagActionsAndToolStripItemInsertion's own note), these methods mutate
	/// TabPages via host.CreateComponent/host.DestroyComponent directly rather than needing a
	/// BehaviorService, so they DO create/destroy real sited components in this headless host - and
	/// design/invoke-verb must sync that to designer source, which is what this test actually
	/// exercises (see InvokeAndSyncComponentChanges' own doc comment).
	/// </summary>
	[Fact]
	public async Task ChildHost_TabControlAddRemoveTabVerbs_SyncNewAndRemovedTabPagesToSource()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.tabControl1 = new System.Windows.Forms.TabControl();
						        this.tabPage1 = new System.Windows.Forms.TabPage();
						        this.tabControl1.Controls.Add(this.tabPage1);
						        this.tabControl1.Name = "tabControl1";
						        this.tabPage1.Name = "tabPage1";
						        this.tabPage1.Text = "Page1";
						        this.Controls.Add(this.tabControl1);
						    }
						    private System.Windows.Forms.TabControl tabControl1;
						    private System.Windows.Forms.TabPage tabPage1;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		Assert.Contains(opened.Components, component => component.Name == "tabControl1");

		var verbs = await client.ListVerbsAsync(1, "tabControl1", timeout.Token);
		Assert.True(verbs.Accepted);
		var addTab = Assert.Single(verbs.Items, item => item.Text.Contains("Add", StringComparison.OrdinalIgnoreCase)
			&& item.Text.Contains("Tab", StringComparison.OrdinalIgnoreCase));

		var afterAdd = await client.InvokeVerbAsync(1, "tabControl1", addTab.Index, timeout.Token);
		Assert.True(afterAdd.Accepted);
		var newPage = Assert.Single(afterAdd.Components,
			component => component.Type == "System.Windows.Forms.TabPage" && component.Name != "tabPage1");
		Assert.Equal("tabControl1", newPage.Parent);

		var flushedAfterAdd = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.Contains($"{newPage.Name} = new System.Windows.Forms.TabPage();", flushedAfterAdd, StringComparison.Ordinal);
		Assert.Contains($"tabControl1.Controls.Add({newPage.Name});", flushedAfterAdd, StringComparison.Ordinal);
		// The pre-existing sibling tab page must still be there - a regression here would mean the
		// sync logic (or the underlying delete-statement walk it reuses) wiped unrelated siblings,
		// the exact class of bug this session already fixed once for plain Delete (see
		// ChildHost_Delete_PreservesSiblingStatementsAndAddRangeArrayElements).
		Assert.Contains("tabPage1", flushedAfterAdd, StringComparison.Ordinal);

		// TabControlDesigner's AddTabPage verb selects the page it just created, so "Remove Tab"
		// removes THIS one - re-fetch the list fresh (never cached between calls, per ListVerbs'
		// own doc comment) rather than reusing addTab's stale index.
		var verbsAfterAdd = await client.ListVerbsAsync(1, "tabControl1", timeout.Token);
		var removeTab = Assert.Single(verbsAfterAdd.Items, item => item.Text.Contains("Remove", StringComparison.OrdinalIgnoreCase)
			&& item.Text.Contains("Tab", StringComparison.OrdinalIgnoreCase));
		var afterRemove = await client.InvokeVerbAsync(1, "tabControl1", removeTab.Index, timeout.Token);
		Assert.True(afterRemove.Accepted);
		Assert.DoesNotContain(afterRemove.Components, component => component.Name == newPage.Name);

		var flushedAfterRemove = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.DoesNotContain(newPage.Name, flushedAfterRemove, StringComparison.Ordinal);
		Assert.Contains("tabPage1", flushedAfterRemove, StringComparison.Ordinal);
	}

	/// <summary>
	/// design/invoke-menu-command routes a <see cref="CommandID"/> through the child designer's own
	/// IMenuCommandService, so a declared menu item needs neither its own RPC nor its own branch on
	/// the parent side. The parent has no local IDesignerHost to invoke commands on -
	/// FormsDesignerViewContent.Host returns null by design - so before this RPC existed such a
	/// command either did nothing or dereferenced that null into an exception dialog.
	///
	/// **What this does NOT give us, and why the per-command RPCs stay.** A self-hosted
	/// DesignSurface exposes no IMenuCommandService at all, and registering one does not help:
	/// the registry starts empty, and the class that fills it with the StandardCommands set is
	/// `System.Windows.Forms.Design.ControlCommandSet`, which is **internal** - only Visual Studio's
	/// designer package constructs it. So align / size-to-grid / z-order / lock / tab-order can
	/// never route through here, and TryExecuteRemoteLayout's per-command branches are a necessity
	/// rather than debt. What this RPC does cover is every command a designer registers itself in
	/// Initialize - third-party ControlDesigners included - plus a clean, diagnosable rejection
	/// otherwise. Registering a MenuCommandService to change that is a trap: see
	/// InvokeMenuCommand's own doc comment, and note that ChildHost_Rename_UpdatesNamePropertyLiteralForToolStripItem
	/// is the test that fails when someone tries it.
	///
	/// Asserted here rather than through the menu because the menu is untestable: a WPF ContextMenu
	/// is its own top-level window, invisible to both of DevFlow's observation channels. The test
	/// drives the client RPC directly, which is exactly what the parent's TryInvokeRemoteMenuCommand
	/// does once its purpose-built branches decline.
	/// </summary>
	[Fact]
	public async Task ChildHost_InvokeMenuCommand_RejectsACommandNoDesignerRegistered()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, HostDll());

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.button1 = new System.Windows.Forms.Button();
						        this.button1.Name = "button1";
						        this.Controls.Add(this.button1);
						    }
						    private System.Windows.Forms.Button button1;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

		// A CommandID nothing registered must come back as an explicit rejection, not a silent
		// success: "it ran and had no visible effect" and "nothing handled it" have to be
		// distinguishable, because TryInvokeRemoteMenuCommand turns the latter into "fall through"
		// rather than reporting a failure to the user.
		var unknown = await Assert.ThrowsAnyAsync<Exception>(() =>
			client.InvokeMenuCommandAsync(1, Guid.NewGuid(), 12345, timeout.Token));
		Assert.Contains("command", unknown.Message, StringComparison.OrdinalIgnoreCase);

		// Same rejection for a real StandardCommand, and that is the POINT of this assertion rather
		// than a limitation being papered over: it pins the fact that WinForms' ControlCommandSet is
		// internal and unreachable from a self-hosted DesignSurface. If a future runtime ever does
		// supply these, this assertion fails - which is exactly when someone should revisit whether
		// TryExecuteRemoteLayout still needs its per-command branches.
		var standard = await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeMenuCommandAsync(1,
			StandardCommands.LockControls.Guid, StandardCommands.LockControls.ID, timeout.Token));
		Assert.Contains("command", standard.Message, StringComparison.OrdinalIgnoreCase);
	}
#endif

	/// <summary>
	/// Regression test for "Unsupported ToolStripItem type: System.Windows.Forms.ToolStripMenuItem":
	/// design/add-toolstrip-item's ResolveToolStripItemType only matched SHORT type names
	/// ("ToolStripMenuItem"), while DesignerComponentInfo.NewItemTypeNames - and therefore every
	/// itemTypeName a real client actually sends back from its own reported metadata (as the WPF
	/// popup Type Here editor does) - is fully qualified ("System.Windows.Forms.ToolStripMenuItem").
	/// Every other test in this file happens to pass a short name explicitly, which is why this
	/// gap went unnoticed; this test deliberately round-trips NewItemTypeNames' own value instead of
	/// a hand-picked short name, so a future case that narrows the short-name switch again cannot
	/// silently reintroduce the same bug.
	/// </summary>
	[Fact]
	public async Task ChildHost_AddToolStripItem_AcceptsTheFullyQualifiedTypeNameItReportsItself()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
						        this.menuStrip1.Name = "menuStrip1";
						        this.Controls.Add(this.menuStrip1);
						    }
						    private System.Windows.Forms.MenuStrip menuStrip1;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		var menuStrip = Assert.Single(opened.Components, component => component.Name == "menuStrip1");
		var typeName = menuStrip.NewItemTypeNames.First();
		Assert.Equal("System.Windows.Forms.ToolStripMenuItem", typeName);

		var withItem = await client.AddToolStripItemAsync(1, "menuStrip1", typeName, "", "fileToolStripMenuItem", timeout.Token);
		Assert.True(withItem.Accepted);
		Assert.Contains(withItem.Components, component => component.Name == "fileToolStripMenuItem"
			&& component.Type == "System.Windows.Forms.ToolStripMenuItem" && component.Parent == "menuStrip1");
#endif
	}

	/// <summary>
	/// Selecting a MenuStrip item expands its own (possibly empty) dropdown in the REAL designer -
	/// ToolStripMenuItemDesigner.OnSelectionChanged calls InitializeDropDown() unconditionally, see
	/// doc/technotes/winforms-designer.md's "Selection forwarding makes the REAL chrome render".
	/// DesignerSessionState.Popups should report that expanded dropdown as its own overlay, with
	/// TypeHereBounds pointing at the real template node's in-place-edit cell (FindTemplateNodeBounds
	/// in DesignerHostService.cs) - this is the geometry the WPF client's PopupTypeHereEditor anchors
	/// its own real TextBox overlay to.
	/// </summary>
	[Fact]
	public async Task ChildHost_SelectingMenuItem_ExpandsPopupWithTypeHereBounds()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
						        this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
						        this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.fileToolStripMenuItem });
						        this.menuStrip1.Name = "menuStrip1";
						        this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
						        this.fileToolStripMenuItem.Text = "&File";
						        this.Controls.Add(this.menuStrip1);
						    }
						    private System.Windows.Forms.MenuStrip menuStrip1;
						    private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		Assert.Empty(opened.Popups);

		var selected = await client.SetSelectionAsync(1, new[] { "fileToolStripMenuItem" }, timeout.Token);
		var popup = Assert.Single(selected.Popups, p => p.OwnerElementId == "fileToolStripMenuItem");
		Assert.NotNull(popup.TypeHereBounds);
		Assert.True(popup.TypeHereBounds!.Value.Width > 0);
		Assert.True(popup.TypeHereBounds!.Value.Height > 0);
		Assert.NotEmpty(popup.Render.PngBase64);

		// Deselecting collapses the dropdown again - Popups mirrors real designer state, not a
		// client-side cache the RPC could leave stale.
		var deselected = await client.SetSelectionAsync(1, Array.Empty<string>(), timeout.Token);
		Assert.Empty(deselected.Popups);
#else
		Assert.Empty(opened.Popups);
		var selected = await client.SetSelectionAsync(1, new[] { "fileToolStripMenuItem" }, timeout.Token);
		Assert.Empty(selected.Popups);
#endif
	}

	/// <summary>
	/// Clicking an item inside an expanded popup must select THAT item through the real
	/// ISelectionService without collapsing the dropdown - this is the fix for "clicking Type Here
	/// makes the popup disappear" (a container-membership gap in FindDeepest's Controls walk; see
	/// doc/technotes/winforms-designer.md). Regression-covers both halves: hitting a real, sited
	/// item selects it and keeps the popup open, and PopupHitElementId (which the client adopts as
	/// its own SelectedComponentName, since the RPC response is otherwise the only way it learns
	/// what was hit) reports the right name.
	/// </summary>
	[Fact]
	public async Task ChildHost_HitTestPopup_SelectsNestedItemWithoutClosingPopup()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
						        this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
						        this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.fileToolStripMenuItem });
						        this.menuStrip1.Name = "menuStrip1";
						        this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
						        this.fileToolStripMenuItem.Text = "&File";
						        this.Controls.Add(this.menuStrip1);
						    }
						    private System.Windows.Forms.MenuStrip menuStrip1;
						    private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		var beforeAdd = Assert.Single((await client.SetSelectionAsync(1, new[] { "fileToolStripMenuItem" }, timeout.Token))
			.Popups, p => p.OwnerElementId == "fileToolStripMenuItem");
		Assert.NotNull(beforeAdd.TypeHereBounds);

		// The added item occupies the space the template node previously reported BEFORE the add
		// (the template node itself shifts down to make room for it); its own bounds are not
		// reported directly (only the strip's own root Controls get SurfaceX/Y), so this hits the
		// item using the SAME anchor the manual DevFlow verification in this session used: the
		// template node's own pre-add position, a few pixels inside it.
		var itemX = beforeAdd.TypeHereBounds!.Value.X + 5;
		var itemY = beforeAdd.TypeHereBounds!.Value.Y + 5;
		await client.AddToolStripItemAsync(
			1, "menuStrip1", "System.Windows.Forms.ToolStripMenuItem", "fileToolStripMenuItem", "openToolStripMenuItem", timeout.Token);
		var hit = await client.HitTestPopupAsync(1, "fileToolStripMenuItem", itemX, itemY, timeout.Token);
		Assert.Equal("openToolStripMenuItem", hit.PopupHitElementId);
		// The popup is still open - hitting a real, sited item must not collapse it.
		Assert.Contains(hit.Popups, p => p.OwnerElementId == "fileToolStripMenuItem");

		// Clicking the (now-shifted-down) unsited Type Here cell itself must be a safe no-op:
		// no selection, and - critically - the popup stays open (the very bug this RPC exists to
		// fix: an earlier FindDeepest gap treated the in-place-edit TextBox as a real hit and closed
		// the dropdown when the real ISelectionService saw an unsited component).
		var afterAdd = Assert.Single(hit.Popups, p => p.OwnerElementId == "fileToolStripMenuItem");
		var typeHereHit = await client.HitTestPopupAsync(
			1, "fileToolStripMenuItem", afterAdd.TypeHereBounds!.Value.X + 5, afterAdd.TypeHereBounds!.Value.Y + 5, timeout.Token);
		Assert.True(String.IsNullOrEmpty(typeHereHit.PopupHitElementId));
		Assert.Contains(typeHereHit.Popups, p => p.OwnerElementId == "fileToolStripMenuItem");
#endif
	}

	/// <summary>
	/// ContextMenuStrip is never parented into the form's Controls - it lives only in the tray -
	/// so by default it must contribute no overlay at all (unlike the real ContextMenuStripDesigner,
	/// which shows it unconditionally once initialized; OpenDevelop deliberately narrows this to
	/// "shown only while its tray icon, or one of its own items, is selected", the same
	/// select-to-edit workflow as a MenuStrip's own submenu). Covers SelectedContextMenuStripPopups
	/// and design/hit-test-popup's ContextMenuStrip fallback (an owner id that names the strip
	/// itself directly, not an owning ToolStripDropDownItem) in DesignerHostService.cs.
	/// </summary>
	[Fact]
	public async Task ChildHost_ContextMenuStrip_OnlyOverlaysWhileSelected()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private System.ComponentModel.IContainer components;
						    private void InitializeComponent()
						    {
						        this.components = new System.ComponentModel.Container();
						        this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
						        this.openToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
						        this.contextMenuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.openToolStripMenuItem });
						        this.contextMenuStrip1.Name = "contextMenuStrip1";
						        this.openToolStripMenuItem.Name = "openToolStripMenuItem";
						        this.openToolStripMenuItem.Text = "Open";
						    }
						    private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
						    private System.Windows.Forms.ToolStripMenuItem openToolStripMenuItem;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		// Hidden by default: it is tray-only, and nothing has selected it yet.
		Assert.Empty(opened.Popups);
#if MICROSOFT_FORMS_DESIGNER_HOST
		// IsTrayComponent's designer-kind clauses (see IsTrayComponent's own doc comment) are a
		// Microsoft-only capability - the portable LibreWinForms fork can only ever tell "not a
		// Control at all" apart, which is covered by ChildHost_ReportsTrayComponentsByDesignerKindNotJustControlness.
		Assert.Contains(opened.Components, component => component.Name == "contextMenuStrip1" && component.IsTrayComponent);
#endif

#if MICROSOFT_FORMS_DESIGNER_HOST
		// Selecting the tray icon itself expands its own dropdown as an overlay.
		var selected = await client.SetSelectionAsync(1, new[] { "contextMenuStrip1" }, timeout.Token);
		var popup = Assert.Single(selected.Popups, p => p.OwnerElementId == "contextMenuStrip1");
		Assert.NotNull(popup.TypeHereBounds);

		// Selecting one of its OWN items (not the strip) must keep the same overlay open - the
		// same "selecting a leaf item still shows its owning dropdown" behavior real VS has for
		// MenuStrip submenus (see doc/technotes/winforms-designer.md).
		var itemSelected = await client.SetSelectionAsync(1, new[] { "openToolStripMenuItem" }, timeout.Token);
		Assert.Contains(itemSelected.Popups, p => p.OwnerElementId == "contextMenuStrip1");

		// Hit-testing works against the strip's own element id directly (it has no owning
		// ToolStripDropDownItem - "OwnerElementId" IS the ContextMenuStrip itself).
		var hit = await client.HitTestPopupAsync(1, "contextMenuStrip1", popup.TypeHereBounds!.Value.X - 5,
			popup.TypeHereBounds!.Value.Y - popup.TypeHereBounds!.Value.Height / 2, timeout.Token);
		Assert.Equal("openToolStripMenuItem", hit.PopupHitElementId);

		// Deselecting entirely collapses it again.
		var deselected = await client.SetSelectionAsync(1, Array.Empty<string>(), timeout.Token);
		Assert.Empty(deselected.Popups);
#else
		var selected = await client.SetSelectionAsync(1, new[] { "contextMenuStrip1" }, timeout.Token);
		Assert.Empty(selected.Popups);
#endif
	}

	/// <summary>
	/// Double-clicking a ToolStripItem (the WPF client's DefaultEventRequested, wired to
	/// design/activate-default-event) must generate and wire up its default event handler exactly
	/// like it already does for an ordinary Control - ToolStripItem's own real
	/// `[DefaultEvent("Click")]` flows through the SAME generic TypeDescriptor-based code path
	/// (ActivateDefaultEvent never special-cases Control), so this is a regression test for that
	/// genericity rather than new functionality - confirmed working with no code changes needed by
	/// a live DevFlow double-click on tests/fixtures/ToolStripFixture's own toolStripButton1 before
	/// this test was added.
	/// </summary>
	[Fact]
	public async Task ChildHost_ActivateDefaultEvent_WiresUpClickHandlerForToolStripItem()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.toolStrip1 = new System.Windows.Forms.ToolStrip();
						        this.toolStripButton1 = new System.Windows.Forms.ToolStripButton();
						        this.toolStrip1.Items.Add(this.toolStripButton1);
						        this.toolStrip1.Name = "toolStrip1";
						        this.toolStripButton1.Name = "toolStripButton1";
						        this.Controls.Add(this.toolStrip1);
						    }
						    private System.Windows.Forms.ToolStrip toolStrip1;
						    private System.Windows.Forms.ToolStripButton toolStripButton1;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		var button = Assert.Single(opened.Components, component => component.Name == "toolStripButton1");
		Assert.Contains(button.Events, item => item.Name == "Click" && String.IsNullOrEmpty(item.Handler));

		var activated = await client.ActivateDefaultEventAsync(1, "toolStripButton1", timeout.Token);
		Assert.True(activated.Accepted);
		Assert.Contains(activated.Components.Single(item => item.Name == "toolStripButton1").Events,
			item => item.Name == "Click" && item.Handler == "toolStripButton1_Click");

		var flushed = await client.FlushAsync(1, timeout.Token);
		Assert.Contains("toolStripButton1.Click += toolStripButton1_Click;",
			flushed.Files.Single(item => item.Kind == "Designer").Text, StringComparison.Ordinal);
		Assert.Contains("private void toolStripButton1_Click(System.Object sender, System.EventArgs e)",
			flushed.Files.Single(item => item.Kind == "Source").Text, StringComparison.Ordinal);
	}

	/// <summary>
	/// Regression test for a Name-property-literal gap: design/rename's RewriteComponentName call
	/// renames every IDENTIFIER reference generically (works for any component), but the separate
	/// "elementId.Name = "oldName";" statement's STRING LITERAL argument is a different concern -
	/// RenameComponent only ever refreshed it via RewriteProperty for `component is Control`,
	/// leaving a ToolStripItem's own real Name property (a plain public property, same shape as
	/// Control.Name) holding the stale old name as a literal after every identifier had already
	/// moved to the new one.
	/// </summary>
	[Fact]
	public async Task ChildHost_Rename_UpdatesNamePropertyLiteralForToolStripItem()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.toolStrip1 = new System.Windows.Forms.ToolStrip();
					        this.toolStripButton1 = new System.Windows.Forms.ToolStripButton();
					        this.toolStrip1.Items.Add(this.toolStripButton1);
					        this.toolStrip1.Name = "toolStrip1";
					        this.toolStripButton1.Name = "toolStripButton1";
					        this.Controls.Add(this.toolStrip1);
					    }
					    private System.Windows.Forms.ToolStrip toolStrip1;
					    private System.Windows.Forms.ToolStripButton toolStripButton1;
					}
					"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

		var renamed = await client.RenameAsync(1, "toolStripButton1", "renamedButton", timeout.Token);
		Assert.True(renamed.Accepted);
		Assert.Contains(renamed.Components, component => component.Name == "renamedButton");
		Assert.Contains("renamedButton.Name = \"renamedButton\";",
			DesignerText(await client.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
	}

	/// <summary>
	/// Regression test for two compounding bugs in design/delete-elements' designer-source rewrite
	/// (DesignerHostService.RewriteDeletedComponent), both found while adding ToolStripItem
	/// support but NEITHER actually specific to ToolStripItem - both are general, pre-existing
	/// defects that would affect deleting ANY component whose designer source happens to share a
	/// statement or an AddRange array with another component:
	///
	/// 1. A `{ ... }` method body (InitializeComponent's own included) IS a StatementSyntax/
	///    MethodBlockSyntax in Roslyn's C#/VB model, so the naive "remove every StatementSyntax
	///    that mentions the deleted identifier" walk always matched the WHOLE METHOD BODY too
	///    (since it mentions the identifier somewhere by construction) - and RemoveNodes drops an
	///    ancestor before its own now-redundant descendants, silently wiping every OTHER
	///    component's statements along with the deleted one's.
	/// 2. A deleted item that is one of several elements in a single shared
	///    "collection.AddRange(new T[] { a, b, c })" call lost the WHOLE STATEMENT (every sibling
	///    in that same array along with it), rather than just its own array element.
	///
	/// This test's ToolStrip has two items declared via ONE shared AddRange (triggering bug 2) so
	/// that deleting one exercises both fixes at once: if bug 1 regressed, toolStrip1's own
	/// unrelated statements (and the WHOLE method body) would vanish too; if bug 2 regressed, the
	/// surviving sibling would disappear from the AddRange array as well.
	/// </summary>
	[Fact]
	public async Task ChildHost_Delete_PreservesSiblingStatementsAndAddRangeArrayElements()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.toolStrip1 = new System.Windows.Forms.ToolStrip();
					        this.toolStripButton1 = new System.Windows.Forms.ToolStripButton();
					        this.toolStripButton2 = new System.Windows.Forms.ToolStripButton();
					        this.toolStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.toolStripButton1, this.toolStripButton2 });
					        this.toolStrip1.Name = "toolStrip1";
					        this.toolStripButton1.Name = "toolStripButton1";
					        this.toolStripButton2.Name = "toolStripButton2";
					        this.Controls.Add(this.toolStrip1);
					    }
					    private System.Windows.Forms.ToolStrip toolStrip1;
					    private System.Windows.Forms.ToolStripButton toolStripButton1;
					    private System.Windows.Forms.ToolStripButton toolStripButton2;
					}
					"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

		var deleted = await client.DeleteElementsAsync(1, new[] { "toolStripButton2" }, timeout.Token);
		Assert.True(deleted.Accepted);
		Assert.DoesNotContain(deleted.Components, component => component.Name == "toolStripButton2");
		Assert.Contains(deleted.Components, component => component.Name == "toolStrip1");
		Assert.Contains(deleted.Components, component => component.Name == "toolStripButton1");

		var flushed = DesignerText(await client.FlushAsync(1, timeout.Token));
		// Bug 2's regression signature: the whole AddRange statement gone, taking
		// toolStripButton1 with it.
		Assert.Contains("toolStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { toolStripButton1 })",
			flushed, StringComparison.Ordinal);
		// Bug 1's regression signature: every OTHER statement in InitializeComponent gone too.
		Assert.Contains("toolStrip1.Name = \"toolStrip1\";", flushed, StringComparison.Ordinal);
		Assert.Contains("toolStripButton1.Name = \"toolStripButton1\";", flushed, StringComparison.Ordinal);
		Assert.Contains("Controls.Add(toolStrip1);", flushed, StringComparison.Ordinal);
	}

	/// <summary>
	/// Drag-to-reorder on a ToolStrip whose items are declared as separate consecutive
	/// "toolStrip1.Items.Add(x)" statements (the shape ChildHost_SupportsSmartTagActionsAndToolStripItemInsertion's
	/// own AddToolStripItemAsync calls produce, and one of the two declaration shapes real designer
	/// output uses). Moving the LAST item to the front must update both the live ToolStripItemCollection
	/// order (so hit-testing/rendering reflect it immediately) and the designer source's own statement
	/// order (so Flush - and a subsequent reopen - round-trips it), covering
	/// DesignerHostService.ReorderToolStripItem/RewriteReorderedToolStripItems.
	/// </summary>
	[Fact]
	public async Task ChildHost_ReorderToolStripItem_MovesItemAndRewritesAddStatementOrder()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.toolStrip1 = new System.Windows.Forms.ToolStrip();
						        this.toolStripButton1 = new System.Windows.Forms.ToolStripButton();
						        this.toolStripButton2 = new System.Windows.Forms.ToolStripButton();
						        this.toolStripButton3 = new System.Windows.Forms.ToolStripButton();
						        this.toolStrip1.Items.Add(this.toolStripButton1);
						        this.toolStrip1.Items.Add(this.toolStripButton2);
						        this.toolStrip1.Items.Add(this.toolStripButton3);
						        this.toolStrip1.Name = "toolStrip1";
						        this.toolStripButton1.Name = "toolStripButton1";
						        this.toolStripButton2.Name = "toolStripButton2";
						        this.toolStripButton3.Name = "toolStripButton3";
						        this.Controls.Add(this.toolStrip1);
						    }
						    private System.Windows.Forms.ToolStrip toolStrip1;
						    private System.Windows.Forms.ToolStripButton toolStripButton1;
						    private System.Windows.Forms.ToolStripButton toolStripButton2;
						    private System.Windows.Forms.ToolStripButton toolStripButton3;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		// Drag toolStripButton3 (last) to index 0 (front).
		var reordered = await client.ReorderToolStripItemAsync(1, "toolStripButton3", 0, timeout.Token);
		Assert.True(reordered.Accepted);
		var toolStrip = Assert.Single(reordered.Components, component => component.Name == "toolStrip1");
		// The live model now reports children in the new order - Parent/Index bookkeeping is
		// exercised by BuildElementTree's own existing tests; here it is the ORDER among siblings
		// that must have moved, verified via the flushed source below.

		var flushed = DesignerText(await client.FlushAsync(1, timeout.Token));
		var button1Index = flushed.IndexOf("toolStrip1.Items.Add(toolStripButton1)", StringComparison.Ordinal);
		var button2Index = flushed.IndexOf("toolStrip1.Items.Add(toolStripButton2)", StringComparison.Ordinal);
		var button3Index = flushed.IndexOf("toolStrip1.Items.Add(toolStripButton3)", StringComparison.Ordinal);
		Assert.True(button3Index >= 0 && button1Index >= 0 && button2Index >= 0);
		Assert.True(button3Index < button1Index, "toolStripButton3's Add statement should now come first");
		Assert.True(button1Index < button2Index, "toolStripButton1 and toolStripButton2 should keep their relative order");

		// Moving it back to the end (index 2, since Remove already shifted the collection down to
		// 2 remaining items before the Insert) restores the original statement order.
		var restored = await client.ReorderToolStripItemAsync(1, "toolStripButton3", 2, timeout.Token);
		Assert.True(restored.Accepted);
		var restoredFlushed = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.True(restoredFlushed.IndexOf("toolStrip1.Items.Add(toolStripButton1)", StringComparison.Ordinal)
			< restoredFlushed.IndexOf("toolStrip1.Items.Add(toolStripButton3)", StringComparison.Ordinal));
#else
		await Assert.ThrowsAnyAsync<Exception>(() =>
			client.ReorderToolStripItemAsync(1, "toolStripButton1", 0, timeout.Token));
#endif
	}

	/// <summary>
	/// Same drag-to-reorder RPC, exercised against the OTHER declaration shape real designer output
	/// uses - a StatusStrip whose items are declared via a single
	/// "statusStrip1.Items.AddRange(new ToolStripItem[] { a, b })" call (see
	/// ChildHost_AddRangeAndBareControlsAddPopulateComponentTree for the MenuStrip equivalent of this
	/// shape). RewriteReorderedToolStripItems must reorder the ARRAY's elements in place rather than
	/// looking for separate Add() statements, which do not exist here.
	/// </summary>
	[Fact]
	public async Task ChildHost_ReorderToolStripItem_ReordersAddRangeArrayForStatusStrip()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var snapshot = new DesignerDocumentSnapshot {
			Version = 1,
			PrimaryFileName = "/project/Form1.cs",
			DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.cs", Kind = "Source",
					Text = "namespace Sample; partial class Form1 { }"
				},
				new DesignerSourceFileSnapshot {
					FileName = "/project/Form1.Designer.cs", Kind = "Designer",
					Text = """
						namespace Sample;
						partial class Form1
						{
						    private void InitializeComponent()
						    {
						        this.statusStrip1 = new System.Windows.Forms.StatusStrip();
						        this.statusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
						        this.progressBar1 = new System.Windows.Forms.ToolStripProgressBar();
						        this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.statusLabel1, this.progressBar1 });
						        this.statusStrip1.Name = "statusStrip1";
						        this.statusLabel1.Name = "statusLabel1";
						        this.progressBar1.Name = "progressBar1";
						        this.Controls.Add(this.statusStrip1);
						    }
						    private System.Windows.Forms.StatusStrip statusStrip1;
						    private System.Windows.Forms.ToolStripStatusLabel statusLabel1;
						    private System.Windows.Forms.ToolStripProgressBar progressBar1;
						}
						"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		// Drag progressBar1 (second) in front of statusLabel1.
		var reordered = await client.ReorderToolStripItemAsync(1, "progressBar1", 0, timeout.Token);
		Assert.True(reordered.Accepted);

		var flushed = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.Contains("new System.Windows.Forms.ToolStripItem[] { progressBar1, statusLabel1 }", flushed, StringComparison.Ordinal);
#else
		await Assert.ThrowsAnyAsync<Exception>(() =>
			client.ReorderToolStripItemAsync(1, "statusLabel1", 0, timeout.Token));
#endif
	}

	/// <summary>
	/// The reorder RPC works the same for an item inside a POPUP's own DropDownItems collection
	/// (not just a root strip's Items) - it resolves the real owning collection from the dragged
	/// item's own live Owner/OwnerItem, so this needed no special-casing; this test's own purpose
	/// is regression coverage, since every other reorder test only exercises a root strip's Items.
	/// Also confirms a popup item's own SurfaceX/Y/Width/Height (which the WPF client's vertical
	/// popupReorderThumb relies on to compute a drop target - see
	/// RemoteFormsDesignerControl.OnPopupReorderDragCompleted) survive the reorder and correctly
	/// swap, mirroring the client-side computation without needing a live WPF control to test it.
	/// </summary>
	[Fact]
	public async Task ChildHost_ReorderToolStripItem_WorksForItemsInsideAnOpenPopup()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.menuStrip1 = new System.Windows.Forms.MenuStrip();
					        this.fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
					        this.openToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
					        this.exitToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
					        this.menuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { this.fileToolStripMenuItem });
					        this.fileToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] { this.openToolStripMenuItem, this.exitToolStripMenuItem });
					        this.menuStrip1.Name = "menuStrip1";
					        this.fileToolStripMenuItem.Name = "fileToolStripMenuItem";
					        this.openToolStripMenuItem.Name = "openToolStripMenuItem";
					        this.exitToolStripMenuItem.Name = "exitToolStripMenuItem";
					        this.Controls.Add(this.menuStrip1);
					    }
					    private System.Windows.Forms.MenuStrip menuStrip1;
					    private System.Windows.Forms.ToolStripMenuItem fileToolStripMenuItem;
					    private System.Windows.Forms.ToolStripMenuItem openToolStripMenuItem;
					    private System.Windows.Forms.ToolStripMenuItem exitToolStripMenuItem;
					}
					"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		// Expand the dropdown (selection forwarding activates the real designer chrome) so the
		// items report live surface bounds, the same precondition the WPF client's own gesture has.
		var selected = await client.SetSelectionAsync(1, new[] { "fileToolStripMenuItem" }, timeout.Token);
		var openBefore = selected.Components.Single(item => item.Name == "openToolStripMenuItem");
		var exitBefore = selected.Components.Single(item => item.Name == "exitToolStripMenuItem");
		Assert.True(openBefore.SurfaceY < exitBefore.SurfaceY, "openToolStripMenuItem should start above exitToolStripMenuItem");

		// Drag exitToolStripMenuItem (second) above openToolStripMenuItem.
		var reordered = await client.ReorderToolStripItemAsync(1, "exitToolStripMenuItem", 0, timeout.Token);
		Assert.True(reordered.Accepted);
		var exitAfter = reordered.Components.Single(item => item.Name == "exitToolStripMenuItem");
		var openAfter = reordered.Components.Single(item => item.Name == "openToolStripMenuItem");
		Assert.True(exitAfter.SurfaceY < openAfter.SurfaceY, "exitToolStripMenuItem should now be above openToolStripMenuItem");

		Assert.Contains("fileToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] { exitToolStripMenuItem, openToolStripMenuItem });",
			DesignerText(await client.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
#else
		await Assert.ThrowsAnyAsync<Exception>(() =>
			client.ReorderToolStripItemAsync(1, "exitToolStripMenuItem", 0, timeout.Token));
#endif
	}

	/// <summary>
	/// TabControl support: (1) each tab HEADER's own rect is reported (a header is not a component
	/// of its own - nothing else could report its geometry - see DesignerComponentInfo.
	/// TabHeaderBounds's own doc comment), Microsoft-only since LibreWinForms does not implement
	/// TabControl.GetTabRect/TabCount; (2) design/select-tab switches the real SelectedTab, which
	/// is why button1 (on tabPage1) and button2 (on tabPage2, deliberately given the SAME bounds as
	/// button1 to prove this is a visibility distinction, not a coordinate one) are hit-testable
	/// one at a time depending on which page is currently active - matching how a real WinForms
	/// TabControl only ever lays out/shows its SelectedTab's children; (3) unlike design/set-
	/// property, select-tab must not persist (no "tabControl1.SelectedIndex = ...;" line - real VS
	/// does not either) or count as an undo step (canUndo stays false).
	/// </summary>
	[Fact]
	public async Task ChildHost_SelectTab_SwitchesActivePageWithoutPersistingOrCreatingAnUndoStep()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.tabControl1 = new System.Windows.Forms.TabControl();
					        this.tabPage1 = new System.Windows.Forms.TabPage();
					        this.tabPage2 = new System.Windows.Forms.TabPage();
					        this.button1 = new System.Windows.Forms.Button();
					        this.button2 = new System.Windows.Forms.Button();
					        this.tabControl1.Controls.Add(this.tabPage1);
					        this.tabControl1.Controls.Add(this.tabPage2);
					        this.tabControl1.Name = "tabControl1";
					        this.tabControl1.SelectedIndex = 0;
					        this.tabControl1.Size = new System.Drawing.Size(300, 200);
					        this.tabPage1.Controls.Add(this.button1);
					        this.tabPage1.Name = "tabPage1";
					        this.tabPage1.Text = "Tab 1";
					        this.tabPage2.Controls.Add(this.button2);
					        this.tabPage2.Name = "tabPage2";
					        this.tabPage2.Text = "Tab 2";
					        this.button1.Location = new System.Drawing.Point(10, 10);
					        this.button1.Name = "button1";
					        this.button1.Size = new System.Drawing.Size(75, 23);
					        this.button2.Location = new System.Drawing.Point(10, 10);
					        this.button2.Name = "button2";
					        this.button2.Size = new System.Drawing.Size(75, 23);
					        this.Controls.Add(this.tabControl1);
					    }
					    private System.Windows.Forms.TabControl tabControl1;
					    private System.Windows.Forms.TabPage tabPage1;
					    private System.Windows.Forms.TabPage tabPage2;
					    private System.Windows.Forms.Button button1;
					    private System.Windows.Forms.Button button2;
					}
					"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		var button1 = opened.Components.Single(item => item.Name == "button1");

#if MICROSOFT_FORMS_DESIGNER_HOST
		var tabControl = opened.Components.Single(item => item.Name == "tabControl1");
		Assert.Equal(2, tabControl.TabHeaderBounds.Count);
		Assert.True(tabControl.TabHeaderBounds[0].Width > 0 && tabControl.TabHeaderBounds[0].Height > 0);
		// Both headers sit on the same row, left to right.
		Assert.True(tabControl.TabHeaderBounds[1].X > tabControl.TabHeaderBounds[0].X);

		// tabPage1 (index 0) is active: button1 hit-testable, button2 (same bounds, tabPage2) is not.
		var hitOnPage1 = await client.HitTestAsync(1, button1.SurfaceX + 8, button1.SurfaceY + 5, timeout.Token);
		Assert.Equal("button1", hitOnPage1.ComponentName);

		// IsVisible must distinguish the active page's children from the hidden page's. Both pages
		// occupy the SAME rect, so button1 and button2 report overlapping SurfaceX/Y - without this
		// flag the client cannot tell which of two components at the same spot is actually on
		// screen, and drew outlines/name tags for BOTH (phantom overlays that read as "the wrong
		// page is being rendered", and swallowed clicks aimed at what looked like a control).
		// See DesignerComponentInfo.IsVisible.
		Assert.True(opened.Components.Single(item => item.Name == "tabPage1").IsVisible);
		Assert.True(opened.Components.Single(item => item.Name == "button1").IsVisible);
		Assert.False(opened.Components.Single(item => item.Name == "tabPage2").IsVisible);
		Assert.False(opened.Components.Single(item => item.Name == "button2").IsVisible);

		var switched = await client.SelectTabAsync(1, "tabControl1", 1, timeout.Token);
		Assert.True(switched.Accepted);
		Assert.False(switched.CanUndo);
		var button2 = switched.Components.Single(item => item.Name == "button2");
		var hitOnPage2 = await client.HitTestAsync(1, button2.SurfaceX + 8, button2.SurfaceY + 5, timeout.Token);
		Assert.Equal("button2", hitOnPage2.ComponentName);

		// ...and it flips with the active page, so the client's overlays follow the tab switch.
		Assert.False(switched.Components.Single(item => item.Name == "tabPage1").IsVisible);
		Assert.False(switched.Components.Single(item => item.Name == "button1").IsVisible);
		Assert.True(switched.Components.Single(item => item.Name == "tabPage2").IsVisible);
		Assert.True(button2.IsVisible);
		// The root form and a plain top-level control are always reported visible - a regression
		// making IsVisible false by default would silently hide EVERY overlay in the designer.
		Assert.True(switched.Components.Single(item => item.Name == "tabControl1").IsVisible);

		// Switching back and forth is pure view state: no designer-source line, no undo step.
		Assert.DoesNotContain("SelectedIndex = 1", DesignerText(await client.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
#else
		Assert.Empty(opened.Components.Single(item => item.Name == "tabControl1").TabHeaderBounds);
#endif
	}

	[Fact]
	public async Task ChildHost_BoundsSnapshotsAndSupportsIndependentLifetimes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		var first = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var second = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var firstPid = first.ProcessId;
		try {
			Assert.NotEqual(first.ProcessId, second.ProcessId);
			Assert.NotEqual(first.SessionId, second.SessionId);
			Assert.True((await first.OpenAsync(Snapshot(1, "first"), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Snapshot(1, "second"), timeout.Token)).Accepted);
			var oversized = Snapshot(2, "oversized");
			for (var index = oversized.Files.Count; index <= 256; index++)
				oversized.Files.Add(new DesignerSourceFileSnapshot { FileName = $"/project/{index}.cs", Text = " " });
			await Assert.ThrowsAnyAsync<Exception>(() => first.UpdateAsync(oversized, timeout.Token));
			Assert.True(first.IsAlive);
			first.Dispose();
			await WaitForExitAsync(firstPid, timeout.Token);
			Assert.True(second.IsAlive);
			Assert.Contains((await second.FlushAsync(1, timeout.Token)).Files,
				item => item.Kind == "Designer" && item.Text.Contains("second", StringComparison.Ordinal));
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	[Fact]
	public async Task SharedHost_UsesOneProcessAndKeepsDocumentsIsolated()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		var first = await FormsDesignerHostClient.AcquireSharedAsync("", "", timeout.Token, hostDll);
		var second = await FormsDesignerHostClient.AcquireSharedAsync("", "", timeout.Token, hostDll);
		try {
			Assert.Equal(first.ProcessId, second.ProcessId);
			Assert.Equal(first.SessionId, second.SessionId);
			Assert.NotEqual(first.DocumentId, second.DocumentId);
			Assert.True((await first.OpenAsync(Snapshot(1, "first"), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Snapshot(1, "second"), timeout.Token)).Accepted);
			await first.SetPropertyAsync(1, "button1", "Text", "first edited", timeout.Token);
			Assert.Contains("first edited", DesignerText(await first.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
			Assert.Contains("second", DesignerText(await second.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
			Assert.DoesNotContain("first edited", DesignerText(await second.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
			first.Dispose();
			Assert.True(second.IsAlive);
			Assert.Contains("second", DesignerText(await second.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	[Fact]
	public async Task SharedHost_RecoversEveryOpenDocumentAfterTheChildExits()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
		var hostDll = HostDll();
		var first = await FormsDesignerHostClient.AcquireSharedAsync("", "", timeout.Token, hostDll);
		var second = await FormsDesignerHostClient.AcquireSharedAsync("", "", timeout.Token, hostDll);
		try {
			Assert.True((await first.OpenAsync(Snapshot(1, "first"), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Snapshot(1, "second"), timeout.Token)).Accepted);
			var oldPid = first.ProcessId;
			var firstRecovered = new TaskCompletionSource<DesignerSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
			var secondRecovered = new TaskCompletionSource<DesignerSessionState>(TaskCreationOptions.RunContinuationsAsynchronously);
			first.Recovered += (_, state) => firstRecovered.TrySetResult(state);
			second.Recovered += (_, state) => secondRecovered.TrySetResult(state);

			first.TerminateHost();
			Assert.True((await firstRecovered.Task.WaitAsync(timeout.Token)).Accepted);
			Assert.True((await secondRecovered.Task.WaitAsync(timeout.Token)).Accepted);
			Assert.NotEqual(oldPid, first.ProcessId);
			Assert.Equal(first.ProcessId, second.ProcessId);
			Assert.Equal(1, first.RecoveryCount);
			Assert.Equal(1, second.RecoveryCount);
			Assert.Contains("first", DesignerText(await first.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
			Assert.Contains("second", DesignerText(await second.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	[Fact]
	public async Task SharedHost_RpcTimeoutTerminatesTheChildAndRecoversEveryOpenDocument()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
		var hostDll = HostDll();
		var first = await FormsDesignerHostClient.AcquireSharedAsync("", "", timeout.Token, hostDll);
		var second = await FormsDesignerHostClient.AcquireSharedAsync("", "", timeout.Token, hostDll);
		try {
			Assert.True((await first.OpenAsync(Snapshot(1, "first timeout"), timeout.Token)).Accepted);
			Assert.True((await second.OpenAsync(Snapshot(1, "second timeout"), timeout.Token)).Accepted);
			var oldPid = first.ProcessId;

			var error = await Assert.ThrowsAsync<TimeoutException>(() => first.DelayAsync(5000, timeout.Token));
			Assert.Contains("diagnostics/delay", error.Message, StringComparison.Ordinal);
			await WaitUntilAsync(() => first.RecoveryCount > 0 && second.RecoveryCount > 0
				&& first.IsAlive && second.IsAlive && first.ProcessId != oldPid, timeout.Token);

			Assert.Equal(first.ProcessId, second.ProcessId);
			Assert.Contains("first timeout", DesignerText(await first.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
			Assert.Contains("second timeout", DesignerText(await second.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
		} finally {
			first.Dispose();
			second.Dispose();
		}
	}

	static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
	{
		while (true) {
			try {
				if (System.Diagnostics.Process.GetProcessById(processId).HasExited) return;
			} catch (ArgumentException) { return; }
			await Task.Delay(25, cancellationToken);
		}
	}

	static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
	{
		using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		wait.CancelAfter(TimeSpan.FromSeconds(30));
		while (!condition())
			await Task.Delay(25, wait.Token);
	}

	static string DesignerText(DesignerEditSet edits) => edits.Files.Single(item => item.Kind == "Designer").Text;

	static string FramePayload(DesignerRenderFrame frame) => String.IsNullOrEmpty(frame.Data) ? frame.PngBase64 : frame.Data;

	[Fact]
	public async Task ChildHost_VbSnapshot_RoundTripsDesignerEdits()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);

		var opened = await client.OpenAsync(VbSnapshot(7, "button1"), timeout.Token);
		Assert.True(opened.Accepted);
		Assert.Equal("System.Windows.Forms.Form", opened.RootType);
		Assert.True(opened.ComponentCount >= 1);
		Assert.NotNull(opened.Render);
		Assert.Contains(opened.Components, component => component.Name == "button1"
			&& component.Type == "System.Windows.Forms.Button"
			&& component.Parent == "Form1"
			&& component.Text == "button1"
			&& component.X == 12 && component.Y == 20
			&& component.Width == 90 && component.Height == 30);
		Assert.Contains(opened.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Text" && property.TypeName == "System.String");
		var openedButton = Assert.Single(opened.Components, component => component.Name == "button1");
		Assert.Contains(openedButton.Events, item => item.Name == "Click" && item.HandlerTypeName == "System.EventHandler");

		// Flush normalizes the VB Me. qualifiers away, like the C# this. pass.
		Assert.Contains("button1.Text = \"button1\"", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);

		var edited = await client.SetPropertyAsync(7, "button1", "Text", "edited in child", timeout.Token);
		Assert.True(edited.Accepted);
		Assert.Contains(edited.Components, component => component.Name == "button1" && component.Text == "edited in child");
		var editedFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("button1.Text = \"edited in child\"", DesignerText(editedFiles), StringComparison.Ordinal);
		Assert.DoesNotContain("Me.", DesignerText(editedFiles), StringComparison.Ordinal);
		var disabled = await client.SetPropertyAsync(7, "button1", "Enabled", "False", timeout.Token);
		Assert.Contains(disabled.Components.Single(component => component.Name == "button1").Properties,
			property => property.Name == "Enabled" && property.Value.Equals("False", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("button1.Enabled = False", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);

		var moved = await client.SetBoundsAsync(7, "button1", 40, 50, 120, 35, timeout.Token);
		Assert.Contains(moved.Components, component => component.Name == "button1"
			&& component.X == 40 && component.Y == 50 && component.Width == 120 && component.Height == 35);
		var movedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("button1.Location = New System.Drawing.Point(40, 50)", movedSource, StringComparison.Ordinal);
		Assert.Contains("button1.Size = New System.Drawing.Size(120, 35)", movedSource, StringComparison.Ordinal);

		var eventBound = await client.SetEventAsync(7, "button1", "Click", "button1_Click", timeout.Token);
		Assert.Contains(eventBound.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "button1_Click");
		var eventFiles = await client.FlushAsync(7, timeout.Token);
		Assert.Contains("AddHandler button1.Click, AddressOf button1_Click",
			eventFiles.Files.Single(item => item.Kind == "Designer").Text, StringComparison.Ordinal);
		Assert.Contains("Private Sub button1_Click(sender As System.Object, e As System.EventArgs)",
			eventFiles.Files.Single(item => item.Kind == "Source").Text, StringComparison.Ordinal);
		var eventCleared = await client.SetEventAsync(7, "button1", "Click", "", timeout.Token);
		Assert.Contains(eventCleared.Components.Single(item => item.Name == "button1").Events,
			item => item.Name == "Click" && item.Handler == "");
		Assert.DoesNotContain("AddHandler button1.Click",
			DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var added = await client.AddElementAsync(7, "Form1", new DesignerToolboxItemInfo { TypeName = "System.Windows.Forms.Label" }, "label1", 30, 70, timeout.Token);
		Assert.Contains(added.Components, component => component.Name == "label1"
			&& component.Type == "System.Windows.Forms.Label" && component.Parent == "Form1"
			&& component.X == 30 && component.Y == 70);
		var addedSource = DesignerText(await client.FlushAsync(7, timeout.Token));
		Assert.Contains("label1 = New System.Windows.Forms.Label()", addedSource, StringComparison.Ordinal);
		Assert.Contains("Controls.Add(label1)", addedSource, StringComparison.Ordinal);
		Assert.Contains("Friend WithEvents label1 As System.Windows.Forms.Label", addedSource, StringComparison.Ordinal);
		Assert.Contains("label1.Size = New System.Drawing.Size(", addedSource, StringComparison.Ordinal);
		var labeled = await client.SetPropertyAsync(7, "label1", "Text", "new label", timeout.Token);
		Assert.Contains(labeled.Components, component => component.Name == "label1" && component.Text == "new label");
		Assert.Contains("label1.Text = \"new label\"", DesignerText(await client.FlushAsync(7, timeout.Token)),
			StringComparison.Ordinal);

		var deleted = await client.DeleteElementsAsync(7, new[] { "label1" }, timeout.Token);
		Assert.True(deleted.Accepted);
		Assert.DoesNotContain(deleted.Components, component => component.Name == "label1");
		Assert.DoesNotContain("label1", DesignerText(await client.FlushAsync(7, timeout.Token)), StringComparison.Ordinal);

		var stale = await client.UpdateAsync(VbSnapshot(7, "stale"), timeout.Token);
		Assert.False(stale.Accepted);
		Assert.Contains("Stale", stale.Error, StringComparison.Ordinal);
	}

	/// <summary>
	/// Edge behaviour of the TabControl RPCs added alongside tab support - the cases a client can
	/// reach by mis-aiming a click or replaying a stale index, none of which may take the child
	/// process down (it is shared by every open designer document in the session).
	///
	/// design/select-tab is deliberately FORGIVING rather than throwing: the client derives its
	/// tabIndex from TabHeaderBounds captured in an earlier frame, so a click racing a tab
	/// add/remove can legitimately arrive with an index that no longer exists. It must be a no-op,
	/// not an error dialog. design/invoke-verb is deliberately STRICT: its index comes from a
	/// list-verbs response the caller just made, so a bad one is a caller bug worth surfacing.
	/// </summary>
	[Fact]
	public async Task ChildHost_TabRpcs_ToleratePlausiblyStaleInputWithoutFaultingTheHost()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.tabControl1 = new System.Windows.Forms.TabControl();
					        this.tabPage1 = new System.Windows.Forms.TabPage();
					        this.tabPage2 = new System.Windows.Forms.TabPage();
					        this.button1 = new System.Windows.Forms.Button();
					        this.tabControl1.Controls.Add(this.tabPage1);
					        this.tabControl1.Controls.Add(this.tabPage2);
					        this.tabControl1.Name = "tabControl1";
					        this.tabControl1.SelectedIndex = 0;
					        this.tabPage1.Name = "tabPage1";
					        this.tabPage2.Name = "tabPage2";
					        this.button1.Name = "button1";
					        this.Controls.Add(this.tabControl1);
					        this.Controls.Add(this.button1);
					    }
					    private System.Windows.Forms.TabControl tabControl1;
					    private System.Windows.Forms.TabPage tabPage1;
					    private System.Windows.Forms.TabPage tabPage2;
					    private System.Windows.Forms.Button button1;
					}
					"""
				}
			}
		};
		Assert.True((await client.OpenAsync(snapshot, timeout.Token)).Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		// An index past the last page, and a negative one: both no-ops, and the still-correct
		// active page must be reported as such (IsVisible is what the client draws overlays from,
		// so a silent corruption here would resurrect the phantom-overlay bug).
		foreach (var staleIndex in new[] { 2, 99, -1 }) {
			var ignored = await client.SelectTabAsync(1, "tabControl1", staleIndex, timeout.Token);
			Assert.True(ignored.Accepted);
			Assert.True(ignored.Components.Single(item => item.Name == "tabPage1").IsVisible);
			Assert.False(ignored.Components.Single(item => item.Name == "tabPage2").IsVisible);
		}

		// Aimed at something that is not a TabControl at all (a plain Button, and the root form) -
		// the client hit-tests headers from reported bounds, so a near-miss lands on a neighbour.
		foreach (var notATabControl in new[] { "button1", "Form1" }) {
			var ignored = await client.SelectTabAsync(1, notATabControl, 1, timeout.Token);
			Assert.True(ignored.Accepted);
		}

		// A component with no verbs of its own answers with an empty list, not an error - the
		// client asks for EVERY selection to decide whether to offer any.
		var buttonVerbs = await client.ListVerbsAsync(1, "button1", timeout.Token);
		Assert.True(buttonVerbs.Accepted);
		Assert.DoesNotContain(buttonVerbs.Items, item => item.Text.Contains("Tab", StringComparison.OrdinalIgnoreCase));

		// Unknown ids and out-of-range verb indices are caller bugs: surfaced, not swallowed.
		await Assert.ThrowsAnyAsync<Exception>(() => client.ListVerbsAsync(1, "noSuchComponent", timeout.Token));
		var verbs = await client.ListVerbsAsync(1, "tabControl1", timeout.Token);
		Assert.NotEmpty(verbs.Items);
		await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeVerbAsync(1, "tabControl1", verbs.Items.Count + 5, timeout.Token));
		await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeVerbAsync(1, "tabControl1", -1, timeout.Token));

		// ...and the host is still healthy afterwards: every rejection above left the session usable.
		var stillAlive = await client.SelectTabAsync(1, "tabControl1", 1, timeout.Token);
		Assert.True(stillAlive.Accepted);
		Assert.True(stillAlive.Components.Single(item => item.Name == "tabPage2").IsVisible);
#else
		// LibreWinForms has no DesignerVerb support - fail clearly rather than silently no-op.
		var libreVerbs = await client.ListVerbsAsync(1, "tabControl1", timeout.Token);
		Assert.False(libreVerbs.Accepted);
		await Assert.ThrowsAnyAsync<Exception>(() => client.InvokeVerbAsync(1, "tabControl1", 0, timeout.Token));
#endif
	}

	/// <summary>
	/// IsVisible describes what is ACTUALLY RENDERED, which at design time is deliberately NOT the
	/// same as the Visible property's value. Real WinForms designers shadow Visible (and Enabled):
	/// setting it false records the value for runtime but leaves the control on the surface, so it
	/// stays selectable and movable - the behaviour real VS has, and the reason
	/// <c>Control.Visible</c>'s getter still returns true here.
	///
	/// So a hidden-at-runtime control MUST keep its outlines/name tag in the designer. The
	/// TabControl case in
	/// ChildHost_SelectTab_SwitchesActivePageWithoutPersistingOrCreatingAnUndoStep is the genuinely
	/// different one: a non-selected TabPage's window really is hidden by the TabControl itself, not
	/// by the shadowed property, which is exactly why phantom overlays appeared there and nowhere
	/// else. This test pins the distinction, because "fixing" IsVisible to read the shadowed
	/// property instead would silently drop every overlay for controls the user set Visible=false
	/// at design time.
	/// </summary>
	[Fact]
	public async Task ChildHost_IsVisible_ReflectsTheRenderNotTheShadowedDesignTimeVisibleProperty()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.panel1 = new System.Windows.Forms.Panel();
					        this.nestedButton = new System.Windows.Forms.Button();
					        this.visibleButton = new System.Windows.Forms.Button();
					        this.panel1.Controls.Add(this.nestedButton);
					        this.panel1.Name = "panel1";
					        this.panel1.Size = new System.Drawing.Size(200, 100);
					        this.nestedButton.Name = "nestedButton";
					        this.visibleButton.Name = "visibleButton";
					        this.Controls.Add(this.panel1);
					        this.Controls.Add(this.visibleButton);
					    }
					    private System.Windows.Forms.Panel panel1;
					    private System.Windows.Forms.Button nestedButton;
					    private System.Windows.Forms.Button visibleButton;
					}
					"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);
		Assert.True(opened.Components.Single(item => item.Name == "nestedButton").IsVisible);

		var hidden = await client.SetPropertyAsync(1, "panel1", "Visible", "false", timeout.Token);
		Assert.True(hidden.Accepted);
		// Visible is SHADOWED at design time on BOTH backends: the value is recorded for runtime,
		// but the control stays on the surface so it - and its child - keep their designer overlays
		// and stay selectable, which is what real VS does. Microsoft's ControlDesigner shadows it
		// itself; LibreWinForms' portable fork has no such shadowing, so this host does it (see
		// DesignerHostService.shadowedVisible). This assertion being backend-agnostic IS the point:
		// it was split per backend while that gap existed.
		Assert.True(hidden.Components.Single(item => item.Name == "panel1").IsVisible);
		Assert.True(hidden.Components.Single(item => item.Name == "nestedButton").IsVisible);
		// An unrelated sibling is untouched - "everything reports false" would hide every overlay.
		Assert.True(hidden.Components.Single(item => item.Name == "visibleButton").IsVisible);

		// The Properties pad must show the user's own edit, not the live on-surface true.
		Assert.Equal("False", hidden.Components.Single(item => item.Name == "panel1")
			.Properties.Single(property => property.Name == "Visible").Value);

		// ...and the value is persisted, so the built app really does hide it.
		Assert.Contains("panel1.Visible = false;", DesignerText(await client.FlushAsync(1, timeout.Token)),
			StringComparison.Ordinal);
	}

	/// <summary>
	/// The other half of Visible shadowing: a control that arrives ALREADY hidden from source must
	/// still be shown and selectable on the surface, with the Properties pad reporting the source's
	/// false. Without this, shadowing would only survive until the document was reopened - the
	/// control would come back invisible and be unreachable except from the Document Outline.
	///
	/// The TabPage assertions guard the exclusion that makes this safe: a TabControl drives its
	/// pages' Visible itself, so "restoring" an unselected page would show every page at once, and
	/// the phantom-overlay bug would be back.
	/// </summary>
	[Fact]
	public async Task ChildHost_AControlHiddenInSource_IsStillShownAndSelectableOnTheSurface()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.hiddenPanel = new System.Windows.Forms.Panel();
					        this.tabControl1 = new System.Windows.Forms.TabControl();
					        this.tabPage1 = new System.Windows.Forms.TabPage();
					        this.tabPage2 = new System.Windows.Forms.TabPage();
					        this.tabControl1.Controls.Add(this.tabPage1);
					        this.tabControl1.Controls.Add(this.tabPage2);
					        this.hiddenPanel.Name = "hiddenPanel";
					        this.hiddenPanel.Size = new System.Drawing.Size(120, 60);
					        this.hiddenPanel.Visible = false;
					        this.tabControl1.Name = "tabControl1";
					        this.tabControl1.SelectedIndex = 0;
					        this.tabPage1.Name = "tabPage1";
					        this.tabPage2.Name = "tabPage2";
					        this.Controls.Add(this.hiddenPanel);
					        this.Controls.Add(this.tabControl1);
					    }
					    private System.Windows.Forms.Panel hiddenPanel;
					    private System.Windows.Forms.TabControl tabControl1;
					    private System.Windows.Forms.TabPage tabPage1;
					    private System.Windows.Forms.TabPage tabPage2;
					}
					"""
				}
			}
		};
		var opened = await client.OpenAsync(snapshot, timeout.Token);
		Assert.True(opened.Accepted);

		// Shown on the surface (so it keeps its overlays and can be clicked)...
		var panel = opened.Components.Single(item => item.Name == "hiddenPanel");
		Assert.True(panel.IsVisible);
		// ...while the Properties pad still reports what the source says.
		Assert.Equal("False", panel.Properties.Single(property => property.Name == "Visible").Value);
		// ...and the source keeps saying it, untouched by being shown.
		Assert.Contains("hiddenPanel.Visible = false;", DesignerText(await client.FlushAsync(1, timeout.Token)),
			StringComparison.Ordinal);

		// A TabControl's own pages are NOT adopted: the selected one is visible, the other is not.
		Assert.True(opened.Components.Single(item => item.Name == "tabPage1").IsVisible);
		Assert.False(opened.Components.Single(item => item.Name == "tabPage2").IsVisible);
	}

	/// <summary>
	/// design/reorder-toolstrip-item CLAMPS its target index (Math.Clamp) rather than rejecting it,
	/// because the client computes drop indices from an insertion line hit-tested against item
	/// bounds - dropping past the last item is a normal gesture, not an error. Pinned because the
	/// rewritten source must stay consistent with the clamped live order: an off-by-one here writes
	/// designer code whose Items.Add order disagrees with what the user sees.
	/// </summary>
	[Fact]
	public async Task ChildHost_ReorderToolStripItem_ClampsAnOutOfRangeDropIndex()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var hostDll = HostDll();
		using var client = await FormsDesignerHostClient.StartAsync("", "", timeout.Token, hostDll);
		var snapshot = new DesignerDocumentSnapshot {
			Version = 1, PrimaryFileName = "/project/Form1.cs", DesignerFileName = "/project/Form1.Designer.cs",
			Files = {
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.cs", Kind = "Source", Text = "namespace Sample; partial class Form1 { }" },
				new DesignerSourceFileSnapshot { FileName = "/project/Form1.Designer.cs", Kind = "Designer", Text = """
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.toolStrip1 = new System.Windows.Forms.ToolStrip();
					        this.toolStripButton1 = new System.Windows.Forms.ToolStripButton();
					        this.toolStripButton2 = new System.Windows.Forms.ToolStripButton();
					        this.toolStrip1.Items.Add(this.toolStripButton1);
					        this.toolStrip1.Items.Add(this.toolStripButton2);
					        this.toolStrip1.Name = "toolStrip1";
					        this.toolStripButton1.Name = "toolStripButton1";
					        this.toolStripButton2.Name = "toolStripButton2";
					        this.Controls.Add(this.toolStrip1);
					    }
					    private System.Windows.Forms.ToolStrip toolStrip1;
					    private System.Windows.Forms.ToolStripButton toolStripButton1;
					    private System.Windows.Forms.ToolStripButton toolStripButton2;
					}
					"""
				}
			}
		};
		Assert.True((await client.OpenAsync(snapshot, timeout.Token)).Accepted);

#if MICROSOFT_FORMS_DESIGNER_HOST
		// Dropped way past the end: clamps to last, and the source order follows.
		Assert.True((await client.ReorderToolStripItemAsync(1, "toolStripButton1", 99, timeout.Token)).Accepted);
		var afterEnd = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.True(afterEnd.IndexOf("Items.Add(toolStripButton2)", StringComparison.Ordinal)
			< afterEnd.IndexOf("Items.Add(toolStripButton1)", StringComparison.Ordinal),
			"clamping to the end must put toolStripButton1 last in the rewritten Items.Add order");

		// Dropped above the first item: clamps to front, and back it goes.
		Assert.True((await client.ReorderToolStripItemAsync(1, "toolStripButton1", -7, timeout.Token)).Accepted);
		var afterStart = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.True(afterStart.IndexOf("Items.Add(toolStripButton1)", StringComparison.Ordinal)
			< afterStart.IndexOf("Items.Add(toolStripButton2)", StringComparison.Ordinal),
			"clamping to the front must put toolStripButton1 first in the rewritten Items.Add order");

		// A non-item target is still a hard error rather than a silent clamp.
		await Assert.ThrowsAnyAsync<Exception>(() => client.ReorderToolStripItemAsync(1, "toolStrip1", 0, timeout.Token));
		await Assert.ThrowsAnyAsync<Exception>(() => client.ReorderToolStripItemAsync(1, "noSuchItem", 0, timeout.Token));
#else
		await Assert.ThrowsAnyAsync<Exception>(() => client.ReorderToolStripItemAsync(1, "toolStripButton1", 0, timeout.Token));
#endif
	}

	static DesignerDocumentSnapshot VbSnapshot(long version, string text) => new() {
		Version = version,
		PrimaryFileName = "/project/Form1.vb",
		DesignerFileName = "/project/Form1.Designer.vb",
		Language = "VisualBasic",
		Files = {
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.vb",
				Kind = "Source",
				Text = "Imports System.Windows.Forms\nPublic Class Form1\n    Inherits Form\nEnd Class"
			},
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.Designer.vb",
				Kind = "Designer",
				Text = $$"""
					Imports System.Windows.Forms

					Partial Class Form1
					    Inherits Form

					    Private Sub InitializeComponent()
					        Me.button1 = New System.Windows.Forms.Button()
					        Me.button1.Text = "{{text}}"
					        Me.button1.Location = New System.Drawing.Point(12, 20)
					        Me.button1.Size = New System.Drawing.Size(90, 30)
					        Me.Controls.Add(Me.button1)
					    End Sub

					    Friend WithEvents button1 As System.Windows.Forms.Button
					End Class
					"""
			}
		}
	};

	static DesignerDocumentSnapshot Snapshot(long version, string text) => new() {
		Version = version,
		PrimaryFileName = "/project/Form1.cs",
		DesignerFileName = "/project/Form1.Designer.cs",
		Files = {
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.cs",
				Kind = "Source",
				Text = "namespace Sample; partial class Form1 { }"
			},
			new DesignerSourceFileSnapshot {
				FileName = "/project/Form1.Designer.cs",
				Kind = "Designer",
				Text = $$"""
					namespace Sample;
					partial class Form1
					{
					    private void InitializeComponent()
					    {
					        this.button1 = new System.Windows.Forms.Button();
					        this.button1.Text = "{{text}}";
					        this.button1.Location = new System.Drawing.Point(12, 20);
					        this.button1.Size = new System.Drawing.Size(90, 30);
					        this.Controls.Add(this.button1);
					    }
					    private System.Windows.Forms.Button button1;
					}
					"""
			}
		}
	};
}
