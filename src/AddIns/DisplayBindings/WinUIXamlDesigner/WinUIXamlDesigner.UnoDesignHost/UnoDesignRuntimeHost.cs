using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer;
using ICSharpCode.SharpDevelop.Designer.Surface;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Project.Sdk;

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
sealed class UnoDesignRuntimeHost : IDesignCanvasBackend, IWinUIXamlRuntimeHost, IWinUIXamlSelectionOverlay, IWinUIXamlDesignView, IWinUIXamlDirectManipulation, IWinUIXamlTextEditing, IWinUIXamlToolboxCatalog, IWinUIXamlLifecycleProbe, IWinUIXamlPathPick, IWinUIXamlTheme, IWinUIXamlVisualStates, IWinUIXamlMultiSelection, IWinUIXamlContextCommands, IWinUIXamlGridGuides, IWinUIXamlDiagnostics, IWinUIXamlIncrementalRender
{
	// The shared design canvas (ICSharpCode.DesignerCanvas addin): the surface, and the controller
	// that owns selection, drags and picking over it. This class is only the Uno/WinUI backend:
	// it talks to the child design host and answers the controller's hit tests.
	readonly DesignSurface surface = new();
	readonly DesignSurfaceController canvas;
	readonly System.Windows.Threading.Dispatcher dispatcher;
	readonly string projectDirectory;
	readonly string documentFileName;
	readonly string? hostDllPath;
	readonly Func<Architecture?, string?>? hostDllPathLocator;
	readonly string hostDisplayName;
	UnoDesignClient client;
	Task connectTask;
	DesignSnapshot lastSnapshot => canvas.LastSnapshot;
	string lastLoadedText;
	System.Windows.Threading.DispatcherTimer scaleTimer;
	double lastRenderDpi = 1.0;
	int version;
	bool sessionOpened;
	double? configuredDesignWidth;
	double? configuredDesignHeight;
	bool disposed;

	public UnoDesignRuntimeHost(XamlFrameworkContext framework, string documentFileName, string? hostDllPath = null,
		string? hostDisplayName = null, Func<Architecture?, string?>? hostDllPathLocator = null)
	{
		// The host may be constructed on the UI thread but fed XAML from a background loader
		// (AbstractViewContentHandlingLoadErrors.LoadInternal), and every async continuation
		// here settles on a thread-pool thread - so capture the UI dispatcher explicitly and
		// marshal all state/surface updates through it.
		dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
		this.documentFileName = documentFileName;
		this.hostDllPath = hostDllPath;
		this.hostDllPathLocator = hostDllPathLocator;
		this.hostDisplayName = hostDisplayName ?? "Uno design host";
		surface.BackendName = this.hostDisplayName.Contains("WinUI", StringComparison.OrdinalIgnoreCase) ? "WinUI"
			: this.hostDisplayName.Contains("ProGPU", StringComparison.OrdinalIgnoreCase) ? "ProGPU"
			: "Uno";
		projectDirectory = framework?.ProjectFileName == null
			? null
			: Path.GetDirectoryName(framework.ProjectFileName);
		StatusText = "Starting " + this.hostDisplayName + "…";
		surface.DesignThemeRequested += OnSurfaceThemeRequested;
		surface.DesignVisualStateRequested += OnSurfaceVisualStateRequested;
		surface.SizePresetRequested += OnSurfaceSizePresetRequested;
		canvas = new DesignSurfaceController(surface, this);
		canvas.ElementPicked += (_, name) => ElementPicked?.Invoke(this, name);
		canvas.ElementPathPicked += (_, path) => ElementPathPicked?.Invoke(this, path);
		canvas.SelectionChanged += (_, names) => SelectionChanged?.Invoke(this, names);
		canvas.ElementDragCommitted += (_, info) => ElementDragCommitted?.Invoke(this, info);
		canvas.ElementGroupDragCommitted += (_, moves) => ElementGroupDragCommitted?.Invoke(this, moves);
		canvas.ElementDoubleClicked += (_, info) => ElementDoubleClicked?.Invoke(this, info);
		canvas.TextEditCommitted += (_, text) => TextEditCommitted?.Invoke(this, text);
		canvas.GridGuideDragCommitted += (_, args) => GridGuideDragCommitted?.Invoke(this, args);
		canvas.ContextCommandRequested += (_, args) => ContextCommandRequested?.Invoke(this, args);
		canvas.NudgeRequested += (_, delta) => NudgeRequested?.Invoke(this, delta);
		canvas.UndoRedoRequested += (_, undo) => UndoRedoRequested?.Invoke(this, undo);
		LoadSettings();
		if (settingsGridlines)
			surface.SetGridlines(true);
		surface.IsGridEnabled = settingsGridlines;
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

	void OnSurfaceVisualStateRequested(object sender, (string Group, string? State) request)
		=> GoToVisualState(request.Group, request.State);

	/// <summary>The groups reported by the last snapshot, for the toolbar and for DevFlow.</summary>
	public IReadOnlyList<(string Group, IReadOnlyList<string> States, string CurrentState)> GetVisualStateGroups()
		=> lastSnapshot?.VisualStateGroups
			?.Select(g => (g.Name, (IReadOnlyList<string>)g.States, g.CurrentState ?? ""))
			.ToList()
			?? (IReadOnlyList<(string, IReadOnlyList<string>, string)>)Array.Empty<(string, IReadOnlyList<string>, string)>();

	/// <summary>Previews a VisualState on the design surface. A null state stops forcing that
	/// group. Fire-and-forget like the other surface commands: the resulting snapshot flows back
	/// through ApplySnapshot, which also re-syncs the combos.</summary>
	public void GoToVisualState(string group, string? state)
	{
		if (client == null || string.IsNullOrEmpty(group))
			return;
		var requested = Volatile.Read(ref version);
		// The switch is an async round-trip to the child, so show the shared "please wait" chrome
		// (the same overlay a first design load shows) instead of letting the canvas sit on the
		// old frame and then snap to the new one with no feedback.
		dispatcher.BeginInvoke(() => surface.BeginVisualStateLoading(group, state));
		_ = Task.Run(async () => {
			try
			{
				var snapshot = await client.GoToStateAsync(group, state);
				dispatcher.BeginInvoke(() => {
					ApplySnapshot(snapshot, requested);
					// ApplySnapshot can decline a stale snapshot; end the overlay either way so a
					// superseded switch can never leave the canvas stuck behind it.
					surface.EndVisualStateLoading();
				});
			}
			catch (Exception e)
			{
				dispatcher.BeginInvoke(() => {
					// No snapshot is coming, so nothing else will end the overlay.
					surface.EndVisualStateLoading();
					SetStatus("WinUI designer could not apply visual state '"
						+ (state ?? "(none)") + "': " + e.GetBaseException().Message);
				});
			}
		});
	}

	/// <summary>Raised with a design-surface context-menu command and the primary selection.</summary>
	public event EventHandler<(string Command, string Name)> ContextCommandRequested;


	/// <summary>Raised when a Grid row/column divider drag commits (name, isRow, index, design position).</summary>
	public event EventHandler<(string Name, bool IsRow, int Index, double Position)> GridGuideDragCommitted;


	/// <summary>Shows the row/column divider guides over the named Grid (design-space rect
	/// plus divider offsets); empty offsets hide them.</summary>
	public void SetGridGuides(string name, double x, double y, double width, double height, double[] rowOffsets, double[] colOffsets)
		=> canvas.SetGridGuides(name, x, y, width, height, rowOffsets, colOffsets);

	/// <summary>Hides the Grid divider guides.</summary>
	public void ClearGridGuides() => canvas.ClearGridGuides();

	/// <summary>Raised when the user nudges the selection with arrow keys (design units).</summary>
	public event EventHandler<(double DX, double DY)> NudgeRequested;


	/// <summary>Raised when the user presses Ctrl+Z/Ctrl+Y on the surface (undo: true/false).</summary>
	public event EventHandler<bool> UndoRedoRequested;


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
			var w = snapshot.Render.Width;
			var h = snapshot.Render.Height;
			if (w <= 0 || h <= 0)
			{
				return "bad frame";
			}
			var bytes = DesignerFrameCodec.DecodeBgra32(snapshot.Render);
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

	public bool ShowTabOrder => canvas.ShowTabOrder;

	/// <summary>Toggles the tab-order badges (drawn by the shared canvas from the tree's TabIndex
	/// properties, which <c>DesignHost.BuildTree</c> populates per node).</summary>
	public void SetTabOrderMode(bool show) => canvas.SetTabOrderMode(show);

	#endregion

	#region IWinUIXamlPathPick

	public event EventHandler<string> ElementPathPicked;

	/// <summary>
	/// IDesignCanvasBackend: the child's hit test at a design point. Called from the pointer-pressed
	/// handler, i.e. ON the UI thread, so the wait freezes the whole IDE window for as long as it
	/// lasts. The transport's own limit is the 30s operation timeout, which is an eternity to sit on
	/// a click: an unresponsive child made the main window look hung. Give up quickly instead and
	/// treat it as "nothing picked"; the next click issues a fresh request.
	/// </summary>
	public DesignCanvasHit HitTest(double x, double y)
	{
		if (client == null)
			return null;
		var pending = client.HitTestAsync(Volatile.Read(ref version), x, y);
		if (!pending.Wait(TimeSpan.FromSeconds(2)))
			return null;
		var result = pending.GetAwaiter().GetResult();
		return new DesignCanvasHit(result.Hit, result.PickPath, result.Chain);
	}

	public IReadOnlyList<(string Type, int TypeIndex, string Path)> GetPickChain(string path) => canvas.GetPickChain(path);

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

	public void BeginTextEdit(double x, double y, double width, double height, string text)
		=> canvas.BeginTextEdit(x, y, width, height, text);

	public event EventHandler<string> TextEditCommitted;

	#endregion

	async Task ConnectAsync(string kind)
	{
		try
		{
			var designProject = SD.ProjectService.FindProjectContainingFile(FileName.Create(documentFileName));
			if (designProject != null)
			{
				// Only the native WinUI child runs with the designed app's dependency graph (checked
				// below). The Uno compatibility renderer just needs the output assembly, so a class
				// library - which never gets a .runtimeconfig.json - must not look like a failed build.
				var requireRuntimeGraph = hostDisplayName.Contains("WinUI", StringComparison.OrdinalIgnoreCase);
				var build = await DesignerBuildCoordinator.EnsureBuiltAsync(designProject, requireRuntimeGraph,
					report: message => { SetStatus(message); ReportDesigner(message); });
				if (!build.IsUsable)
				{
					SetStatus("WinUI designer cannot use the active build: " + build.Error);
					return;
				}
			}
			var (runtimeConfig, depsFile, appBin, dependencyError, dotnetHostPath, dotnetHostArchitecture) = ProjectDependencyContext();
			// Native WinUI's XamlReader must run with the designed executable's dependency graph.
			// Starting the generic child when the selected configuration has no output makes every
			// third-party control look like an unrelated XAML type-resolution error (for example
			// SettingsCard was not found). Unlike the Uno compatibility renderer, that fallback
			// cannot produce a faithful Microsoft WinUI preview, so give the user an actionable
			// configuration/build prompt and do not start a misleading child process.
			if (hostDisplayName.Contains("WinUI", StringComparison.OrdinalIgnoreCase)
				&& (string.IsNullOrEmpty(runtimeConfig) || string.IsNullOrEmpty(depsFile)))
			{
				SetStatus(dependencyError ?? "WinUI designer requires a built project output for the active configuration. Select a buildable Windows App SDK configuration and build it, then close and reopen this document.");
				return;
			}
			// Project evaluation finishes after the display binding constructs this host. Resolve a
			// versioned Microsoft child here, beside dependency-context discovery, rather than
			// freezing the IDE's net10 default during factory registration.
			var child = hostDllPath ?? hostDllPathLocator?.Invoke(dotnetHostArchitecture);
			client = await UnoDesignClient.AcquireSharedAsync(runtimeConfig, depsFile, CancellationToken.None, child, appBin, dotnetHostPath, dotnetHostArchitecture);
			client.Recovered += OnClientRecovered;
			client.RecoveryFailed += OnClientRecoveryFailed;
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
			SetStatus($"{hostDisplayName} ready ({capabilities.Runtime} {capabilities.Version}) for {kind}.{appNote}");
		}
		catch (Exception e)
		{
			client?.Dispose();
			client = null;
			var failure = hostDisplayName + " failed to start: " + e.GetBaseException().Message;
			SetStatus(failure);
			// Host process lifecycle is not specific to one designer, so it goes to the shared IDE
			// channel; the child's own output stays on the designer's channel.
			DesignerOutput.AppendLine(DesignerOutput.Ide, failure);
		}
	}

	void OnClientRecovered(object? sender, DesignSnapshot state)
	{
		dispatcher.BeginInvoke(async () => {
			if (disposed || !ReferenceEquals(sender, client)) return;
			await EnsureAppResourcesAsync();
			var text = Volatile.Read(ref lastLoadedText);
			if (text != null) {
				_ = RenderAsync(text, Interlocked.Increment(ref version));
				return;
			}
			ApplySnapshot(state, Volatile.Read(ref version));
		});
	}

	/// <summary>Automatic recovery gave up (UnoDesignClient.consecutiveFailedRecoveries) rather
	/// than keep spawning child processes for a document that crashes every one of them. This is
	/// the only place that message reaches the user - the raw HostExited/RecoverAllAsync failure
	/// path otherwise has no user-visible surface at all.</summary>
	void OnClientRecoveryFailed(object? sender, Exception exception)
	{
		dispatcher.BeginInvoke(() => {
			if (disposed || !ReferenceEquals(sender, client)) return;
			var message = $"{hostDisplayName}: " + exception.Message;
			SetStatus(message);
			DesignerOutput.AppendLine(DesignerOutput.Channel(UnoDesignClient.OutputChannelName), message);
		});
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
		// surface is a WPF DispatcherObject and this method runs on the async continuation
		// thread (after the awaited RPC below), so marshal the combo update onto the UI
		// dispatcher like every other surface mutation in this file.
		var themes = AppResourceBuilder.GetThemeNames(xaml);
		if (themes.Count > 0)
		{
			dispatcher.BeginInvoke(() => surface.SetDesignThemes(themes));
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
	/// Reports a designer-level decision: to the log for support, and to the designer's own Output
	/// pad channel so the user can see it while working. These lines are the context that explains
	/// the single error the design surface shows - without them a missing launch argument reads as
	/// a plain XAML "type not found".
	/// </summary>
	static void ReportDesigner(string message, Exception e = null)
	{
		if (e == null)
			LoggingService.Warn(message);
		else
			LoggingService.Warn(message, e);
		DesignerOutput.AppendLine(DesignerOutput.Channel(UnoDesignClient.OutputChannelName), message);
	}

	/// <summary>
	/// The designed project's runtimeconfig.json and deps.json, so the child runs inside the
	/// project's own dependency graph (its real Uno version, custom controls, converters), plus
	/// its output directory for assembly preloading.
	///
	/// The runtime graph is adopted only when the app's framework is one this host can actually
	/// run on: <c>dotnet exec --runtimeconfig</c> pins the child to the version that file names, so
	/// an app on an OLDER major (a net9.0 app, a net10.0 host) kills the child before Main with
	/// "Could not load file or assembly 'System.Runtime, Version=10.0.0.0'". The app directory is
	/// still handed over on its own in that case - preloading the app's assemblies is what makes
	/// its <c>local:</c> types resolve, and a newer runtime loads assemblies built against an older
	/// one. Every "no graph" path is logged, because the designer otherwise reports a plain
	/// "type not found" XAML error for what is really a missing launch argument.
	/// </summary>
	(string? RuntimeConfig, string? DepsFile, string? AppBin, string? Error, string? DotnetHostPath, Architecture? DotnetHostArchitecture) ProjectDependencyContext()
	{
		try
		{
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(documentFileName));
			if (project == null)
			{
				var error = "WinUI designer cannot find the project for '" + documentFileName
					+ "'. Open the solution, select a buildable Windows App SDK configuration, and close then reopen this document.";
				ReportDesigner(error);
				return (null, null, null, error, null, null);
			}
			var outputAssembly = project.OutputAssemblyFullPath;
			if (string.IsNullOrEmpty(outputAssembly))
			{
				var active = SD.ProjectService.CurrentSolution?.ActiveConfiguration.ToString() ?? "the active configuration";
				var error = "WinUI designer cannot preview '" + project.Name + "' because " + active
					+ " has no project output. Select a buildable Windows App SDK configuration (for WinUI Gallery: Debug-Unpackaged|ARM64), build it, then close and reopen this document.";
				ReportDesigner(error);
				return (null, null, null, error, null, null);
			}
			var runtimeConfig = Path.ChangeExtension(outputAssembly, ".runtimeconfig.json");
			var depsFile = Path.ChangeExtension(outputAssembly, ".deps.json");
			var appBin = Path.GetDirectoryName(outputAssembly);
			if (!File.Exists(runtimeConfig) || !File.Exists(depsFile))
			{
				var error = "WinUI designer cannot preview '" + project.Name + "' because the active configuration is not built"
					+ " (missing " + Path.GetFileName(runtimeConfig) + " or " + Path.GetFileName(depsFile)
					+ "). Select a buildable Windows App SDK configuration, build it, then close and reopen this document.";
				ReportDesigner(error);
				return (null, null, Directory.Exists(appBin) ? appBin : null, error, null, null);
			}
			// A packaged (MSIX) output only works with package identity: its merged resources.pri
			// hangs the unpackaged child before the handshake ("A task was canceled"), and its
			// generated XAML metadata provider cannot be created there, so framework markup such as
			// AnimatedIcon.State fails to resolve.
			if (File.Exists(Path.Combine(appBin, "AppxManifest.xml")))
			{
				var active = SD.ProjectService.CurrentSolution?.ActiveConfiguration.ToString() ?? "the active configuration";
				var error = "WinUI designer cannot preview '" + project.Name + "' because " + active
					+ " builds a packaged (MSIX) app, which only runs with package identity. Select an unpackaged"
					+ " configuration (for WinUI Gallery: Debug-Unpackaged|ARM64), build it, then close and reopen this document.";
				ReportDesigner(error);
				return (null, null, null, error, null, null);
			}
			if (!CanHostRunOnAppArchitecture(appBin, out var appArchitecture, out var dotnetHostPath, out var dotnetHostArchitecture))
			{
				var error = "WinUI designer cannot preview '" + project.Name + "' because that configuration's output is "
					+ appArchitecture + ", OpenDevelop is running as " + RuntimeInformation.ProcessArchitecture
					+ ", and no installed .NET SDK for " + appArchitecture + " was found on this machine to launch the"
					+ " design host under instead. A self-contained app carries its own native runtime (hostpolicy.dll,"
					+ " coreclr.dll), so the design host must run under a matching-architecture \"dotnet\" to load it."
					+ " Install a " + appArchitecture + " .NET SDK (side-by-side installs are supported, e.g."
					+ " \"dotnet-install -Architecture " + appArchitecture.ToLowerInvariant() + "\"), or select a "
					+ RuntimeInformation.ProcessArchitecture + " configuration, build it, then close and reopen this document.";
				ReportDesigner(error);
				return (null, null, null, error, null, null);
			}
			if (!CanHostRunOnAppFramework(runtimeConfig, out var appVersion))
			{
				ReportDesigner("WinUI designer: '" + project.Name + "' pins .NET " + appVersion
					+ " which this design host (.NET " + Environment.Version.Major + ") cannot run on;"
					+ " preloading the app's assemblies from " + appBin + " without adopting its runtime graph.");
				return (null, null, appBin, "WinUI designer cannot run this configuration's runtime graph; select a compatible built configuration, then close and reopen this document.", null, null);
			}
			return (runtimeConfig, depsFile, appBin, null, dotnetHostPath, dotnetHostArchitecture);
		}
		catch (Exception e)
		{
			ReportDesigner("WinUI designer: could not determine the project dependency context for '"
				+ documentFileName + "'.", e);
			return (null, null, null, "WinUI designer could not determine the active project's output. Select a buildable Windows App SDK configuration, build it, then close and reopen this document.", null, null);
		}
	}

	/// <summary>
	/// Whether this host can actually load the app's output, architecture-wise - the companion to
	/// <see cref="CanHostRunOnAppFramework"/>, which asks the same question about the CLR version.
	///
	/// A self-contained app (WinUI Gallery's Debug-Unpackaged, and any
	/// <c>WindowsAppSdkSelfContained</c>/<c>PublishSingleFile</c> configuration) ships its own
	/// native runtime next to its managed output, built for that configuration's Platform. Adopting
	/// such a graph from a design host of another architecture kills the child process before it
	/// can report anything useful - the .NET host just prints
	/// "Failed to load [...\win-arm64\hostpolicy.dll], HRESULT: 0x800700C1". Detect it from the
	/// native host library actually sitting in the output directory rather than from the RID in the
	/// path, which is only a naming convention.
	///
	/// A framework-dependent app has no native runtime of its own in its output, so there is
	/// nothing to mismatch here and it is reported as usable.
	///
	/// The design host itself runs entirely out-of-process, bridged to this IDE process over
	/// loopback JSON-RPC (see <see cref="ICSharpCode.SharpDevelop.Designer.Remote.DesignerHostProcessClient"/>),
	/// so nothing about that bridge requires the child to share this process's architecture - only
	/// the "dotnet" muxer used to launch it does, because THAT process is what loads the app's
	/// native hostpolicy/coreclr. When the app's architecture differs from this process, look for
	/// an installed .NET SDK of the app's own architecture (<see
	/// cref="DotNetSdkService.ResolveDotnetHostForArchitecture"/>) - the .NET installer's
	/// side-by-side layout (<c>dotnet\x64</c>, <c>dotnet\arm64</c>, ...) makes exactly this
	/// available on a machine with more than one architecture's SDK installed, e.g. Windows on
	/// ARM64 commonly carries an x64 side install. Only when no matching-architecture SDK can be
	/// found at all is this reported as a real mismatch.
	/// </summary>
	/// <summary>Major Windows App Runtime version this designer's WinUI host was built against
	/// (<c>Microsoft.WindowsAppSDK</c> in Directory.Packages.props, and
	/// <c>WindowsAppSdkHostVersion</c> in WinUIXamlDesigner.MicrosoftHost.csproj, which defaults to
	/// the same value). Only used for the "not installed" and "older than" messages: any installed
	/// major supplies the XAML framework, so an unexpected value is reported, never blocked.</summary>
	const int RequiredWindowsAppRuntimeMajorVersion = 2;

	/// <summary>
	/// Whether a Windows App Runtime (the framework package behind Microsoft.UI.Xaml) is installed
	/// on this machine, and the highest major version found.
	/// </summary>
	/// <remarks>
	/// Enumerated as AppX packages, because that is how the runtime installs itself in practice: the
	/// framework package registers under WindowsApps and writes no classic Uninstall entry. Reading
	/// only HKLM/HKCU\SOFTWARE\...\Uninstall therefore reports "absent" on a machine that has the
	/// runtime - verified on an ARM64 dev box carrying Microsoft.WindowsAppRuntime.2 2.4.0.0 arm64,
	/// which a registry-only probe missed entirely. The uninstall key is still consulted as a
	/// fallback for the older EXE-bootstrapper layout.
	///
	/// The distinction that matters is "enumerated and found nothing" from "could not enumerate".
	/// Only the first may block; the second returns <c>true</c>, because this probe exists to
	/// replace a useless message with a useful one and must never become the reason a working
	/// preview stops working.
	/// </remarks>
	static bool WindowsAppRuntimeInstalled(out int majorVersion)
	{
		majorVersion = 0;
		var enumerated = false;
		foreach (var packageName in EnumerateAppRuntimePackageNames(out enumerated))
		{
			// "Microsoft.WindowsAppRuntime.2" -> 2. The CBS/framework variants
			// ("Microsoft.WindowsAppRuntime.CBS.2") also carry a major and are wanted.
			var dot = packageName.LastIndexOf('.');
			var tail = dot < 0 ? "" : packageName.Substring(dot + 1);
			var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
			if (digits.Length == 0 || !int.TryParse(digits, out var packageMajor) || packageMajor <= 0)
				continue;
			if (packageMajor > majorVersion)
				majorVersion = packageMajor;
			return true;
		}
		if (enumerated)
			return false;
		// Could not enumerate: the registry fallback may still answer.
		if (TryFindRuntimeInUninstallRegistry(out majorVersion))
			return true;
		return true;
	}

	/// <summary>Names of installed AppX packages that look like a Windows App Runtime, and whether
	/// the package list could be read at all. Never throws.</summary>
	static IEnumerable<string> EnumerateAppRuntimePackageNames(out bool enumerated)
	{
		enumerated = false;
		var names = new List<string>();
		try
		{
			// WinRT: Windows.Management.Deployment.PackageManager. Reached by reflection so this
			// file keeps compiling where the Windows SDK projection is not referenced.
			var packageManagerType = Type.GetType("Windows.Management.Deployment.PackageManager, Windows, ContentType=WindowsRuntime");
			if (packageManagerType == null)
				return names;
			var findPackages = packageManagerType.GetMethod("FindPackages");
			if (findPackages == null || findPackages.GetParameters().Length != 0)
				return names;
			foreach (var ctor in packageManagerType.GetConstructors())
			{
				var parameters = ctor.GetParameters();
				if (parameters.Length != 0)
					continue;
				object? manager;
				try
				{
					manager = ctor.Invoke(null);
				}
				catch (System.Reflection.TargetInvocationException)
				{
					continue;
				}
				if (manager == null)
					continue;
				if (findPackages.Invoke(manager, null) is not IList<object> packages)
					continue;
				enumerated = true;
				foreach (var package in packages)
				{
					// Over reflection the projected PackageId surfaces as a string already.
					var id = package?.GetType().GetProperty("Id")?.GetValue(package);
					var name = id as string ?? id?.GetType().GetProperty("Name")?.GetValue(id) as string;
					if (name != null && name.IndexOf("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase) >= 0)
						names.Add(name);
				}
				break;
			}
		}
		catch (Exception)
		{
			// Any WinRT/permission failure leaves enumerated false: "cannot tell".
		}
		return names;
	}

	/// <summary>Looks for the runtime under the classic uninstall keys, which the EXE bootstrapper
	/// layout writes. Returns false when nothing is found; never throws.</summary>
	static bool TryFindRuntimeInUninstallRegistry(out int majorVersion)
	{
		majorVersion = 0;
		var found = false;
		foreach (var hive in new[] {
			Microsoft.Win32.RegistryHive.LocalMachine,
			Microsoft.Win32.RegistryHive.CurrentUser })
		{
			foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
			{
				try
				{
					using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
					using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
					if (uninstall == null)
						continue;
					foreach (var name in uninstall.GetSubKeyNames())
					{
						using var entry = uninstall.OpenSubKey(name);
						var displayName = entry?.GetValue("DisplayName") as string;
						if (displayName == null || displayName.IndexOf("Windows App Runtime", StringComparison.OrdinalIgnoreCase) < 0)
							continue;
						found = true;
						if (entry.GetValue("DisplayVersion") is string version
							&& int.TryParse(version, out var parsedMajor)
							&& parsedMajor > majorVersion)
						{
							majorVersion = parsedMajor;
						}
					}
				}
				catch (Exception)
				{
					// Restricted or unavailable hive/view: nothing learned here.
				}
			}
		}
		return found;
	}

	static bool CanHostRunOnAppArchitecture(string? appBin, out string appArchitecture, out string? dotnetHostPath, out Architecture? dotnetHostArchitecture)
	{
		appArchitecture = "an unknown architecture";
		dotnetHostPath = null;
		dotnetHostArchitecture = null;
		if (string.IsNullOrEmpty(appBin))
			return true;

		// hostpolicy.dll is the file the .NET host fails on first; coreclr.dll is checked too so a
		// trimmed/odd layout still gets diagnosed instead of silently passing.
		foreach (var nativeHostLibrary in new[] { "hostpolicy.dll", "coreclr.dll" })
		{
			var path = Path.Combine(appBin, nativeHostLibrary);
			if (!File.Exists(path))
				continue;
			var architecture = DotNetSdkService.DetectHostArchitecture(path);
			if (architecture == null)
				continue;
			if (architecture == RuntimeInformation.ProcessArchitecture)
				return true;
			dotnetHostPath = DotNetSdkService.ResolveDotnetHostForArchitecture(architecture.Value);
			if (dotnetHostPath != null)
			{
				dotnetHostArchitecture = architecture;
				return true;
			}
			appArchitecture = architecture.ToString()!;
			return false;
		}

		// No native runtime in the output: framework-dependent, nothing architecture-specific to adopt.
		return true;
	}

	/// <summary>
	/// Whether this host can actually run on the framework the app's runtimeconfig pins.
	///
	/// A host built for a different CLR major cannot run on an app's graph: a self-contained app
	/// (<c>includedFrameworks</c>) carries that CLR beside its executable, while a
	/// framework-dependent app names it through <c>framework</c>/<c>frameworks</c>.  In contrast,
	/// a self-contained graph whose major matches the selected child is not only valid but required:
	/// it supplies the app's Windows App SDK native DLLs and PRI resources.  Dropping that graph
	/// makes WinUI bind to the IDE host's Windows App SDK instead, which can stow a native XAML
	/// parse exception while materializing a third-party control template.
	/// An unreadable or framework-less runtimeconfig is treated as usable, preserving the
	/// previous behaviour.
	/// </summary>
	static bool CanHostRunOnAppFramework(string runtimeConfigPath, out string appVersion)
	{
		appVersion = "unknown";
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
			if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
				return true;
			// A runtimeconfig names its frameworks as a single "framework", a "frameworks" array (an
			// app on both Microsoft.NETCore.App and Microsoft.WindowsDesktop.App), or
			// "includedFrameworks" when the app is self-contained. The lowest version any of them
			// names is what the host would have to run on.
			var versions = new List<string>();
			var selfContained = false;
			if (options.TryGetProperty("framework", out var single) && single.TryGetProperty("version", out var singleVersion))
				versions.Add(singleVersion.GetString());
			foreach (var property in new[] { "frameworks", "includedFrameworks" })
			{
				if (!options.TryGetProperty(property, out var many) || many.ValueKind != System.Text.Json.JsonValueKind.Array)
					continue;
				selfContained |= property == "includedFrameworks";
				foreach (var framework in many.EnumerateArray())
					if (framework.TryGetProperty("version", out var manyVersion))
						versions.Add(manyVersion.GetString());
			}
			var named = versions.Where(v => !string.IsNullOrEmpty(v)).ToList();
			if (named.Count > 0)
				appVersion = string.Join(", ", named.Distinct());
			else if (options.TryGetProperty("tfm", out var tfm))
				appVersion = tfm.GetString() ?? appVersion;
			// The Microsoft WinUI factory selected a child from this same runtimeconfig before this
			// method runs.  Do not compare a self-contained app to the IDE process (which is net10):
			// it is the selected child, not this WPF parent, that dotnet exec will load.  Its app-local
			// Windows App SDK graph is mandatory for compiled XAML resources.
			if (selfContained)
				return true;
			var majors = named
				.Select(v => Version.TryParse(v, out var parsed) ? parsed.Major : -1)
				.Where(major => major > 0)
				.ToList();
			return majors.Count == 0 || majors.Min() >= Environment.Version.Major;
		}
		catch (Exception)
		{
			// Unreadable runtimeconfig: let the launch decide rather than silently dropping the graph.
			return true;
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

	public int ResolvedNameCount => canvas.IndexedNameCount;
	public string LastPickDiagnostic => canvas.LastPickDiagnostic;

	public void SetSelectableNames(IReadOnlyList<string> names) => canvas.SetSelectableNames(names);

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
			// IsProcessAlive alone races the child's own process.Exited event: StreamJsonRpc can
			// report the pipe severed (ConnectionLostException, or the ObjectDisposedException that
			// DesignerHostProcessClient normalizes to IOException) slightly before HasExited flips,
			// so a message-based check catches the same "the child just died" case reliably too.
			var childGone = client is { IsProcessAlive: false }
				|| e is StreamJsonRpc.ConnectionLostException
				|| e.GetBaseException().Message.Contains("connection with the remote party was lost", StringComparison.OrdinalIgnoreCase)
				|| e.GetBaseException().Message.Contains("no longer running", StringComparison.OrdinalIgnoreCase);
			var message = childGone
				// A crashed child (a native crash in a framework control, e.g. NavigationView/Pivot -
				// see doc/technotes/winui-designer.md) is not this document's fault. Recovery is
				// already under way (UnoDesignClient.OnConnectionExited); if it succeeds,
				// OnClientRecovered re-renders with lastLoadedText automatically, so this status is
				// transient. Say so instead of surfacing the raw disposal/IO exception text, which
				// names the wrong failure (a JsonRpc/pipe detail, not "the page you're viewing").
				? $"{hostDisplayName} process exited while rendering (likely a native crash in the" +
					" page's content); attempting to restart the host automatically…"
				: $"{hostDisplayName} render failed: " + e.GetBaseException().Message;
			SetStatus(message);
			DesignerOutput.AppendLine(DesignerOutput.Channel(UnoDesignClient.OutputChannelName), message);
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
		canvas.ApplySnapshot(snapshot);
		if (snapshot.Render != null)
			HasRenderedPreview = true;
		StatusText = snapshot.Diagnostics.Count == 0
			? $"Rendered by {hostDisplayName} ({FormatSize(snapshot.Render)})."
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

	public string ResolveNameAt(Vector2 point) => canvas.ResolveNameAt(point);

	/// <summary>The primary (single) selection's element name.</summary>
	public string SelectedElementName => canvas.SelectedElementName;

	public event EventHandler<IReadOnlyList<string>> SelectionChanged;

	public IReadOnlyList<string> SelectedNames => canvas.SelectedNames;

	public void SelectElements(IReadOnlyList<string> names) => canvas.SelectElements(names);

	public event EventHandler<IReadOnlyList<(string Name, double DX, double DY)>> ElementGroupDragCommitted;

	public event EventHandler<ElementDragInfo> ElementDragCommitted;

	public (double X, double Y, double Width, double Height)? QueryElementBounds(string name) => canvas.QueryElementBounds(name);

	public void ShowSelection(string name) => canvas.ShowSelection(name);

	public void ShowSelectionAtPath(string path, string label) => canvas.ShowSelectionAtPath(path, label);

	public void SelectElement(string name) => canvas.SelectElement(name);

	public void ClearSelection() => canvas.ClearSelection();

	#region IWinUIXamlDesignView

	public (double Zoom, double PanX, double PanY) GetViewport() => canvas.GetViewport();

	public double GetViewportScale() => canvas.GetViewportScale();

	public void SetViewport(double zoom, double panX, double panY) => canvas.SetViewport(zoom, panX, panY);

	public void FitView() => canvas.FitView();

	public (double X, double Y) DesignToSurfacePoint(double x, double y) => canvas.DesignToSurfacePoint(x, y);

	public (double X, double Y) DesignToScreenPoint(double x, double y) => canvas.DesignToScreenPoint(x, y);

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

	public string DescribeElementState(string name) => canvas.DescribeElementState(name);

	public string FrameProfile()
	{
		var render = lastSnapshot?.Render;
		return render == null ? "no frame" : $"render {render.Width}x{render.Height} png={render.Data.Length / 1024}KB";
	}

	public string CompositorMetricsDump() => "not applicable (out-of-process Uno host)";
	public string RenderProbeAndProfile() => "not applicable (out-of-process Uno host)";
	public string DumpDrawCalls() => "not applicable (out-of-process Uno host)";
	public string WinUICommandProbe() => "not applicable (out-of-process Uno host)";

	public string DiagnoseScreenAnchors() => canvas.DiagnoseScreenAnchors();
	public string ImagePathProbe() => "not applicable (out-of-process Uno host)";
	public void SetShowDiagnosticOverlay(bool value) { }
	public void SetRecreateBitmapEachFrame(bool value) { }
	public void SetPresentViaBackgroundBrush(bool value) { }

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		scaleTimer?.Stop();
		canvas.Dispose();
		if (client != null) { client.Recovered -= OnClientRecovered; client.RecoveryFailed -= OnClientRecoveryFailed; }
		client?.Dispose();
		client = null;
	}
}
