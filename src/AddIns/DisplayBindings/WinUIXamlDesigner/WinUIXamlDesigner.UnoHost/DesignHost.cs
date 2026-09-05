using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Windows.Foundation;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// StreamJsonRpc target of the out-of-process design host. Every method marshals
	/// onto the headless Uno dispatcher thread before touching the UI tree.
	/// </summary>
	public class DesignHost
	{
		readonly Func<string?> getXamlAssemblyPath;

		public DesignHost(Func<string?> getXamlAssemblyPath)
		{
			this.getXamlAssemblyPath = getXamlAssemblyPath;
		}

		/// <summary>
		/// Optional hook letting a host keep the design root inside a live visual tree. Invoked as
		/// (previous, next) on every root change - both may be null - so the host can swap exactly
		/// this document's element without disturbing any other.
		///
		/// Uno leaves this null: its headless Skia dispatcher can measure, arrange and
		/// RenderTargetBitmap-render an element that belongs to no window at all. Real WinUI 3
		/// cannot - RenderAsync on an unparented element never completes, so session/open just
		/// blocks until the client's timeout - which is why the Microsoft host installs a hook that
		/// puts the root inside an offscreen window.
		///
		/// The pair-shaped signature is load-bearing for shared hosts: one process serves several
		/// documents at once, so a hook that only knew "the current root" would evict the previous
		/// document's element from the tree and silently break ITS rendering.
		/// </summary>
		public static Action<FrameworkElement?, FrameworkElement?>? HostVisualRoot;


		/// <summary>
		/// Optional hook letting a host rewrite XAML text immediately before every
		/// <c>XamlReader.Load</c> call in this class (document load, app resources, a
		/// toolbox-inserted element's template).
		///
		/// Uno leaves this null: its native <c>XamlControlsResources</c> merge makes the Fluent v2
		/// design tokens (colors, default text/control styles) available through the ordinary
		/// Application.Resources fallback chain, the same way a real app gets them. The Microsoft
		/// host cannot construct that type unpackaged (see FrameworkDefaultResources.cs), so it
		/// installs the same tokens from plain XAML instead - but a `{StaticResource ...}`
		/// reference resolves EAGERLY, at the parse that node belongs to, and does not reach into
		/// Application.Resources the way `{ThemeResource ...}` does at live-tree lookup time later.
		/// Merging the token dictionary into the SAME text being parsed - what this hook does - is
		/// the only way a StaticResource reference to a Fluent v2 token resolves for a host that
		/// cannot supply XamlControlsResources.
		/// </summary>
		public static Func<string, string>? TransformXamlBeforeLoad;

		FrameworkElement? root;

		public DesignerCapabilities GetCapabilities()
			=> HeadlessDispatcher.Dispatch(() => BuildCapabilities());

		public DesignerSessionState LoadDesign(string xaml, double width, double height, double dpi)
			=> HeadlessDispatcher.DispatchAsync(() => LoadDesignAsync(new LoadDesignRequest { Xaml = xaml, Width = width, Height = height, Dpi = dpi })).GetAwaiter().GetResult();

		/// <summary>Session-aware first load (session/open): stamps the returned snapshot with
		/// the session/document ids and the initial version.</summary>
		public DesignerSessionState OpenSession(string sessionId, string documentId, string xaml, double width, double height, double dpi)
		{
			this.sessionId = sessionId;
			this.documentId = documentId;
			var snapshot = HeadlessDispatcher.DispatchAsync(() => LoadDesignAsync(new LoadDesignRequest {
				SessionId = sessionId, DocumentId = documentId, Version = 1, Xaml = xaml, Width = width, Height = height, Dpi = dpi
			})).GetAwaiter().GetResult();
			version = 1;
			snapshot.SessionId = sessionId;
			snapshot.DocumentId = documentId;
			snapshot.Version = version;
			return snapshot;
		}

		/// <summary>Session-aware subsequent full-document push (session/update): replaces
		/// design/load for theme reloads, size-preset changes and any other full re-render.</summary>
		public DesignerSessionState UpdateSession(string sessionId, string documentId, string xaml, double width, double height, double dpi, long baseVersion)
		{
			EnsureOwnSession(sessionId, documentId);
			if (RejectIfStale(sessionId, documentId, baseVersion, allowNewerVersion: true) is { } stale)
				return stale;
			var snapshot = HeadlessDispatcher.DispatchAsync(() => LoadDesignAsync(new LoadDesignRequest {
				SessionId = sessionId, DocumentId = documentId, Version = baseVersion, Xaml = xaml, Width = width, Height = height, Dpi = dpi
			})).GetAwaiter().GetResult();
			version = baseVersion;
			snapshot.SessionId = sessionId;
			snapshot.DocumentId = documentId;
			snapshot.Version = version;
			return snapshot;
		}

		/// <summary>Stub: this host holds no independent child-side edit buffer, so flush
		/// reports the last-loaded XAML as the sole file - lands the wire shape now.</summary>
		public DesignerEditSet FlushSession(string sessionId, string documentId, long baseVersion)
		{
			EnsureOwnSession(sessionId, documentId);
			if (baseVersion != version)
				throw new InvalidOperationException($"Cannot flush stale version {baseVersion}; the open document is at version {version}.");
			return new DesignerEditSet {
				SessionId = sessionId,
				DocumentId = documentId,
				BaseVersion = baseVersion,
				Files = new List<DesignerSourceFileSnapshot> {
					new DesignerSourceFileSnapshot { FileName = "(document)", Text = lastXaml }
				}
			};
		}

		/// <summary>Applies a single property change directly to the live element and
		/// re-renders, without re-running XamlReader.Load - the point of moving off design/load
		/// for incremental edits.</summary>
		public DesignerSessionState SetProperty(string sessionId, string documentId, long baseVersion, string elementId, string propertyName, string value)
			=> HeadlessDispatcher.DispatchAsync(() => SetPropertyAsync(sessionId, documentId, baseVersion, elementId, propertyName, value)).GetAwaiter().GetResult();

		/// <summary>No live code-behind instance exists in this design host, so this just
		/// validates the element/event names and returns the snapshot unchanged - lands the
		/// RPC contract; deeper semantics is future work.</summary>
		public DesignerSessionState SetEvent(string sessionId, string documentId, long baseVersion, string elementId, string eventName, string handlerName)
			=> HeadlessDispatcher.Dispatch(() => SetEventCore(sessionId, documentId, baseVersion, elementId, eventName, handlerName));

		/// <summary>Parses <paramref name="item"/> and inserts it as a child of the named
		/// parent, then re-renders without re-running the full document XamlReader.Load.</summary>
		public DesignerSessionState AddElement(string sessionId, string documentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, double x, double y)
			=> HeadlessDispatcher.DispatchAsync(() => AddElementAsync(sessionId, documentId, baseVersion, parentId, item, x, y)).GetAwaiter().GetResult();

		/// <summary>Sets an element's Width/Height directly, and its Canvas.Left/Top when its
		/// parent is a Canvas, then re-renders.</summary>
		public DesignerSessionState SetBounds(string sessionId, string documentId, long baseVersion, string elementId, double x, double y, double width, double height)
			=> HeadlessDispatcher.DispatchAsync(() => SetBoundsAsync(sessionId, documentId, baseVersion, elementId, x, y, width, height)).GetAwaiter().GetResult();

		/// <summary>Removes each named element from its Panel parent, then re-renders.</summary>
		public DesignerSessionState DeleteElements(string sessionId, string documentId, long baseVersion, string[] elementIds)
			=> HeadlessDispatcher.DispatchAsync(() => DeleteElementsAsync(sessionId, documentId, baseVersion, elementIds)).GetAwaiter().GetResult();

		/// <summary>Renames the live element (FrameworkElement.Name), then re-renders.</summary>
		public DesignerSessionState Rename(string sessionId, string documentId, long baseVersion, string elementId, string newName)
			=> HeadlessDispatcher.DispatchAsync(() => RenameAsync(sessionId, documentId, baseVersion, elementId, newName)).GetAwaiter().GetResult();

		public DesignerSessionState Layout(double width, double height, double dpi)
			=> HeadlessDispatcher.DispatchAsync(() => LayoutAsync(new LayoutRequest { Width = width, Height = height, Dpi = dpi })).GetAwaiter().GetResult();

		public DesignerSessionState SetTheme(string theme)
			=> HeadlessDispatcher.DispatchAsync(() => SetThemeAsync(theme)).GetAwaiter().GetResult();

		public DesignerAppResourcesResult LoadAppResources(string xaml)
			=> HeadlessDispatcher.Dispatch(() => LoadAppResourcesCore(xaml));

		/// <summary>Renders the current design again and writes it as a PNG to the given
		/// path (via Skia, which the WPF-side PNG codecs cannot do under LibreWPF).</summary>
		public string ExportPng(string path)
			=> HeadlessDispatcher.DispatchAsync(() => ExportPngAsync(path)).GetAwaiter().GetResult();

		async Task<string> ExportPngAsync(string path)
		{
			if (string.IsNullOrEmpty(path) || root is null)
			{
				return "Nothing to export (no design loaded)";
			}
			try
			{
				var dpi = lastDpi > 1.0 ? lastDpi : 1.0;
				var rtb = new RenderTargetBitmap();
				var scaled = new Size(root.RenderSize.Width * dpi, root.RenderSize.Height * dpi);
				await rtb.RenderAsync(root, (int)scaled.Width, (int)scaled.Height);
				var pixels = await rtb.GetPixelsAsync();
				var buffer = new byte[pixels.Length];
				DataReader.FromBuffer(pixels).ReadBytes(buffer);
				using var bitmap = new SkiaSharp.SKBitmap();
				var info = new SkiaSharp.SKImageInfo(rtb.PixelWidth, rtb.PixelHeight, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
				var pinned = System.Runtime.InteropServices.Marshal.AllocHGlobal(buffer.Length);
				try
				{
					System.Runtime.InteropServices.Marshal.Copy(buffer, 0, pinned, buffer.Length);
					bitmap.InstallPixels(info, pinned, info.RowBytes);
					using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
					using var stream = System.IO.File.Create(path);
					data.SaveTo(stream);
				}
				finally
				{
					System.Runtime.InteropServices.Marshal.FreeHGlobal(pinned);
				}
				return $"Wrote {path} ({rtb.PixelWidth}x{rtb.PixelHeight})";
			}
			catch (Exception e)
			{
				return "Export failed: " + e.GetBaseException().Message;
			}
		}
		public DesignerHitTestResult HitTest(string sessionId, string documentId, long baseVersion, double x, double y)
		{
			EnsureOwnSession(sessionId, documentId);
			return HeadlessDispatcher.Dispatch(() => HitTestCore(new HitTestRequest { X = x, Y = y }));
		}

		public void Shutdown()
		{
			HeadlessDispatcher.RequestExit();
		}

		public void Close()
		{
			HeadlessDispatcher.Dispatch(() => {
				var closedRoot = root;
				root = null;
				HostVisualRoot?.Invoke(closedRoot, null);
				sessionId = null;
				documentId = null;
				lastXaml = "";
				return true;
			});
		}

		DesignerCapabilities BuildCapabilities()
		{
			var asm = typeof(FrameworkElement).Assembly;
			return new DesignerCapabilities
			{
				Runtime = "Uno.Skia",
				Version = asm.GetName().Version?.ToString() ?? "",
				Toolbox = BuildToolboxCatalog()
			};
		}

		/// <summary>
		/// Toolbox catalog generated from the *loaded* runtime assemblies (the project's
		/// own Uno, once the child runs under the project's deps) plus a design-time
		/// allowlist - the toolbox always matches the project's actual control set.
		/// </summary>
		List<DesignerToolboxItemInfo> BuildToolboxCatalog()
		{
			var catalog = new List<DesignerToolboxItemInfo>();
			foreach (var t in typeof(FrameworkElement).Assembly.GetTypes())
			{
				if (catalog.Count >= 200)
				{
					break;
				}
				if (TryCreateToolboxItem(t) is { } item)
				{
					catalog.Add(item);
				}
			}
			return catalog;
		}

		static DesignerToolboxItemInfo? TryCreateToolboxItem(Type type)
		{
			var ns = type.Namespace ?? "";
			if (ns != "Microsoft.UI.Xaml.Controls")
			{
				return null;
			}
			if (!type.IsPublic || type.IsAbstract || type.IsGenericTypeDefinition)
			{
				return null;
			}
			if (!typeof(FrameworkElement).IsAssignableFrom(type))
			{
				return null;
			}
			if (type.GetConstructor(Type.EmptyTypes) is null)
			{
				return null;
			}
			// Shell/template parts and navigation hosts make no sense as insertable content.
			switch (type.Name)
			{
				case "Page":
				case "UserControl":
				case "Frame":
				case "Popup":
				case "ContentDialog":
				case "Flyout":
				case "MenuFlyout":
				case "ContentPresenter":
				case "ItemsPresenter":
				case "ContentControl":
				case "MenuFlyoutItem":
				case "ToggleMenuFlyoutItem":
				case "MenuFlyoutSubItem":
				case "CommandBarFlyout":
				case "AppBarSeparator":
				case "NavigationViewItem":
				case "NavigationViewItemSeparator":
				case "TreeViewItem":
				case "TabViewItem":
				case "ListViewItem":
				case "GridViewItem":
				case "ListBoxItem":
				case "ComboBoxItem":
				case "MediaPlayerElement":
					return null;
			}
			var name = type.Name;
			return new DesignerToolboxItemInfo
			{
				Name = name,
				DisplayName = name,
				Category = Categorize(name),
				XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation",
				Template = BuildDefaultTemplate(name)
			};
		}

		/// <summary>Maps a WinUI control to its Toolbox group, mirroring the grouping the
		/// Fluent control gallery uses so the pad reads like the real WinUI toolbox.</summary>
		static string Categorize(string name)
		{
			switch (name)
			{
				case "Button":
				case "RepeatButton":
				case "ToggleButton":
				case "HyperlinkButton":
				case "AppBarButton":
				case "AppBarToggleButton":
				case "RadioButton":
				case "CheckBox":
				case "ToggleSwitch":
					return "Buttons";

				case "TextBlock":
				case "TextBox":
				case "RichEditBox":
				case "RichTextBlock":
				case "RichTextBlockOverflow":
				case "PasswordBox":
				case "AutoSuggestBox":
				case "NumberBox":
					return "Text";

				case "Grid":
				case "StackPanel":
				case "RelativePanel":
				case "Canvas":
				case "VariableSizedWrapGrid":
				case "Border":
				case "ItemsRepeater":
				case "ScrollViewer":
					return "Containers";

				case "ListView":
				case "GridView":
				case "ListBox":
				case "ComboBox":
				case "ItemsView":
				case "SelectorBar":
				case "TreeView":
				case "Pivot":
					return "Lists";

				case "Image":
				case "SymbolIcon":
				case "FontIcon":
				case "BitmapIcon":
				case "PathIcon":
				case "IconSourceElement":
					return "Media";

				case "NavigationView":
				case "TabView":
				case "SplitView":
				case "CommandBar":
				case "MenuBar":
				case "PersonPicture":
					return "Navigation";

				case "ProgressBar":
				case "ProgressRing":
				case "RatingControl":
				case "Slider":
					return "Status & Progress";

				case "InfoBar":
				case "TeachingTip":
				case "ToolTip":
					return "Feedback";

				case "DatePicker":
				case "TimePicker":
				case "CalendarDatePicker":
				case "CalendarView":
					return "Date & Time";

				case "ColorPicker":
				case "InkCanvas":
				case "InkToolbar":
					return "Ink & Color";

				default:
					return "Common";
			}
		}

		/// <summary>Matches the default content the host's editor gives inserted controls.
		/// Controls that need a size or a value to be useful on the canvas get sensible
		/// initial properties; everything else is inserted as an empty element.</summary>
		static string BuildDefaultTemplate(string name)
		{
			var ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
			switch (name)
			{
				case "TextBlock":
					return $"<TextBlock xmlns=\"{ns}\" Text=\"TextBlock\"/>";
				case "TextBox":
					return $"<TextBox xmlns=\"{ns}\" Text=\"TextBox\"/>";
				case "RichEditBox":
					return $"<RichEditBox xmlns=\"{ns}\" IsReadOnly=\"False\" Height=\"120\"/>";
				case "PasswordBox":
					return $"<PasswordBox xmlns=\"{ns}\" Password=\"PasswordBox\"/>";
				case "NumberBox":
					return $"<NumberBox xmlns=\"{ns}\" Value=\"0\"/>";
				case "Button":
				case "CheckBox":
				case "HyperlinkButton":
				case "RadioButton":
				case "ToggleSwitch":
					return $"<{name} xmlns=\"{ns}\" Content=\"{name}\"/>";
				case "RepeatButton":
				case "ToggleButton":
				case "AppBarButton":
				case "AppBarToggleButton":
					return $"<{name} xmlns=\"{ns}\" Content=\"{name}\"/>";
				case "Slider":
					return $"<Slider xmlns=\"{ns}\" Width=\"200\" Minimum=\"0\" Maximum=\"100\" Value=\"50\"/>";
				case "ProgressBar":
					return $"<ProgressBar xmlns=\"{ns}\" Width=\"200\" Value=\"40\"/>";
				case "ProgressRing":
					return $"<ProgressRing xmlns=\"{ns}\" Width=\"60\" Height=\"60\" IsActive=\"True\"/>";
				case "RatingControl":
					return $"<RatingControl xmlns=\"{ns}\" Value=\"3\"/>";
				case "Image":
					return $"<Image xmlns=\"{ns}\" Width=\"200\" Height=\"120\"/>";
				case "AutoSuggestBox":
					return $"<AutoSuggestBox xmlns=\"{ns}\" PlaceholderText=\"Search\"/>";
				case "InfoBar":
					return $"<InfoBar xmlns=\"{ns}\" Title=\"Title\" Message=\"Message\" IsOpen=\"True\"/>";
				case "CalendarDatePicker":
					return $"<CalendarDatePicker xmlns=\"{ns}\" Header=\"Date\"/>";
				case "ColorPicker":
					return $"<ColorPicker xmlns=\"{ns}\" Color=\"#FF0078D4\"/>";
				case "ComboBox":
					return $"<ComboBox xmlns=\"{ns}\" PlaceholderText=\"Select\"><ComboBoxItem Content=\"Item 1\"/><ComboBoxItem Content=\"Item 2\"/></ComboBox>";
				case "ListBox":
					return $"<ListBox xmlns=\"{ns}\" Width=\"180\" Height=\"120\"><ListBoxItem Content=\"Item 1\"/><ListBoxItem Content=\"Item 2\"/></ListBox>";
				case "ListView":
					return $"<ListView xmlns=\"{ns}\" Width=\"240\" Height=\"160\"><ListViewItem Content=\"Item 1\"/><ListViewItem Content=\"Item 2\"/></ListView>";
				case "GridView":
					return $"<GridView xmlns=\"{ns}\" Width=\"240\" Height=\"160\"><GridViewItem Content=\"Item 1\"/><GridViewItem Content=\"Item 2\"/></GridView>";
				default:
					return $"<{name} xmlns=\"{ns}\"/>";
			}
		}

		async Task<DesignerSessionState> LoadDesignAsync(LoadDesignRequest request)
		{
			var snapshot = new DesignerSessionState { Accepted = true };
			try
			{
				lastXaml = request.Xaml;
				var xaml = InjectDesignData(request.Xaml);
				xaml = TransformXamlBeforeLoad?.Invoke(xaml) ?? xaml;
				var previousRoot = root;
				root = (FrameworkElement)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
				HostVisualRoot?.Invoke(previousRoot, root);
				return await FinishLayoutAsync(request.Width, request.Height, request.Dpi, snapshot);
			}
			catch (Exception e)
			{
				var failedRoot = root;
				root = null;
				HostVisualRoot?.Invoke(failedRoot, null);
				snapshot.Diagnostics.Add(ToDiagnostic(e.GetBaseException()));
				return snapshot;
			}
		}

		/// <summary>
		/// Design-time data preview: an element carrying <c>d:DesignData="N"</c> (or
		/// <c>"3;A,B,C"</c> for custom labels) gets placeholder items injected so collection
		/// controls show content in the designer before any runtime data exists. The
		/// d:-prefixed attribute (and its namespace/ignorable plumbing) is stripped before
		/// the runtime XamlReader sees it. Injection happens on the XDocument; the namespace
		/// cleanup is done on the serialized text (removing xmlns declarations via
		/// XAttribute.Remove is fragile when the document was just re-enumerated).
		/// </summary>
		static string InjectDesignData(string xaml)
		{
			if (string.IsNullOrEmpty(xaml) || !xaml.Contains("DesignData", StringComparison.Ordinal))
			{
				return xaml;
			}
			var xns = "http://schemas.microsoft.com/winfx/2006/xaml";
			var document = System.Xml.Linq.XDocument.Parse(xaml, System.Xml.Linq.LoadOptions.PreserveWhitespace);
			foreach (var element in document.Descendants()
				.Where(e => e.Attributes().Any(a => a.Name.LocalName == "DesignData")).ToList())
			{
				var attribute = element.Attributes().First(a => a.Name.LocalName == "DesignData");
				var value = (string)attribute;
				var labels = new List<string>();
				if (value.Contains(';', StringComparison.Ordinal))
				{
					var parts = value.Split(';');
					if (int.TryParse(parts[0].Trim(), out var count))
					{
						var custom = parts.Length > 1
							? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
							: Array.Empty<string>();
						for (var i = 0; i < count; i++)
							labels.Add(i < custom.Length ? custom[i] : "Item " + (i + 1));
					}
				}
				else if (int.TryParse(value.Trim(), out var n))
				{
					for (var i = 1; i <= n; i++)
						labels.Add("Item " + i);
				}
				attribute.Remove();
				foreach (var label in labels)
				{
					element.Add(new System.Xml.Linq.XElement(System.Xml.Linq.XName.Get("String", xns), label));
				}
			}
			using var writer = new StringWriter();
			document.Save(writer, System.Xml.Linq.SaveOptions.DisableFormatting);
			xaml = writer.ToString();
			// Strip the design-time namespace declarations and attributes at the text level.
			xaml = System.Text.RegularExpressions.Regex.Replace(xaml,
				@"\s+xmlns:\w+=""[^""]*(expression/blend|openxmlformats)[^""]*""", "");
			xaml = System.Text.RegularExpressions.Regex.Replace(xaml,
				@"\s+\w+:Ignorable=""[^""]*""", "");
			xaml = System.Text.RegularExpressions.Regex.Replace(xaml,
				@"\s+\w+:DesignData=""[^""]*""", "");
			return xaml;
		}

		/// <summary>Builds a diagnostic from a XAML load exception, extracting the line/position
		/// from the message text ("Line N, position M") so the shell can jump to the error.</summary>
		static DesignerDiagnostic ToDiagnostic(Exception e)
		{
			var diagnostic = new DesignerDiagnostic { Message = e.Message };
			var match = System.Text.RegularExpressions.Regex.Match(e.Message,
				@"[Ll]ine\s+(\d+)(?:[,;]\s*[Pp]osition\s+(\d+))?");
			if (match.Success)
			{
				if (int.TryParse(match.Groups[1].Value, out var line))
					diagnostic.Line = Math.Max(1, line);
				if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var column))
					diagnostic.Column = Math.Max(1, column);
			}
			return diagnostic;
		}

		async Task<DesignerSessionState> LayoutAsync(LayoutRequest request)
		{
			var snapshot = new DesignerSessionState { Accepted = true };
			if (root is null)
			{
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = "No design loaded." });
				return snapshot;
			}
			return await FinishLayoutAsync(request.Width, request.Height, request.Dpi, snapshot);
		}

		/// <summary>Switches the design's theme (Light/Dark) and re-renders, so ThemeResource
		/// lookups resolve against the chosen theme. The headless tree has no XamlRoot, so
		/// element-level RequestedTheme cannot drive the resource re-resolution on its own;
		/// instead the application-level explicit theme is set through the same internal
		/// route Application.RequestedTheme would take (blocked post-initialization), and the
		/// design is reloaded so {ThemeResource} values resolve under the new active theme.</summary>
		async Task<DesignerSessionState> SetThemeAsync(string theme)
		{
			var snapshot = new DesignerSessionState { Accepted = true };
			if (root is null)
			{
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = "No design loaded." });
				return snapshot;
			}
			try
			{
				SetApplicationThemeReflectively(theme);
				return await LoadDesignAsync(new LoadDesignRequest
				{
					Xaml = lastXaml,
					Width = lastWidth,
					Height = lastHeight,
					Dpi = lastDpi
				});
			}
			catch (Exception e)
			{
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = e.GetBaseException().Message });
				return snapshot;
			}
		}

		/// <summary>
		/// Uno blocks Application.RequestedTheme after initialization, but its internal
		/// SetExplicitRequestedTheme still works (and is what the hot-reload/theme-change
		/// paths use): it updates the framework theming state and notifies the core, which
		/// flips the active theme for ThemeResource resolution.
		/// </summary>
		static void SetApplicationThemeReflectively(string theme)
		{
			var method = typeof(Application).GetMethod("SetExplicitRequestedTheme",
				System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
			if (method == null)
			{
				throw new InvalidOperationException("SetExplicitRequestedTheme not found on Uno's Application.");
			}
			ApplicationTheme? value = theme switch
			{
				"Dark" => ApplicationTheme.Dark,
				"Light" => ApplicationTheme.Light,
				_ => null
			};
			method.Invoke(Application.Current, new object[] { value });
		}

		string lastXaml = "";
		double lastWidth;
		double lastHeight;
		double lastDpi;
		string? sessionId;
		string? documentId;
		long version;

		void EnsureOwnSession(string requestSessionId, string requestDocumentId)
		{
			if (sessionId != null && requestSessionId != sessionId)
				throw new UnauthorizedAccessException("The request's session id does not match this design host.");
			if (documentId != null && requestDocumentId != documentId)
				throw new InvalidOperationException("The request's document id does not match the open document.");
		}

		/// <summary>Rejects an operation issued against an older parent-owned source snapshot.
		/// Mutations must never revive source that <c>session/update</c> has already replaced;
		/// this is the common DDP stale-operation rule used by every child backend.</summary>
		DesignerSessionState? RejectIfStale(string requestSessionId, string requestDocumentId, long baseVersion,
			bool allowNewerVersion = false)
		{
			if (baseVersion == version || allowNewerVersion && baseVersion > version)
				return null;
			var error = $"Stale base version {baseVersion}; the open document is at version {version}.";
			var state = new DesignerSessionState {
				SessionId = requestSessionId,
				DocumentId = requestDocumentId,
				Version = baseVersion,
				Accepted = false,
				Error = error
			};
			state.Diagnostics.Add(new DesignerDiagnostic { Message = error });
			return state;
		}

		async Task<DesignerSessionState> SetPropertyAsync(string requestSessionId, string requestDocumentId, long baseVersion, string elementId, string propertyName, string value)
		{
			EnsureOwnSession(requestSessionId, requestDocumentId);
			if (RejectIfStale(requestSessionId, requestDocumentId, baseVersion) is { } stale)
				return stale;
			var snapshot = new DesignerSessionState { SessionId = requestSessionId, DocumentId = requestDocumentId, Version = baseVersion, Accepted = true };
			if (root is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "No design loaded.";
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			var element = FindByName(root, elementId);
			if (element is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "Element not found: " + elementId;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			try
			{
				ApplyPropertyValue(element, propertyName, value);
				version = Math.Max(version, baseVersion);
				var result = await FinishLayoutAsync(lastWidth, lastHeight, lastDpi, snapshot);
				result.SessionId = requestSessionId;
				result.DocumentId = requestDocumentId;
				result.Version = version;
				return result;
			}
			catch (Exception e)
			{
				snapshot.Accepted = false;
				snapshot.Error = e.GetBaseException().Message;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
		}

		/// <summary>Sets a property on a live FrameworkElement by name via TypeDescriptor/
		/// reflection, converting the incoming string with the property's declared type.</summary>
		static void ApplyPropertyValue(FrameworkElement element, string propertyName, string value)
		{
			var property = element.GetType().GetProperty(propertyName)
				?? throw new ArgumentException("Property not found: " + propertyName, nameof(propertyName));
			if (!property.CanWrite)
				throw new InvalidOperationException($"Property {propertyName} is read-only.");
			object? converted;
			if (property.PropertyType == typeof(string))
			{
				converted = value;
			}
			else
			{
				var converter = System.ComponentModel.TypeDescriptor.GetConverter(property.PropertyType);
				converted = converter != null && converter.CanConvertFrom(typeof(string))
					? converter.ConvertFromInvariantString(value)
					: Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
			}
			property.SetValue(element, converted);
		}

		DesignerSessionState SetEventCore(string requestSessionId, string requestDocumentId, long baseVersion, string elementId, string eventName, string handlerName)
		{
			EnsureOwnSession(requestSessionId, requestDocumentId);
			if (RejectIfStale(requestSessionId, requestDocumentId, baseVersion) is { } stale)
				return stale;
			var snapshot = new DesignerSessionState { SessionId = requestSessionId, DocumentId = requestDocumentId, Version = baseVersion, Accepted = true };
			if (root is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "No design loaded.";
				return snapshot;
			}
			var element = FindByName(root, elementId);
			if (element is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "Element not found: " + elementId;
				return snapshot;
			}
			var eventInfo = element.GetType().GetEvent(eventName);
			if (eventInfo is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "Event not found: " + eventName;
				return snapshot;
			}
			// No live code-behind instance exists in this design host - the event/handler
			// names are validated but nothing is actually wired up here.
			snapshot.Tree = BuildTree(root, root, "");
			snapshot.Accepted = true;
			return snapshot;
		}

		async Task<DesignerSessionState> AddElementAsync(string requestSessionId, string requestDocumentId, long baseVersion, string parentId, DesignerToolboxItemInfo item, double x, double y)
		{
			EnsureOwnSession(requestSessionId, requestDocumentId);
			if (RejectIfStale(requestSessionId, requestDocumentId, baseVersion) is { } stale)
				return stale;
			var snapshot = new DesignerSessionState { SessionId = requestSessionId, DocumentId = requestDocumentId, Version = baseVersion, Accepted = true };
			if (root is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "No design loaded.";
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			var parent = FindByName(root, parentId);
			if (parent is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "Parent not found: " + parentId;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			try
			{
				var newElement = (UIElement)Microsoft.UI.Xaml.Markup.XamlReader.Load(TransformXamlBeforeLoad?.Invoke(item.Template) ?? item.Template);
				if (parent is Panel panel)
				{
					panel.Children.Add(newElement);
					if (parent is Canvas && newElement is FrameworkElement newFe)
					{
						Canvas.SetLeft(newFe, x);
						Canvas.SetTop(newFe, y);
					}
				}
				else
				{
					snapshot.Accepted = false;
					snapshot.Error = "Parent does not support adding children: " + parentId;
					snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
					return snapshot;
				}
				version = Math.Max(version, baseVersion);
				var result = await FinishLayoutAsync(lastWidth, lastHeight, lastDpi, snapshot);
				result.SessionId = requestSessionId;
				result.DocumentId = requestDocumentId;
				result.Version = version;
				return result;
			}
			catch (Exception e)
			{
				snapshot.Accepted = false;
				snapshot.Error = e.GetBaseException().Message;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
		}

		async Task<DesignerSessionState> SetBoundsAsync(string requestSessionId, string requestDocumentId, long baseVersion, string elementId, double x, double y, double width, double height)
		{
			EnsureOwnSession(requestSessionId, requestDocumentId);
			if (RejectIfStale(requestSessionId, requestDocumentId, baseVersion) is { } stale)
				return stale;
			var snapshot = new DesignerSessionState { SessionId = requestSessionId, DocumentId = requestDocumentId, Version = baseVersion, Accepted = true };
			if (root is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "No design loaded.";
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			var element = FindByName(root, elementId);
			if (element is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "Element not found: " + elementId;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			try
			{
				element.Width = width;
				element.Height = height;
				if (VisualTreeHelper.GetParent(element) is Canvas)
				{
					Canvas.SetLeft(element, x);
					Canvas.SetTop(element, y);
				}
				version = Math.Max(version, baseVersion);
				var result = await FinishLayoutAsync(lastWidth, lastHeight, lastDpi, snapshot);
				result.SessionId = requestSessionId;
				result.DocumentId = requestDocumentId;
				result.Version = version;
				return result;
			}
			catch (Exception e)
			{
				snapshot.Accepted = false;
				snapshot.Error = e.GetBaseException().Message;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
		}

		/// <summary>Matches WinForms' DeleteComponent, which throws on a not-found component
		/// rather than skipping - a batch delete fails the whole request instead of silently
		/// dropping a bad name, so the caller finds out about a stale reference immediately.</summary>
		async Task<DesignerSessionState> DeleteElementsAsync(string requestSessionId, string requestDocumentId, long baseVersion, string[] elementIds)
		{
			EnsureOwnSession(requestSessionId, requestDocumentId);
			if (RejectIfStale(requestSessionId, requestDocumentId, baseVersion) is { } stale)
				return stale;
			var snapshot = new DesignerSessionState { SessionId = requestSessionId, DocumentId = requestDocumentId, Version = baseVersion, Accepted = true };
			if (root is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "No design loaded.";
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			try
			{
				foreach (var name in elementIds)
				{
					var element = FindByName(root, name)
						?? throw new ArgumentException("Element not found: " + name, nameof(elementIds));
					if (VisualTreeHelper.GetParent(element) is Panel panel)
					{
						panel.Children.Remove(element);
					}
					else
					{
						throw new InvalidOperationException("Element's parent does not support removing children: " + name);
					}
				}
				version = Math.Max(version, baseVersion);
				var result = await FinishLayoutAsync(lastWidth, lastHeight, lastDpi, snapshot);
				result.SessionId = requestSessionId;
				result.DocumentId = requestDocumentId;
				result.Version = version;
				return result;
			}
			catch (Exception e)
			{
				snapshot.Accepted = false;
				snapshot.Error = e.GetBaseException().Message;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
		}

		async Task<DesignerSessionState> RenameAsync(string requestSessionId, string requestDocumentId, long baseVersion, string elementId, string newName)
		{
			EnsureOwnSession(requestSessionId, requestDocumentId);
			if (RejectIfStale(requestSessionId, requestDocumentId, baseVersion) is { } stale)
				return stale;
			var snapshot = new DesignerSessionState { SessionId = requestSessionId, DocumentId = requestDocumentId, Version = baseVersion, Accepted = true };
			if (root is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "No design loaded.";
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			var element = FindByName(root, elementId);
			if (element is null)
			{
				snapshot.Accepted = false;
				snapshot.Error = "Element not found: " + elementId;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
			try
			{
				element.Name = newName;
				version = Math.Max(version, baseVersion);
				var result = await FinishLayoutAsync(lastWidth, lastHeight, lastDpi, snapshot);
				result.SessionId = requestSessionId;
				result.DocumentId = requestDocumentId;
				result.Version = version;
				return result;
			}
			catch (Exception e)
			{
				snapshot.Accepted = false;
				snapshot.Error = e.GetBaseException().Message;
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = snapshot.Error });
				return snapshot;
			}
		}

		static FrameworkElement? FindByName(DependencyObject node, string name)
		{
			if (node is FrameworkElement fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
				return fe;
			var count = VisualTreeHelper.GetChildrenCount(node);
			for (var i = 0; i < count; i++)
			{
				if (FindByName(VisualTreeHelper.GetChild(node, i), name) is { } found)
					return found;
			}
			return null;
		}

		async Task<DesignerSessionState> FinishLayoutAsync(double width, double height, double dpi, DesignerSessionState snapshot)
		{
			try
			{
				lastWidth = width;
				lastHeight = height;
				lastDpi = dpi;
				var size = new Size(width, height);
				root!.Measure(size);
				root.Arrange(new Rect(0, 0, width, height));
				// NOTE: do NOT call root.UpdateLayout() here to settle positions. In the Microsoft
				// host the root is parented in a real (offscreen) window, so a framework layout
				// pass re-arranges it to the WINDOW's size and discards the explicit design-size
				// Arrange above - the render then comes back at a different size than the one the
				// snapshot reports, and the presented bitmap is visibly stretched. BuildTree reads
				// positions from ActualOffset instead, which this Arrange has already committed.
				BoundsLog($"FinishLayout requested={width}x{height} dpi={dpi} rootActual={root.ActualWidth}x{root.ActualHeight} rootDesired={root.DesiredSize.Width}x{root.DesiredSize.Height}");
				snapshot.Render = await RenderAsync(dpi);
				// The tree is read AFTER the render, not before. In the Microsoft host the root
				// lives in a real offscreen window, so the window owns its layout and the
				// Measure/Arrange above is discarded - read at that point, every element still
				// had ActualOffset and layout slot of zero, so the whole tree reported itself at
				// (0,0) and the selection outline sat a whole row above the rendered control for
				// anything but a panel's first child. Rendering is what drives that pending
				// layout pass to completion, so by here the offsets are real. Uno is unaffected:
				// its Measure/Arrange commit synchronously, so the values are the same either
				// way, and rendering never invalidates them.
				snapshot.Tree = BuildTree(root, root, "");
				BoundsLog($"FinishLayout rendered={snapshot.Render?.Width}x{snapshot.Render?.Height} rootActualAfterRender={root.ActualWidth}x{root.ActualHeight}");
			}
			catch (Exception e)
			{
				snapshot.Diagnostics.Add(new DesignerDiagnostic { Message = e.GetBaseException().Message });
			}
			return snapshot;
		}

		/// <summary>
		/// Replaces the app-level resource dictionaries with the project's App.xaml content
		/// (rebuilt inline by the parent) so StaticResource/ThemeResource resolve against the
		/// real app. The Fluent XamlControlsResources stays in place below it, so lookups hit
		/// the app's own entries first. A load failure is reported and the design continues
		/// without the app resources.
		/// </summary>
		DesignerAppResourcesResult LoadAppResourcesCore(string xaml)
		{
			try
			{
				var dictionary = (ResourceDictionary)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
				Application.Current.Resources.MergedDictionaries.Insert(0, dictionary);
				return new DesignerAppResourcesResult { Success = true };
			}
			catch (Exception e)
			{
				return new DesignerAppResourcesResult { Success = false, Error = e.GetBaseException().Message };
			}
		}

		async Task<DesignerRenderFrame> RenderAsync(double dpi)
		{
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			var rtb = new RenderTargetBitmap();
			// The headless visual tree has no display, so its system scale is 1.0 and an
			// unscaled render would be soft on a Retina display. Uno's RenderTargetBitmap
			// rasterizes at renderSize * GetEffectiveRasterizationScale(), and that scale
			// honors RootScale._testOverrideScale - set it reflectively (same pattern as
			// HeadlessDispatcher) to get a crisp, native-resolution bitmap. The override
			// only feeds GetEffectiveRasterizationScale; it does not trigger ApplyScale,
			// so the root visual's own scale stays untouched (no double scaling).
			if (dpi > 0 && Math.Abs(dpi - 1.0) > 0.001 && TrySetRasterizationScale(root, dpi))
			{
				await rtb.RenderAsync(root);
			}
			else
			{
				// Careful with this overload: it does not merely set the bitmap's dimensions, it
				// SCALES the element's content to fill them. Passing anything other than the
				// element's own render size stretches the design - measured live, asking for
				// 1280x720 while the page's content was ~51px tall smeared it across all 720 rows.
				var scaled = new Size(root!.RenderSize.Width * dpi, root.RenderSize.Height * dpi);
				await rtb.RenderAsync(root, (int)scaled.Width, (int)scaled.Height);
			}
			var pixels = await rtb.GetPixelsAsync();
			var buffer = new byte[pixels.Length];
			DataReader.FromBuffer(pixels).ReadBytes(buffer);
			BoundsLog($"  render rtbPixels={rtb.PixelWidth}x{rtb.PixelHeight} rootRenderSize={root!.RenderSize.Width}x{root.RenderSize.Height}"
				+ $" dpi={dpi} bufferBytes={buffer.Length} expectedBytes={(long)rtb.PixelWidth * rtb.PixelHeight * 4}"
				+ $" impliedRows={(rtb.PixelWidth > 0 ? buffer.Length / (rtb.PixelWidth * 4) : -1)}");
			// The frame travels as deflate-compressed BGRA (base64): a 2x design bitmap is
			// tens of MB raw, and UI frames compress very well - typically 10-30x smaller
			// over the RPC pipe. The parent decompresses before presenting.
			var data = DesignerFrameCodec.EncodeDeflateBase64(buffer);
			stopwatch.Stop();
			return new DesignerRenderFrame
			{
				Width = rtb.PixelWidth,
				Height = rtb.PixelHeight,
				Dpi = Math.Max(1.0, dpi),
				Data = data,
				RenderMs = stopwatch.Elapsed.TotalMilliseconds
			};
		}

		static bool TrySetRasterizationScale(FrameworkElement element, double dpi)
		{
			try
			{
				var xamlRoot = element.XamlRoot;
				if (xamlRoot is null)
				{
					return false;
				}
				var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
				var visualTree = xamlRoot.GetType().GetProperty("VisualTree", flags)?.GetValue(xamlRoot);
				var rootScale = visualTree?.GetType().GetProperty("RootScale", flags)?.GetValue(visualTree);
				var field = rootScale?.GetType().GetField("_testOverrideScale", flags);
				if (field is null)
				{
					return false;
				}
				field.SetValue(rootScale, (float)dpi);
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Walks the live visual tree and reports every element with its bounds in root
		/// coordinates. Template parts are included; the parent maps a pick back to the
		/// nearest named ancestor, matching the document namescope rule.
		/// </summary>
		static DesignerElementNode BuildTree(DependencyObject node, UIElement root, string path)
		{
			var nodeInfo = new DesignerElementNode {
				Path = path
			};
			if (node is FrameworkElement fe)
			{
				nodeInfo.Name = string.IsNullOrEmpty(fe.Name) ? null : fe.Name;
				nodeInfo.Type = fe.GetType().Name;
				var bounds = GetBoundsInRoot(fe, root);
				nodeInfo.X = bounds.X;
				nodeInfo.Y = bounds.Y;
				nodeInfo.Width = bounds.Width;
				nodeInfo.Height = bounds.Height;
				// Only TabIndex is populated here (not the WPF host's full property reflection) -
				// this designer's Properties pad is driven off WinUIXamlElementPropertyAdapter
				// reading the host-owned XAML document directly, not this per-node list; TabIndex
				// is the one value the tab-order badge overlay needs that isn't otherwise
				// available without selecting the element first.
				if (fe is Control control)
				{
					nodeInfo.Properties.Add(new DesignerPropertyInfo {
						Name = "TabIndex", Value = control.TabIndex.ToString(CultureInfo.InvariantCulture)
					});
				}
			}
			else if (node is UIElement ue)
			{
				nodeInfo.Type = ue.GetType().Name;
			}

			nodeInfo.IsVisible = IsEffectivelyVisible(node, root);

			var count = VisualTreeHelper.GetChildrenCount(node);
			for (var i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(node, i);
				if (child is UIElement)
				{
					var childPath = path.Length == 0 ? i.ToString() : path + "," + i;
					nodeInfo.Children.Add(BuildTree(child, root, childPath));
				}
			}
			return nodeInfo;
		}

		/// <summary>Whether this element is actually on screen - see DesignerElementNode.IsVisible
		/// for why a client cannot safely draw coordinate-keyed overlays without it.
		///
		/// WinUI/Uno have no WPF-style <c>UIElement.IsVisible</c> (which folds the ancestor chain in
		/// for you), only the element's own local <c>Visibility</c>, so the chain is folded here. A
		/// collapsed element stays in the visual tree - collapsing is not removal - so its children
		/// are still enumerated by the walk above and would otherwise be reported as on screen.
		///
		/// The root is deliberately excluded: a design host's root is not a normal displayed
		/// element, and if it reported collapsed then EVERY node would come back invisible and a
		/// client filtering on this would draw no overlays at all.</summary>
		static bool IsEffectivelyVisible(DependencyObject node, UIElement root)
		{
			for (var current = node; current != null && current != root;
				current = VisualTreeHelper.GetParent(current))
			{
				if (current is UIElement element && element.Visibility != Visibility.Visible)
					return false;
			}
			return true;
		}

		/// <summary>
		/// An element's bounds in root coordinates, accumulated up the parent chain.
		///
		/// ActualOffset is the position Arrange assigned an element within its parent, and it is
		/// committed synchronously by the explicit Measure/Arrange the design host performs - no
		/// framework layout pass required. That matters because this host must NOT let a real
		/// layout pass run: in the Microsoft host the root is parented in a live offscreen
		/// window, and a pass there re-arranges it to the window's size, throwing away the
		/// explicit design-size Arrange and stretching the presented bitmap.
		///
		/// TransformToVisual is unusable here for the same underlying reason on both hosts: it
		/// reads committed visual transforms, which in Uno's headless tree short-circuit to the
		/// identity matrix and in the Microsoft host are not committed until that forbidden
		/// layout pass. LayoutInformation.GetLayoutSlot is populated under Uno but comes back as
		/// an all-zero Rect in the Microsoft host, which collapsed every element onto the root's
		/// origin - that is what drew the selection outline a whole row above the rendered
		/// control for anything but a panel's first child (a StackPanel's second child reported
		/// its parent's Y, not its own). The slot path is kept only as a fallback for a host
		/// that leaves ActualOffset at zero, where it is the value that used to be correct.
		/// </summary>
		static Rect GetBoundsInRoot(FrameworkElement element, UIElement root)
		{
			var x = 0.0;
			var y = 0.0;
			var trace = BoundsLogEnabled ? new System.Text.StringBuilder() : null;
			FrameworkElement current = element;
			while (current is not null && !ReferenceEquals(current, root))
			{
				var offset = current.ActualOffset;
				var slot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(current);
				var usedOffset = offset.X != 0f || offset.Y != 0f;
				if (usedOffset)
				{
					x += offset.X;
					y += offset.Y;
				}
				else
				{
					// WPF/WinUI arrange an element at slot + margin, so a margin'd child sits
					// offset inside its own slot.
					x += slot.X + current.Margin.Left;
					y += slot.Y + current.Margin.Top;
				}
				trace?.Append($" [{current.GetType().Name}{(string.IsNullOrEmpty(current.Name) ? "" : ":" + current.Name)}"
					+ $" actualOffset=({offset.X},{offset.Y}) slot=({slot.X},{slot.Y},{slot.Width},{slot.Height})"
					+ $" margin=({current.Margin.Left},{current.Margin.Top}) used={(usedOffset ? "offset" : "slot")}"
					+ $" -> running=({x},{y})]");
				current = VisualTreeHelper.GetParent(current) as FrameworkElement;
			}
			// WinUI's offscreen composition path can render the child before its public
			// ActualWidth/ActualHeight are committed. Its DesiredSize is already the value
			// used by the completed parent arrange (and is non-zero for the rendered child),
			// so use it only for that transient zero-sized reporting case.
			var width = element.ActualWidth > 0 ? element.ActualWidth : element.DesiredSize.Width;
			var height = element.ActualHeight > 0 ? element.ActualHeight : element.DesiredSize.Height;
			if (trace is not null)
			{
				BoundsLog($"  bounds {element.GetType().Name}{(string.IsNullOrEmpty(element.Name) ? "" : ":" + element.Name)}"
					+ $" = ({x},{y}) {width}x{height} | actual={element.ActualWidth}x{element.ActualHeight}"
					+ $" desired={element.DesiredSize.Width}x{element.DesiredSize.Height} |{trace}");
			}
			return new Rect(x, y, width, height);
		}

		/// <summary>Temporary positioning diagnostics: set OD_DESIGNHOST_BOUNDS_LOG=1 to append the
		/// per-element ActualOffset/layout-slot walk that produces each reported design-space
		/// rectangle. Off by default so a normal run pays nothing.</summary>
		static readonly bool BoundsLogEnabled =
			Environment.GetEnvironmentVariable("OD_DESIGNHOST_BOUNDS_LOG") == "1";

		static void BoundsLog(string message)
		{
			if (!BoundsLogEnabled)
				return;
			try
			{
				System.IO.File.AppendAllText(
					System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opendevelop-designhost-bounds.log"),
					$"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
			}
			catch
			{
				// Diagnostics must never break a design session.
			}
		}

		DesignerHitTestResult HitTestCore(HitTestRequest request)
		{
			var result = new DesignerHitTestResult();
			if (root is null)
			{
				return result;
			}
			// Uno's FindElementsInHostCoordinates skips the whole subtree of any Panel without a
			// Background (IsViewHitImpl), which in a headless design would swallow every control -
			// so walk the tree ourselves and test point-in-bounds, exactly like BuildTree measures
			// bounds, and report hits innermost-first.
			var point = new Point(request.X, request.Y);
			var hits = new List<UIElement>();
			var paths = new List<string>();
			CollectHits(root, root, point, hits, paths, "");
			foreach (var hit in hits)
			{
				var name = ResolveName(hit);
				if (name != null && !result.Chain.Contains(name))
				{
					result.Chain.Add(name);
				}
			}
			if (paths.Count > 0)
			{
				// Always report the innermost hit's tree path alongside the chain: template parts
				// can leak names (e.g. a ScrollViewer's internal "Root") that are not backed by
				// the source, so the shell decides whether the chain yields a selectable name or
				// falls back to auto-naming the picked element.
				result.PickPath = paths[0];
			}
			return result;
		}

		static void CollectHits(UIElement element, UIElement root, Point point, List<UIElement> hits, List<string> paths, string path)
		{
			var count = VisualTreeHelper.GetChildrenCount(element);
			for (var i = 0; i < count; i++)
			{
				if (VisualTreeHelper.GetChild(element, i) is UIElement child)
				{
					var childPath = path.Length == 0 ? i.ToString() : path + "," + i;
					CollectHits(child, root, point, hits, paths, childPath);
				}
			}
			if (element is FrameworkElement fe)
			{
				if (GetBoundsInRoot(fe, root).Contains(point))
				{
					hits.Add(element);
					paths.Add(path);
				}
			}
		}

		static string? ResolveName(DependencyObject element)
		{
			var current = element;
			while (current is not null)
			{
				if (current is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
				{
					return fe.Name;
				}
				current = VisualTreeHelper.GetParent(current);
			}
			return null;
		}
	}
}
