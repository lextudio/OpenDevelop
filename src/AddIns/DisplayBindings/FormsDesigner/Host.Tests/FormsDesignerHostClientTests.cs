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
		// transaction, no exception). This particular method
		// (ToolStripActionList.InsertStandardItems) is itself a no-op in this headless host - real
		// WinForms only actually populates items when a BehaviorService is present (VS's
		// interactive chrome), which this offscreen DesignSurface deliberately never registers - so
		// it is not asserted to add components; that would assert Microsoft's own internal
		// implementation detail rather than this host's contract.
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
		var withItem = await client.AddToolStripItemAsync(1, "menuStrip1", "ToolStripMenuItem", "", "fileToolStripMenuItem", timeout.Token);
		Assert.True(withItem.Accepted);
		Assert.Contains(withItem.Components, component => component.Name == "fileToolStripMenuItem"
			&& component.Type == "System.Windows.Forms.ToolStripMenuItem" && component.Parent == "menuStrip1");
		var flushed = DesignerText(await client.FlushAsync(1, timeout.Token));
		Assert.Contains("fileToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();", flushed, StringComparison.Ordinal);
		// Flush's ThisQualifierRewriter drops the redundant "this." prefix (same convention as
		// every other RewriteAdded* helper in DesignerHostService.cs - see e.g. the plain
		// "Controls.Add(label1);" assertions above), so the emitted statement has none either.
		Assert.Contains("menuStrip1.Items.Add(fileToolStripMenuItem);", flushed, StringComparison.Ordinal);

		// A submenu item nests into the parent's DropDownItems rather than the strip's own Items.
		var withSubItem = await client.AddToolStripItemAsync(1, "menuStrip1", "ToolStripMenuItem", "fileToolStripMenuItem", "openToolStripMenuItem", timeout.Token);
		Assert.Contains(withSubItem.Components, component => component.Name == "openToolStripMenuItem" && component.Parent == "fileToolStripMenuItem");
		Assert.Contains("fileToolStripMenuItem.DropDownItems.Add(openToolStripMenuItem);",
			DesignerText(await client.FlushAsync(1, timeout.Token)), StringComparison.Ordinal);
#else
		// LibreWinForms: fail clearly (a thrown NotSupportedException over RPC) instead of
		// silently no-opping.
		await Assert.ThrowsAnyAsync<Exception>(() =>
			client.AddToolStripItemAsync(1, "menuStrip1", "ToolStripMenuItem", "", "fileToolStripMenuItem", timeout.Token));
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
