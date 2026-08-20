using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

using DesignSnapshot = ICSharpCode.SharpDevelop.Designer.Remote.DesignerSessionState;
using ElementNode = ICSharpCode.SharpDevelop.Designer.Remote.DesignerElementNode;
using DesignDiagnostic = ICSharpCode.SharpDevelop.Designer.Remote.DesignerDiagnostic;
using RenderResult = ICSharpCode.SharpDevelop.Designer.Remote.DesignerRenderFrame;
using ToolboxItemInfo = ICSharpCode.SharpDevelop.Designer.Remote.DesignerToolboxItemInfo;
using DocumentSnapshot = ICSharpCode.SharpDevelop.Designer.Remote.DesignerDocumentSnapshot;
using SourceFileSnapshot = ICSharpCode.SharpDevelop.Designer.Remote.DesignerSourceFileSnapshot;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// <see cref="IWinUIXamlRuntimeHost"/> backed by the out-of-process Uno design host:
/// the child process runs a real Uno runtime, loads the document's XAML with
/// XamlReader, lays it out and renders it to a PNG that is displayed here. All state
/// crossings are JSON over loopback TCP - no WinUI type ever enters this process.
/// </summary>
sealed class UnoDesignRuntimeHost : IWinUIXamlRuntimeHost, IWinUIXamlSelectionOverlay, IWinUIXamlDesignView, IWinUIXamlDirectManipulation, IWinUIXamlTextEditing, IWinUIXamlToolboxCatalog, IWinUIXamlLifecycleProbe, IWinUIXamlPathPick, IWinUIXamlTheme, IWinUIXamlMultiSelection, IWinUIXamlContextCommands, IWinUIXamlGridGuides, IWinUIXamlDiagnostics, IWinUIXamlIncrementalRender
{
	readonly UnoDesignSurfaceControl surface = new();
	readonly HashSet<string> selectableNames = new(StringComparer.Ordinal);
	readonly System.Windows.Threading.Dispatcher dispatcher;
	readonly string projectDirectory;
	readonly string documentFileName;
	UnoDesignClient client;
	Task connectTask;
	DesignSnapshot lastSnapshot;
	Dictionary<string, ElementNode> nodesByName = new();
	string lastPickDiagnostic = "no click yet";
	string lastLoadedText;
	System.Windows.Threading.DispatcherTimer scaleTimer;
	double lastRenderDpi = 1.0;
	int version;
	bool sessionOpened;
	double? configuredDesignWidth;
	double? configuredDesignHeight;
	bool disposed;

	public UnoDesignRuntimeHost(XamlFrameworkContext framework, string documentFileName)
	{
		// The host may be constructed on the UI thread but fed XAML from a background loader
		// (AbstractViewContentHandlingLoadErrors.LoadInternal), and every async continuation
		// here settles on a thread-pool thread - so capture the UI dispatcher explicitly and
		// marshal all state/surface updates through it.
		dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
		this.documentFileName = documentFileName;
		projectDirectory = framework?.ProjectFileName == null
			? null
			: Path.GetDirectoryName(framework.ProjectFileName);
		StatusText = "Starting Uno design host…";
		surface.DesignThemeRequested += OnSurfaceThemeRequested;
		surface.SizePresetRequested += OnSurfaceSizePresetRequested;
		surface.ContextCommandRequested += OnSurfaceContextCommandRequested;
		surface.NudgeRequested += OnSurfaceNudgeRequested;
		surface.UndoRedoRequested += OnSurfaceUndoRedoRequested;
		surface.SurfacePointerPressed += OnSurfacePointerPressed;
		surface.ElementResolver = ResolveNameAt;
		surface.SurfaceElementDragStarted += OnSurfaceElementDragStarted;
		surface.SurfaceElementDragDelta += OnSurfaceElementDragDelta;
		surface.SurfaceElementDragCommitted += OnSurfaceElementDragCommitted;
		surface.SurfaceElementDoubleClicked += OnSurfaceElementDoubleClicked;
		surface.TextEditCommitted += OnSurfaceTextEditCommitted;
		surface.GridGuideDragCommitted += OnSurfaceGridGuideDragCommitted;
		LoadSettings();
		if (settingsGridlines)
			surface.SetGridlines(true);
		if (settingsSizePreset is { } size)
			SetDesignSize(size.Width, size.Height);
		connectTask = ConnectAsync(framework?.Kind.ToString() ?? "unknown");

		// LibreWPF raises no window DpiChanged event, so a monitor-scale change (window
		// dragged to a differently-scaled display, display scaling changed in System
		// Settings) is caught by a cheap poller: it re-measures the scale and re-renders
		// at the new resolution when it moved.
		scaleTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, dispatcher) {
			Interval = TimeSpan.FromSeconds(2)
		};
		scaleTimer.Tick += OnScalePoll;
		scaleTimer.Start();
	}

	#region IWinUIXamlLifecycleProbe

	public bool IsChildProcessAlive => client is { } c && c.IsProcessAlive;

	#endregion

	void OnSurfaceThemeRequested(object sender, string theme)
		=> SetDesignTheme(theme);

	/// <summary>Raised with a design-surface context-menu command and the primary selection.</summary>
	public event EventHandler<(string Command, string Name)> ContextCommandRequested;

	void OnSurfaceContextCommandRequested(object sender, (string Command, string Name) args)
		=> ContextCommandRequested?.Invoke(this, args);

	/// <summary>Raised when a Grid row/column divider drag commits (name, isRow, index, design position).</summary>
	public event EventHandler<(string Name, bool IsRow, int Index, double Position)> GridGuideDragCommitted;

	void OnSurfaceGridGuideDragCommitted(object sender, (string Name, bool IsRow, int Index, double Position) args)
		=> GridGuideDragCommitted?.Invoke(this, args);

	/// <summary>Shows the row/column divider guides over the named Grid (design-space rect
	/// plus divider offsets); empty offsets hide them.</summary>
	public void SetGridGuides(string name, double x, double y, double width, double height, double[] rowOffsets, double[] colOffsets)
		=> dispatcher.BeginInvoke(() => surface.SetGridGuides(name, x, y, width, height, rowOffsets, colOffsets));

	/// <summary>Hides the Grid divider guides.</summary>
	public void ClearGridGuides()
		=> dispatcher.BeginInvoke(() => surface.SetGridGuides(null, 0, 0, 0, 0, Array.Empty<double>(), Array.Empty<double>()));

	/// <summary>Raised when the user nudges the selection with arrow keys (design units).</summary>
	public event EventHandler<(double DX, double DY)> NudgeRequested;

	void OnSurfaceNudgeRequested(object sender, (double DX, double DY) delta)
		=> NudgeRequested?.Invoke(this, delta);

	/// <summary>Raised when the user presses Ctrl+Z/Ctrl+Y on the surface (undo: true/false).</summary>
	public event EventHandler<bool> UndoRedoRequested;

	void OnSurfaceUndoRedoRequested(object sender, bool undo)
		=> UndoRedoRequested?.Invoke(this, undo);

	void OnSurfaceSizePresetRequested(object sender, string preset)
	{
		var (width, height) = preset switch
		{
			"phone" => (390.0, 844.0),
			"tablet" => (768.0, 1024.0),
			"desktop" => (1280.0, 720.0),
			_ => (0.0, 0.0)
		};
		if (width > 0 && height > 0)
			SetDesignSize(width, height);
	}

	#region IWinUIXamlTheme

	public void SetDesignTheme(string theme)
	{
		if (client == null)
		{
			return;
		}
		var text = Volatile.Read(ref lastLoadedText);
		if (text == null)
		{
			return;
		}
		if (!string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		Volatile.Write(ref currentTheme, theme);
		settingsTheme = theme;
		SaveSettings();
		if (dispatcher.CheckAccess())
			surface.SetTheme(theme);
		else
			dispatcher.BeginInvoke(() => surface.SetTheme(theme));
		_ = ApplyThemeAsync(theme, text, Interlocked.Increment(ref version));
	}

	async Task ApplyThemeAsync(string theme, string text, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			snapshot = await client.SetThemeAsync(theme);
		}
		catch (Exception e)
		{
			if (disposed || Volatile.Read(ref version) != requested)
				return;
			SetStatus("Uno theme switch failed: " + e.GetBaseException().Message);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>The current design theme, mirrored to the surface toggle ("Light" or "Dark").</summary>
	string currentTheme = "Light";

	/// <summary>Simulated monitor scale for the debug-dpi test hook.</summary>
	double? simulatedDpi;

	/// <summary>Sets (or clears) the simulated display scale; the poller then detects the
	/// change and re-renders, exercising the same path a real monitor move would.</summary>
	public void SetSimulatedDpi(double? dpi)
	{
		simulatedDpi = dpi;
		// Poll immediately so the change is picked up without waiting for the 2s timer.
		dispatcher.BeginInvoke(() => OnScalePoll(null, EventArgs.Empty));
	}

	#region Designer settings persistence

	/// <summary>
	/// The designer's last-used view options (theme, gridlines, canvas-size preset) are
	/// persisted across sessions so reopening a document restores them. Written from the
	/// runtime so both the toolbar buttons and the DevFlow actions keep it in sync.
	/// </summary>
	static readonly string SettingsPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"SharpIDE", "WinUIXamlDesigner.json");

	string settingsTheme = "Light";
	bool settingsGridlines;
	(double Width, double Height)? settingsSizePreset;
	bool settingsApplied;

	class DesignerSettingsData
	{
		public string Theme { get; set; } = "Light";
		public bool Gridlines { get; set; }
		public double? SizeWidth { get; set; }
		public double? SizeHeight { get; set; }
	}

	void LoadSettings()
	{
		try
		{
			if (File.Exists(SettingsPath))
			{
				var data = System.Text.Json.JsonSerializer.Deserialize<DesignerSettingsData>(File.ReadAllText(SettingsPath));
				settingsTheme = data?.Theme ?? "Light";
				settingsGridlines = data?.Gridlines ?? false;
				if (data is { SizeWidth: > 0, SizeHeight: > 0 })
					settingsSizePreset = (data.SizeWidth.Value, data.SizeHeight.Value);
			}
		}
		catch
		{
			// Corrupt or unreadable settings: start from defaults.
		}
	}

	void SaveSettings()
	{
		try
		{
			var dir = Path.GetDirectoryName(SettingsPath);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);
			var data = new DesignerSettingsData {
				Theme = settingsTheme,
				Gridlines = settingsGridlines,
				SizeWidth = settingsSizePreset?.Width,
				SizeHeight = settingsSizePreset?.Height
			};
			File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(data));
		}
		catch
		{
			// Persistence is best-effort; losing it must not break the designer.
		}
	}

	/// <summary>Applies the persisted theme after the first render settles (the child only
	/// re-resolves ThemeResource on a reload under a switched theme).</summary>
	void ApplyPersistedSettingsIfNeeded()
	{
		if (settingsApplied)
			return;
		settingsApplied = true;
		if (settingsTheme != "Light" && Volatile.Read(ref lastLoadedText) != null && client != null)
		{
			SetDesignTheme(settingsTheme);
		}
	}

	#endregion

	public string GetDesignTheme()
		=> Volatile.Read(ref currentTheme);

	/// <summary>
	/// Samples the last rendered bitmap at fixed points (center, corners, mid-left) and
	/// returns them as "#RRGGBB" strings - for pixel-level verification that a re-render
	/// (e.g. a theme switch) actually changed the drawing.
	/// </summary>
	public string RenderSample()
	{
		var snapshot = lastSnapshot;
		if (snapshot?.Render == null || string.IsNullOrEmpty(snapshot.Render.Data))
		{
			return "no frame";
		}
		try
		{
			var bytes = RenderCodec.Decode(snapshot.Render.Data);
			var w = snapshot.Render.Width;
			var h = snapshot.Render.Height;
			if (w <= 0 || h <= 0 || bytes.Length < w * h * 4)
			{
				return "bad frame";
			}
			static string Sample(byte[] px, int w, int h, double fx, double fy)
			{
				var i = ((int)(fy * h) * w + (int)(fx * w)) * 4;
				// BGRA order from Uno's RenderTargetBitmap.
				return $"#{px[i + 2]:X2}{px[i + 1]:X2}{px[i]:X2}";
			}
			var center = Sample(bytes, w, h, 0.5, 0.5);
			var topLeft = Sample(bytes, w, h, 0.03, 0.05);
			var midLeft = Sample(bytes, w, h, 0.05, 0.5);
			return $"{w}x{h} center={center} topleft={topLeft} midleft={midLeft}";
		}
		catch
		{
			return "decode failed";
		}
	}

	/// <summary>Whether the design-space gridlines overlay is currently shown.</summary>
	public bool Gridlines
		=> dispatcher.Invoke(() => surface.Gridlines);

	/// <summary>Shows or hides the design-space gridlines overlay.</summary>
	public void SetGridlines(bool show)
	{
		settingsGridlines = show;
		SaveSettings();
		dispatcher.BeginInvoke(() => surface.SetGridlines(show));
	}

	#endregion

	#region Tab order

	bool showTabOrder;

	/// <summary>Whether the tab-order badge overlay is currently shown.</summary>
	public bool ShowTabOrder => showTabOrder;

	/// <summary>Toggles the tab-order badge overlay - a small numbered badge near every element
	/// that reports a TabIndex property (<c>DesignHost.BuildTree</c> already populates it per
	/// node), matching <c>RemoteFormsDesignerControl.SetTabOrderMode</c>'s own toggle.</summary>
	public void SetTabOrderMode(bool show)
	{
		showTabOrder = show;
		RefreshTabOrderBadges();
	}

	/// <summary>Re-pushes the tab-order badges from the current <see cref="nodesByName"/> tree -
	/// called on toggle-on and whenever the tree is rebuilt while the view is already on, so
	/// badges stay in sync with edits.</summary>
	void RefreshTabOrderBadges()
	{
		if (!showTabOrder)
		{
			dispatcher.BeginInvoke(() => surface.SetTabOrderBadges(Array.Empty<(string, double, double, string)>()));
			return;
		}
		var badges = nodesByName.Values
			.Where(node => node.Name != null)
			.Select(node => (Name: node.Name!, node.X, node.Y,
				TabIndex: node.Properties?.FirstOrDefault(p => p.Name == "TabIndex")?.Value))
			.Where(item => !string.IsNullOrEmpty(item.TabIndex))
			.Select(item => (item.Name, item.X, item.Y, item.TabIndex!))
			.ToArray();
		dispatcher.BeginInvoke(() => surface.SetTabOrderBadges(badges));
	}

	#endregion

	#region IWinUIXamlPathPick

	public event EventHandler<string> ElementPathPicked;

	(string Name, string PickPath) ResolveNameAtWithPath(Vector2 point)
	{
		var design = surface.ToDesignPoint(new Point(point.X, point.Y));
		if (client == null)
		{
			lastPickDiagnostic = "no design host";
			return (null, null);
		}
		try
		{
			var result = client.HitTestAsync(Volatile.Read(ref version), design.X, design.Y).GetAwaiter().GetResult();
			lastPickDiagnostic = $"point={design.X:F0},{design.Y:F0} chain=[{string.Join(",", result.Chain)}]";
			foreach (var name in result.Chain)
			{
				if (selectableNames.Contains(name))
				{
					return (name, null);
				}
			}
			// The chain may still carry names that are not backed by the source (e.g. template
			// parts like a ScrollViewer's internal "Root"); when nothing selectable is under the
			// point, hand the pick path over so the shell can auto-name the element.
			return (null, result.PickPath);
		}
		catch (Exception e)
		{
			lastPickDiagnostic = "hit-test failed: " + e.Message;
			return (null, null);
		}
	}

	/// <summary>Returns the element at the given tree path plus its ancestors (root first),
	/// each with its index among same-type nodes in tree order.</summary>
	public IReadOnlyList<(string Type, int TypeIndex)> GetPickChain(string path)
	{
		var result = new List<(string, int)>();
		if (lastSnapshot?.Tree is not { } root)
		{
			return result;
		}
		var parts = path.Split(',').Select(p => int.TryParse(p, out var i) ? i : -1).ToArray();
		if (parts.Length == 0 || parts.Any(i => i < 0))
		{
			return result;
		}
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		var ancestors = new List<ElementNode>();
		var found = false;
		Walk(root, depth: 0, indexInParent: -1);
		return result;

		void Walk(ElementNode node, int depth, int indexInParent)
		{
			if (found)
			{
				return;
			}
			counts.TryGetValue(node.Type, out var c);
			counts[node.Type] = c + 1;
			var onPath = depth == 0 || (depth <= parts.Length && indexInParent == parts[depth - 1]);
			if (onPath)
			{
				if (depth < ancestors.Count)
				{
					ancestors[depth] = node;
				}
				else
				{
					ancestors.Add(node);
				}
				if (depth == parts.Length)
				{
					for (var d = 0; d <= depth; d++)
					{
						result.Add((ancestors[d].Type, counts[ancestors[d].Type] - 1));
					}
					found = true;
					return;
				}
			}
			for (var i = 0; i < node.Children.Count; i++)
			{
				Walk(node.Children[i], depth + 1, i);
			}
		}
	}

	#endregion

	#region IWinUIXamlToolboxCatalog

	IReadOnlyList<ToolboxItemInfo> catalogCache;

	public IReadOnlyList<ToolboxItemInfo> GetToolboxCatalog()
	{
		var catalog = Volatile.Read(ref catalogCache);
		if (catalog != null)
		{
			return catalog;
		}
		// The child reported the catalog at connect time; if it has not arrived yet, the
		// toolbox keeps its previous content and this is re-queried on the next state change.
		return Array.Empty<ToolboxItemInfo>();
	}

	#endregion

	#region IWinUIXamlTextEditing

	public event EventHandler<ElementDoubleClickInfo> ElementDoubleClicked;
	void OnSurfaceElementDoubleClicked(object sender, Vector2 point)
	{
		var name = ResolveNameAt(point);
		if (name == null || !nodesByName.TryGetValue(name, out var node))
		{
			ElementDoubleClicked?.Invoke(this, null);
			return;
		}
		ElementDoubleClicked?.Invoke(this, new ElementDoubleClickInfo {
			Name = name,
			X = node.X,
			Y = node.Y,
			Width = node.Width,
			Height = node.Height
		});
	}

	public void BeginTextEdit(double x, double y, double width, double height, string text)
		=> dispatcher.Invoke(() => surface.BeginTextEdit(x, y, width, height, text));

	public event EventHandler<string> TextEditCommitted;

	void OnSurfaceTextEditCommitted(object sender, string text)
		=> TextEditCommitted?.Invoke(this, text);

	#endregion

	async Task ConnectAsync(string kind)
	{
		try
		{
			var (runtimeConfig, depsFile) = ProjectDependencyContext();
			client = await UnoDesignClient.StartAsync(runtimeConfig, depsFile, CancellationToken.None);
			var capabilities = await client.GetCapabilitiesAsync();
			Volatile.Write(ref catalogCache, capabilities.Toolbox
				.Select(tool => new ToolboxItemInfo {
					Name = tool.Name,
					DisplayName = tool.DisplayName,
					Category = tool.Category,
					Template = tool.Template,
					XamlNamespace = tool.XamlNamespace
				}).ToList());
			var appNote = await EnsureAppResourcesAsync();
			SetStatus($"Uno design host ready ({capabilities.Runtime} {capabilities.Version}) for {kind}.{appNote}");
		}
		catch (Exception e)
		{
			client?.Dispose();
			client = null;
			SetStatus("Uno design host failed to start: " + e.GetBaseException().Message);
		}
	}

	/// <summary>
	/// Sends the owning project's App.xaml resources to the child so StaticResource and
	/// ThemeResource resolve against the real app. Returns a status suffix describing any
	/// skip reason (or empty when nothing was skipped). If a design already rendered
	/// without resources, it is re-rendered so the resources take effect.
	/// </summary>
	async Task<string> EnsureAppResourcesAsync()
	{
		var appXaml = FindAppXaml();
		if (appXaml == null)
		{
			return "";
		}
		var errors = new List<string>();
		var xaml = AppResourceBuilder.Build(appXaml, errors);
		if (xaml == null)
		{
			return errors.Count == 0 ? "" : " App.xaml skipped: " + string.Join("; ", errors);
		}
		// The theme combo lists exactly the themes the app carries (its
		// ThemeDictionaries keys); the default Light/Dark pair stays when the app has none.
		var themes = AppResourceBuilder.GetThemeNames(xaml);
		if (themes.Count > 0)
		{
			surface.SetDesignThemes(themes);
		}
		try
		{
			var result = await client.SetAppResourcesAsync(xaml);
			if (!result.Success)
			{
				return " App.xaml skipped: " + result.Error;
			}
		}
		catch (Exception e)
		{
			return " App.xaml skipped: " + e.GetBaseException().Message;
		}
		var text = Volatile.Read(ref lastLoadedText);
		if (text != null)
		{
			_ = RenderAsync(text, Interlocked.Increment(ref version));
		}
		return "";
	}

	/// <summary>
	/// The designed project's runtimeconfig.json and deps.json, so the child runs inside the
	/// project's own dependency graph (its real Uno version, custom controls, converters).
	/// Returns nulls when the owning project is unknown or was never built - the child then
	/// falls back to its own deployment.
	/// </summary>
	(string RuntimeConfig, string DepsFile) ProjectDependencyContext()
	{
		try
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(documentFileName));
			var outputAssembly = project?.OutputAssemblyFullPath;
			if (string.IsNullOrEmpty(outputAssembly))
			{
				return (null, null);
			}
			var runtimeConfig = Path.ChangeExtension(outputAssembly, ".runtimeconfig.json");
			var depsFile = Path.ChangeExtension(outputAssembly, ".deps.json");
			return (File.Exists(runtimeConfig) ? runtimeConfig : null,
				File.Exists(depsFile) ? depsFile : null);
		}
		catch
		{
			return (null, null);
		}
	}

	string FindAppXaml()
	{
		if (string.IsNullOrEmpty(projectDirectory))
		{
			return null;
		}
		var candidate = Path.Combine(projectDirectory, "App.xaml");
		return File.Exists(candidate) ? candidate : null;
	}

	void SetStatus(string text)
	{
		if (dispatcher.CheckAccess())
			ApplyStatus(text);
		else
			dispatcher.BeginInvoke(() => ApplyStatus(text));
	}

	void ApplyStatus(string text)
	{
		if (disposed)
			return;
		StatusText = text;
		StateChanged?.Invoke(this, EventArgs.Empty);
	}

	public UIElement WpfSurface => surface;
	public bool HasRenderedPreview { get; private set; }
	public string StatusText { get; private set; }

	/// <summary>The rendered element tree (protocol model), for the Document Outline pad.</summary>
	public DesignerElementNode? ElementTree => lastSnapshot?.Tree;

	/// <summary>Surface geometry (frame/selection/handle/element) for resize-drag tests.</summary>
	public DesignerSurfaceGeometry SurfaceGeometry()
		=> surface.SurfaceGeometry();

	/// <summary>Last lines of the child host's stdout/stderr (ready banners, render logs).</summary>
	public string ChildLog => client?.ChildLog ?? "(child not started)";

	/// <summary>The last render's diagnostics (message + source line/column when known).</summary>
	public IReadOnlyList<(string Message, int Line, int Column)> LastDiagnostics
		=> (lastSnapshot?.Diagnostics ?? new List<DesignDiagnostic>())
			.Select(d => (d.Message, d.Line, d.Column)).ToList();

	/// <summary>Exports the current design to a PNG file via the child host.</summary>
	public string ExportPng(string path)
		=> client == null ? "(child not started)" : client.ExportPngAsync(path).GetAwaiter().GetResult();

	/// <summary>The effective display scale (including any debug simulation).</summary>
	public double EffectiveDisplayDpi => DisplayDpi();

	/// <summary>Performance report of the last render: rasterize+compress time, pixel size,
	/// and the compressed wire size (before base64).</summary>
	public (double RenderMs, int Width, int Height, double Dpi, int CompressedBytes, int RawBytes) RenderTiming()
	{
		var render = lastSnapshot?.Render;
		if (render == null)
			return (0, 0, 0, 0, 0, 0);
		var raw = render.Width * render.Height * 4;
		var compressed = render.Data.Length * 3 / 4;
		return (render.RenderMs, render.Width, render.Height, render.Dpi, compressed, raw);
	}
	public event EventHandler StateChanged;
	public event EventHandler<string> ElementPicked;

	public int ResolvedNameCount => nodesByName.Count;
	public string LastPickDiagnostic => lastPickDiagnostic;

	public void SetSelectableNames(IReadOnlyList<string> names)
	{
		selectableNames.Clear();
		if (names != null)
			foreach (var name in names)
				selectableNames.Add(name);
	}

	public void LoadXaml(string text)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		Volatile.Write(ref lastLoadedText, text);
		if (client == null)
		{
			// Still starting (or failed). Retry once the connect settles; a failed connect
			// already reports via StatusText, so just drop the retry in that case.
			_ = connectTask.ContinueWith(_ => dispatcher.BeginInvoke(() => OnConnectedRetry(text)));
			return;
		}
		_ = RenderAsync(text, Interlocked.Increment(ref version));
	}

	void OnConnectedRetry(string text)
	{
		if (disposed || client == null)
			return;
		SetStatus("Rendering…");
		_ = RenderAsync(text, Interlocked.Increment(ref version));
	}

	async Task RenderAsync(string text, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			var (width, height) = DesignSize(text);
			var dpi = DisplayDpi();
			lastRenderDpi = dpi;
			// Surface size/DPI is presentation state and stays out of the document snapshot.
			client.SetViewport(width, height, dpi);
			var document = new DocumentSnapshot {
				SessionId = client.SessionId,
				DocumentId = client.DocumentId,
				Version = requested,
				PrimaryFileName = documentFileName,
				Language = "",
				Files = { new SourceFileSnapshot { FileName = documentFileName, Kind = "Source", Text = text } }
			};
			if (!sessionOpened)
			{
				snapshot = await client.OpenAsync(document);
				sessionOpened = true;
			}
			else
			{
				snapshot = await client.UpdateAsync(document);
			}
		}
		catch (Exception e)
		{
			if (disposed || Volatile.Read(ref version) != requested)
				return;
			SetStatus("Uno render failed: " + e.GetBaseException().Message);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>
	/// The display scale of the monitor hosting the design surface, measured on the UI
	/// thread. The child renders at this scale so the bitmap is pixel-perfect on Retina
	/// displays; the returned render Dpi is what actually sizes the surface. The
	/// UNO_DESIGN_DPI environment variable overrides the measured scale, to exercise the
	/// dpi-aware render path on a 1x display.
	/// </summary>
	double DisplayDpi()
	{
		// Test hook (od.winui-designer.debug-dpi): simulate a monitor scale change so the
		// scale poller's re-render path can be verified without real multi-monitor setups.
		if (simulatedDpi is { } simulated)
		{
			return simulated;
		}
		if (double.TryParse(Environment.GetEnvironmentVariable("UNO_DESIGN_DPI"), NumberStyles.Float, CultureInfo.InvariantCulture, out var overrideDpi) && overrideDpi > 0)
		{
			return overrideDpi;
		}
		try
		{
			return dispatcher.Invoke(() =>
			{
				// On Windows the presentation source's TransformToDevice IS the monitor
				// scale and is authoritative. LibreWPF bridges both PresentationSource and
				// VisualTreeHelper.GetDpi to a constant 1.0 though, so a 1.0 reading is
				// treated as "unknown" here and falls through to the native AppKit scale.
				var source = PresentationSource.FromVisual(surface);
				if (source?.CompositionTarget != null)
				{
					var d = Math.Max(1.0, source.CompositionTarget.TransformToDevice.M11);
					if (d > 1.01)
					{
						return d;
					}
				}
				var measured = Math.Max(1.0, VisualTreeHelper.GetDpi(surface).DpiScaleX);
				if (measured > 1.01)
				{
					return measured;
				}
				var native = NativeMainScreenScale();
				return native > 1.0 ? native : measured;
			});
		}
		catch
		{
			return 1.0;
		}
	}

	/// <summary>
	/// AppKit backingScaleFactor of the main screen, read through the ObjC runtime. This is
	/// the actual monitor scale on macOS, where LibreWPF does not bridge WPF's DPI APIs to
	/// AppKit (both return 1.0). The window's own screen is not reachable through LibreWPF,
	/// so a window moved to a differently-scaled monitor is caught by the scale poller
	/// (ScalePoller) rather than by a window-level event.
	/// </summary>
	static double NativeMainScreenScale()
	{
		try
		{
			var nsScreen = objc_getClass("NSScreen");
			if (nsScreen == IntPtr.Zero)
			{
				return 1.0;
			}
			var main = objc_msgSend(nsScreen, sel_registerName("mainScreen"));
			if (main == IntPtr.Zero)
			{
				return 1.0;
			}
			return Math.Max(1.0, objc_msgSend_double(main, sel_registerName("backingScaleFactor")));
		}
		catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
		{
			// Not macOS (no ObjC runtime) - the WPF APIs above are authoritative there.
			return 1.0;
		}
	}

	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
	static extern IntPtr objc_getClass(string name);

	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
	static extern IntPtr sel_registerName(string name);

	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")]
	static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

	[System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern double objc_msgSend_double(IntPtr receiver, IntPtr selector);

	void OnScalePoll(object sender, EventArgs e)
	{
		if (disposed || client == null || Volatile.Read(ref lastLoadedText) == null)
		{
			return;
		}
		var dpi = DisplayDpi();
		if (Math.Abs(dpi - lastRenderDpi) > 0.01)
		{
			lastRenderDpi = dpi;
			_ = RenderAsync(Volatile.Read(ref lastLoadedText), Interlocked.Increment(ref version));
		}
	}

	/// <summary>DDP design/set-property as a render-refresh optimization: the caller's own
	/// source-of-truth buffer has already been updated; this only decides which render request
	/// goes out. Falls back to a full <see cref="LoadXaml"/> whenever the session isn't open
	/// yet, the child rejects the edit, or the RPC throws - the caller never has to know which
	/// path actually ran.</summary>
	public void TrySetProperty(string elementName, string propertyName, string value, string fallbackXaml)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (client == null || !sessionOpened)
		{
			LoadXaml(fallbackXaml);
			return;
		}
		_ = SetPropertyIncrementalAsync(elementName, propertyName, value, fallbackXaml, Interlocked.Increment(ref version));
	}

	async Task SetPropertyIncrementalAsync(string elementName, string propertyName, string value, string fallbackXaml, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			snapshot = await client.SetPropertyAsync(requested, elementName, propertyName, value);
			if (!snapshot.Accepted)
			{
				LoadXaml(fallbackXaml);
				return;
			}
		}
		catch (Exception)
		{
			if (disposed) return;
			LoadXaml(fallbackXaml);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>DDP design/set-bounds as a render-refresh optimization; see
	/// <see cref="TrySetProperty"/> for the fallback contract. Only meant for a pure resize -
	/// callers must not use this when the element's position (Margin) also changed, since this
	/// design host's panels position children through Margin, not Canvas.Left/Top, and the
	/// child only applies x/y when the parent happens to be a Canvas.</summary>
	public void TrySetBounds(string elementName, double x, double y, double width, double height, string fallbackXaml)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (client == null || !sessionOpened)
		{
			LoadXaml(fallbackXaml);
			return;
		}
		_ = SetBoundsIncrementalAsync(elementName, x, y, width, height, fallbackXaml, Interlocked.Increment(ref version));
	}

	async Task SetBoundsIncrementalAsync(string elementName, double x, double y, double width, double height, string fallbackXaml, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			snapshot = await client.SetBoundsAsync(requested, elementName, x, y, width, height);
			if (!snapshot.Accepted)
			{
				LoadXaml(fallbackXaml);
				return;
			}
		}
		catch (Exception)
		{
			if (disposed) return;
			LoadXaml(fallbackXaml);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>DDP design/set-event as a render-refresh optimization; see
	/// <see cref="TrySetProperty"/> for the fallback contract.</summary>
	public void TrySetEvent(string elementName, string eventName, string handlerName, string fallbackXaml)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (client == null || !sessionOpened)
		{
			LoadXaml(fallbackXaml);
			return;
		}
		_ = SetEventIncrementalAsync(elementName, eventName, handlerName, fallbackXaml, Interlocked.Increment(ref version));
	}

	async Task SetEventIncrementalAsync(string elementName, string eventName, string handlerName, string fallbackXaml, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			snapshot = await client.SetEventAsync(requested, elementName, eventName, handlerName);
			if (!snapshot.Accepted)
			{
				LoadXaml(fallbackXaml);
				return;
			}
		}
		catch (Exception)
		{
			if (disposed) return;
			LoadXaml(fallbackXaml);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>DDP design/add-element as a render-refresh optimization; see
	/// <see cref="TrySetProperty"/> for the fallback contract. <paramref name="itemXaml"/> is the
	/// exact markup the caller's own editor already produced (x:Name included), so the incremental
	/// render always ends up with the same element the source-of-truth document now has.</summary>
	public void TryAddElement(string containerName, string itemXaml, string fallbackXaml)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (client == null || !sessionOpened)
		{
			LoadXaml(fallbackXaml);
			return;
		}
		_ = AddElementIncrementalAsync(containerName, itemXaml, fallbackXaml, Interlocked.Increment(ref version));
	}

	async Task AddElementIncrementalAsync(string containerName, string itemXaml, string fallbackXaml, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			// The markup backend takes the element name from the item XAML itself, so the
			// proposed name is only advisory here.
			snapshot = await client.AddElementAsync(requested, containerName, new ToolboxItemInfo { Template = itemXaml }, "", 0, 0);
			if (!snapshot.Accepted)
			{
				LoadXaml(fallbackXaml);
				return;
			}
		}
		catch (Exception)
		{
			if (disposed) return;
			LoadXaml(fallbackXaml);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>DDP design/delete-elements as a render-refresh optimization; see
	/// <see cref="TrySetProperty"/> for the fallback contract.</summary>
	public void TryDeleteElements(string[] elementNames, string fallbackXaml)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (client == null || !sessionOpened)
		{
			LoadXaml(fallbackXaml);
			return;
		}
		_ = DeleteElementsIncrementalAsync(elementNames, fallbackXaml, Interlocked.Increment(ref version));
	}

	async Task DeleteElementsIncrementalAsync(string[] elementNames, string fallbackXaml, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			snapshot = await client.DeleteElementsAsync(requested, elementNames);
			if (!snapshot.Accepted)
			{
				LoadXaml(fallbackXaml);
				return;
			}
		}
		catch (Exception)
		{
			if (disposed) return;
			LoadXaml(fallbackXaml);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	/// <summary>DDP design/rename as a render-refresh optimization; see
	/// <see cref="TrySetProperty"/> for the fallback contract. Landed as an unused capability - no
	/// call site in this shell renames an already-named element today.</summary>
	public void TryRename(string elementName, string newName, string fallbackXaml)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		if (client == null || !sessionOpened)
		{
			LoadXaml(fallbackXaml);
			return;
		}
		_ = RenameIncrementalAsync(elementName, newName, fallbackXaml, Interlocked.Increment(ref version));
	}

	async Task RenameIncrementalAsync(string elementName, string newName, string fallbackXaml, int requested)
	{
		DesignSnapshot snapshot;
		try
		{
			snapshot = await client.RenameAsync(requested, elementName, newName);
			if (!snapshot.Accepted)
			{
				LoadXaml(fallbackXaml);
				return;
			}
		}
		catch (Exception)
		{
			if (disposed) return;
			LoadXaml(fallbackXaml);
			return;
		}
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		if (!dispatcher.CheckAccess())
		{
			dispatcher.BeginInvoke(() => ApplySnapshot(snapshot, requested));
			return;
		}
		ApplySnapshot(snapshot, requested);
	}

	void ApplySnapshot(DesignSnapshot snapshot, int requested)
	{
		if (disposed || Volatile.Read(ref version) != requested)
			return;
		lastSnapshot = snapshot;
		nodesByName = IndexTree(snapshot.Tree);
		if (showTabOrder)
			RefreshTabOrderBadges();
		if (snapshot.Render != null)
		{
			surface.SetRender(snapshot.Render);
			HasRenderedPreview = true;
		}
		StatusText = snapshot.Diagnostics.Count == 0
			? $"Rendered by Uno design host ({FormatSize(snapshot.Render)})."
			: string.Join(Environment.NewLine, snapshot.Diagnostics.Select(d => d.Message));
		ApplyPersistedSettingsIfNeeded();
		StateChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>
	/// Reports the design size in logical units (the bitmap is dpi-scaled pixels), with
	/// the scale appended when it differs from 1x, e.g. "640×480 @ 2x".
	/// </summary>
	static string FormatSize(RenderResult render)
	{
		if (render == null)
			return "no frame";
		var logicalWidth = Math.Round(render.Width / render.Dpi);
		var logicalHeight = Math.Round(render.Height / render.Dpi);
		return render.Dpi > 1.01
			? $"{logicalWidth}×{logicalHeight} @ {render.Dpi:0.##}x"
			: $"{logicalWidth}×{logicalHeight}";
	}

	/// <summary>
	/// The design surface is sized to the document root's own Width/Height when declared;
	/// otherwise to the configured design size; otherwise to a desktop-like 1280x720, so
	/// pages without an explicit size get a meaningful canvas instead of a fixed 640x480.
	/// </summary>
	(double Width, double Height) DesignSize(string text)
	{
		var (documentWidth, documentHeight) = ParseDocumentSize(text);
		var width = documentWidth > 0 ? documentWidth : configuredDesignWidth ?? 1280;
		var height = documentHeight > 0 ? documentHeight : configuredDesignHeight ?? 720;
		return (width, height);
	}

	static (double Width, double Height) ParseDocumentSize(string text)
	{
		try
		{
			var root = XDocument.Parse(text).Root;
			if (root == null)
				return (0, 0);
			var width = ParseDimension((string)root.Attribute("Width"));
			var height = ParseDimension((string)root.Attribute("Height"));
			return (width, height);
		}
		catch
		{
			return (0, 0);
		}
	}

	static double ParseDimension(string value)
	{
		if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0)
			return result;
		return 0;
	}

	/// <summary>
	/// Answers a click with the innermost named element that also exists in the source
	/// document; a hit usually lands on a control-template part that has no x:Name.
	/// </summary>
	public string ResolveNameAt(Vector2 point) => ResolveNameAtWithPath(point).Name;

	void OnSurfacePointerPressed(object sender, (Vector2 Point, bool Ctrl) args)
	{
		var (name, pickPath) = ResolveNameAtWithPath(args.Point);
		if (name != null)
		{
			ApplyPickSelection(name, args.Ctrl);
		}
		else if (!string.IsNullOrEmpty(pickPath))
		{
			ElementPathPicked?.Invoke(this, pickPath);
		}
	}

	readonly List<string> multiSelectionNames = new();

	/// <summary>The primary (single) selection's element name, kept in sync with the surface.</summary>
	public string SelectedElementName { get; private set; }

	/// <summary>Raised when the design-surface selection (possibly multiple elements) changes.</summary>
	public event EventHandler<IReadOnlyList<string>> SelectionChanged;

	/// <summary>The currently selected element names, primary first.</summary>
	public IReadOnlyList<string> SelectedNames => multiSelectionNames.Count == 0 && SelectedElementName != null
		? new[] { SelectedElementName }
		: multiSelectionNames;

	/// <summary>Sets the multi-selection programmatically (e.g. from a scripted action);
	/// the first name becomes the primary selection.</summary>
	public void SelectElements(IReadOnlyList<string> names)
	{
		if (names == null)
			return;
		multiSelectionNames.Clear();
		foreach (var name in names)
		{
			if (selectableNames.Contains(name) && !multiSelectionNames.Contains(name))
				multiSelectionNames.Add(name);
		}
		if (multiSelectionNames.Count == 0)
			return;
		SelectElementInternal(multiSelectionNames[0]);
	}

	void ApplyPickSelection(string name, bool ctrl)
	{
		if (!selectableNames.Contains(name))
		{
			return;
		}
		if (ctrl)
		{
			if (!multiSelectionNames.Remove(name))
			{
				multiSelectionNames.Add(name);
			}
			if (multiSelectionNames.Count == 0)
			{
				// Ctrl-clicked the last one away: nothing selected.
				ClearSelectionInternal();
				return;
			}
		}
		else
		{
			multiSelectionNames.Clear();
			multiSelectionNames.Add(name);
		}
		SelectElementInternal(name);
	}

	void ClearSelectionInternal()
	{
		multiSelectionNames.Clear();
		surface.ClearSelection();
		surface.SetSecondarySelection(Array.Empty<(string, double, double, double, double)>());
		SelectedElementName = null;
		SelectionChanged?.Invoke(this, Array.Empty<string>());
	}

	void SelectElementInternal(string name)
	{
		SelectedElementName = name;
		RefreshSelectionOverlay();
		SelectionChanged?.Invoke(this, multiSelectionNames.Count > 0 ? multiSelectionNames.ToArray() : new[] { name });
	}

	void RefreshSelectionOverlay()
	{
		var secondary = new List<(string, double, double, double, double)>();
		foreach (var name in multiSelectionNames)
		{
			if (name != SelectedElementName && nodesByName.TryGetValue(name, out var node))
			{
				secondary.Add((name, node.X, node.Y, node.Width, node.Height));
			}
		}
		surface.SetSecondarySelection(secondary);
	}

	string dragName;
	string dragHandle;
	(double X, double Y, double Width, double Height) dragStartRect;
	// Multi-selection drag: the elements being dragged as a group (primary + secondaries).
	List<string> dragGroup = new();
	Dictionary<string, (double X, double Y, double Width, double Height)> dragGroupStart = new();
	double dragDeltaX;
	double dragDeltaY;

	void OnSurfaceElementDragStarted(object sender, (string Name, string Handle) info)
	{
		dragName = info.Name;
		dragHandle = info.Handle;
		dragDeltaX = 0;
		dragDeltaY = 0;
		dragStartRect = nodesByName.TryGetValue(info.Name, out var node)
			? (node.X, node.Y, node.Width, node.Height)
			: surface.CurrentSelection;
		// Dragging a multi-selected element moves the whole group; otherwise it is a
		// plain single-element drag. Handle resizes stay single-element.
		dragGroup = string.IsNullOrEmpty(dragHandle) && multiSelectionNames.Contains(info.Name)
			? new List<string>(multiSelectionNames)
			: new List<string> { info.Name };
		dragGroupStart = new Dictionary<string, (double, double, double, double)>(StringComparer.Ordinal);
		foreach (var name in dragGroup)
		{
			dragGroupStart[name] = nodesByName.TryGetValue(name, out var n)
				? (n.X, n.Y, n.Width, n.Height)
				: surface.CurrentSelection;
		}
		// Selecting the dragged element keeps the Properties pad and outline in sync.
		ElementPicked?.Invoke(this, info.Name);
	}

	void OnSurfaceElementDragDelta(object sender, (double DX, double DY) delta)
	{
		if (dragName == null)
			return;
		var scale = surface.ViewportScale;
		dragDeltaX = delta.DX / scale;
		dragDeltaY = delta.DY / scale;
		// Snap the primary element's edges/centre to nearby elements' edges/centres and
		// show alignment guides while dragging (move only - resizes are not snapped).
		var guides = (IReadOnlyList<(bool, double)>)Array.Empty<(bool, double)>();
		if (string.IsNullOrEmpty(dragHandle))
		{
			(dragDeltaX, dragDeltaY, guides) = ApplySnap(dragDeltaX, dragDeltaY);
		}
		surface.SetSnapGuides(guides);
		var rect = ApplyHandle(dragStartRect, dragDeltaX, dragDeltaY);
		surface.ShowSelection(rect.X, rect.Y, rect.Width, rect.Height, dragName);
		if (dragGroup.Count > 1)
		{
			// Move the secondary outlines with the group so the whole selection tracks.
			var secondary = new List<(string, double, double, double, double)>();
			foreach (var name in dragGroup)
			{
				if (name == dragName || !dragGroupStart.TryGetValue(name, out var start))
					continue;
				secondary.Add((name, start.X + dragDeltaX, start.Y + dragDeltaY, start.Width, start.Height));
			}
			surface.SetSecondarySelection(secondary);
		}
	}

	/// <summary>
	/// Snaps the dragged element's left/centre/right and top/middle/bottom lines to other
	/// elements' matching lines (within the snap tolerance), returning the corrected delta
	/// and the guide lines to draw.
	/// </summary>
	(double DX, double DY, IReadOnlyList<(bool IsVertical, double Position)> Guides) ApplySnap(double deltaX, double deltaY)
	{
		if (!dragGroupStart.TryGetValue(dragName, out var start))
			return (deltaX, deltaY, Array.Empty<(bool, double)>());
		var siblingBounds = nodesByName.Values
			.Where(node => node.Name != dragName)
			.Select(node => (node.X, node.Y, node.Width, node.Height));
		return SnapGuideCalculator.ApplySnap(start, deltaX, deltaY, siblingBounds);
	}

	/// <summary>Raised when a group drag (multi-selection move) commits, with each element's delta.</summary>
	public event EventHandler<IReadOnlyList<(string Name, double DX, double DY)>> ElementGroupDragCommitted;

	void OnSurfaceElementDragCommitted(object sender, (double DX, double DY) delta)
	{
		if (dragName == null)
			return;
		// NOTE: dragDeltaX/dragDeltaY were already updated by the last OnSurfaceElementDragDelta,
		// including any snap correction - do NOT recompute from the raw delta here, or the snap
		// correction (and its alignment guides) would be lost on commit.
		if (dragGroup.Count > 1)
		{
			var committed = new List<(string, double, double)>(dragGroup.Count);
			foreach (var name in dragGroup)
				committed.Add((name, dragDeltaX, dragDeltaY));
			surface.SetSnapGuides(Array.Empty<(bool, double)>());
			ElementGroupDragCommitted?.Invoke(this, committed);
			dragName = null;
			dragGroup = new();
			dragGroupStart.Clear();
			return;
		}
		var end = ApplyHandle(dragStartRect, dragDeltaX, dragDeltaY);
		surface.SetSnapGuides(Array.Empty<(bool, double)>());
		ElementDragCommitted?.Invoke(this, new ElementDragInfo {
			Name = dragName,
			StartX = dragStartRect.X,
			StartY = dragStartRect.Y,
			StartWidth = dragStartRect.Width,
			StartHeight = dragStartRect.Height,
			EndX = end.X,
			EndY = end.Y,
			EndWidth = end.Width,
			EndHeight = end.Height
		});
		dragName = null;
	}

	/// <summary>Applies a move/resize delta to a design rect for the given handle, keeping
	/// the result at least 1 unit wide/tall so a shrink-past-zero drag cannot crash the
	/// selection outline (Rect rejects negative sizes).</summary>
	(double X, double Y, double Width, double Height) ApplyHandle(
		(double X, double Y, double Width, double Height) rect, double dx, double dy)
	{
		double width;
		double height;
		double rx;
		double ry;
		switch (dragHandle)
		{
			case "e": rx = rect.X; ry = rect.Y; width = rect.Width + dx; height = rect.Height; break;
			case "s": rx = rect.X; ry = rect.Y; width = rect.Width; height = rect.Height + dy; break;
			case "se": rx = rect.X; ry = rect.Y; width = rect.Width + dx; height = rect.Height + dy; break;
			case "w": rx = rect.X + dx; ry = rect.Y; width = rect.Width - dx; height = rect.Height; break;
			case "n": rx = rect.X; ry = rect.Y + dy; width = rect.Width; height = rect.Height - dy; break;
			case "nw": rx = rect.X + dx; ry = rect.Y + dy; width = rect.Width - dx; height = rect.Height - dy; break;
			case "sw": rx = rect.X + dx; ry = rect.Y; width = rect.Width - dx; height = rect.Height + dy; break;
			case "ne": rx = rect.X; ry = rect.Y + dy; width = rect.Width + dx; height = rect.Height - dy; break;
			default: return (rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
		}
		if (width < 1) width = 1;
		if (height < 1) height = 1;
		return (rx, ry, width, height);
	}

	/// <summary>Raised with the committed drag: the element and its start/end design rect.</summary>
	public event EventHandler<ElementDragInfo> ElementDragCommitted;

	public (double X, double Y, double Width, double Height)? QueryElementBounds(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
			return null;
		return (node.X, node.Y, node.Width, node.Height);
	}

	/// <summary>Draws the selection outline over the named element's design bounds.</summary>
	public void ShowSelection(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
		{
			surface.ClearSelection();
			return;
		}
		surface.ShowSelection(node.X, node.Y, node.Width, node.Height, name);
	}

	/// <summary>Selects a single element (from outline/properties/actions), resetting any multi-selection.</summary>
	public void SelectElement(string name)
	{
		if (string.IsNullOrEmpty(name) || !selectableNames.Contains(name))
			return;
		multiSelectionNames.Clear();
		multiSelectionNames.Add(name);
		SelectedElementName = name;
		RefreshSelectionOverlay();
		ShowSelection(name);
		SelectionChanged?.Invoke(this, new[] { name });
	}
	public void ClearSelection() => surface.ClearSelection();

	#region IWinUIXamlDesignView

	public (double Zoom, double PanX, double PanY) GetViewport() => surface.Viewport;

	public double GetViewportScale() => surface.ViewportScale;

	public void SetViewport(double zoom, double panX, double panY)
		=> dispatcher.Invoke(() => surface.SetViewport(zoom, panX, panY));

	public void FitView() => dispatcher.Invoke(() => surface.FitView());

	public (double X, double Y) DesignToSurfacePoint(double x, double y)
	{
		var point = dispatcher.Invoke(() => surface.DesignToSurfacePoint(x, y));
		return (point.X, point.Y);
	}

	public (double X, double Y) DesignToScreenPoint(double x, double y)
	{
		var point = dispatcher.Invoke(() => surface.SurfacePointToScreen(x, y));
		return (point.X, point.Y);
	}

	public (double Width, double Height)? GetDesignSize()
	{
		if (configuredDesignWidth is null || configuredDesignHeight is null)
			return null;
		return (configuredDesignWidth.Value, configuredDesignHeight.Value);
	}

	public void SetDesignSize(double width, double height)
	{
		configuredDesignWidth = Math.Max(1, width);
		configuredDesignHeight = Math.Max(1, height);
		settingsSizePreset = (configuredDesignWidth.Value, configuredDesignHeight.Value);
		SaveSettings();
		var text = Volatile.Read(ref lastLoadedText);
		if (text != null)
		{
			_ = RenderAsync(text, Interlocked.Increment(ref version));
		}
	}

	public void ResetDesignSize()
	{
		configuredDesignWidth = null;
		configuredDesignHeight = null;
		settingsSizePreset = null;
		SaveSettings();
		var text = Volatile.Read(ref lastLoadedText);
		if (text != null)
		{
			_ = RenderAsync(text, Interlocked.Increment(ref version));
		}
	}

	#endregion

	public string DescribeElementState(string name)
	{
		if (string.IsNullOrEmpty(name) || !nodesByName.TryGetValue(name, out var node))
			return "not found";
		return $"type={node.Type} bounds=({node.X:F0},{node.Y:F0}) {node.Width:F0}x{node.Height:F0} children={node.Children.Count}";
	}

	public string FrameProfile()
	{
		var render = lastSnapshot?.Render;
		return render == null ? "no frame" : $"render {render.Width}x{render.Height} png={render.Data.Length / 1024}KB";
	}

	public string CompositorMetricsDump() => "not applicable (out-of-process Uno host)";
	public string RenderProbeAndProfile() => "not applicable (out-of-process Uno host)";
	public string DumpDrawCalls() => "not applicable (out-of-process Uno host)";
	public string WinUICommandProbe() => "not applicable (out-of-process Uno host)";

	public string DiagnoseScreenAnchors() => dispatcher.Invoke(() => surface.DiagnoseScreenAnchors());
	public string ImagePathProbe() => "not applicable (out-of-process Uno host)";
	public void SetShowDiagnosticOverlay(bool value) { }
	public void SetRecreateBitmapEachFrame(bool value) { }
	public void SetPresentViaBackgroundBrush(bool value) { }

	static Dictionary<string, ElementNode> IndexTree(ElementNode node)
	{
		var index = new Dictionary<string, ElementNode>(StringComparer.Ordinal);
		if (node == null)
			return index;
		void Walk(ElementNode current)
		{
			if (current.Name != null)
				index[current.Name] = current;
			foreach (var child in current.Children)
				Walk(child);
		}
		Walk(node);
		return index;
	}

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		scaleTimer?.Stop();
		surface.SurfacePointerPressed -= OnSurfacePointerPressed;
		nodesByName.Clear();
		lastSnapshot = null;
		client?.Dispose();
		client = null;
	}
}
