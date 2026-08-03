// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Bridges real ILSpy panes (AssemblyTreeModel/AssemblyListPane, SearchPaneModel/SearchPane,
// AnalyzerTreeViewModel/AnalyzerTreeView) into OpenDevelop's own pads (DockWorkspace.ToolPanes),
// and renders decompiled output through a real ILSpy DecompilerTextView hosted as a plain
// OpenDevelop document tab (DecompiledCodeViewContent) - i.e. the decompile result opens like a
// read-only, virtual file, exactly as the legacy ILSpy integration presented it, instead of
// standing up ILSpy's own separate DockWorkspace/DockingManager. ILSpy's own document/tab system
// (Docking.DockWorkspace, TabPageModel) is intentionally NOT used here -
// AssemblyTreeModel.DecompileSelectedNodes() calls into it, so instead of reusing that call,
// selection changes are observed directly via MessageBus<AssemblyTreeSelectionChangedEventArgs>
// and decompiled straight into one dedicated DecompilerTextView hosted as an OpenDevelop
// document, mirroring what TabPageModelExtensions.CreateDecompilationOptions()/
// DecompilerTextView.DecompileAsync() do internally.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Analyzers;
using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Search;
using ICSharpCode.ILSpy.TextView;
using ICSharpCode.ILSpy.Themes;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.ILSpyX.TreeView;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

using TomsToolbox.Wpf.Composition;

namespace ICSharpCode.ILSpyAddIn
{
	/// <summary>
	/// Lazily creates the real ILSpy panes on first use and keeps them registered as OpenDevelop
	/// pads for the lifetime of the process.
	/// </summary>
	public static class IlSpyWorkspaceHost
	{
		private static AssemblyTreeModel assemblyTreeModel;
		private static DecompilerTextView decompilerTextView;
		// AssemblyTreeModel derives directly from OpenDevelop's ToolPaneModel now too (see
		// doc/technotes/ilspy.md "Immediate next actions" #3) - no adapter needed for any of the
		// three ILSpy panes anymore.
		private static AssemblyTreeModel assembliesPane;
		private static SearchPaneModel searchPane;
		// AnalyzerTreeViewModel derives directly from OpenDevelop's ToolPaneModel now too (see
		// doc/technotes/ilspy.md "Immediate next actions" #3) - no adapter needed for this pane.
		private static AnalyzerTreeViewModel analyzerPane;
		private static DecompiledCodeViewContent decompiledCodeView;
		private static bool initialized;

		/// <summary>
		/// Whether <see cref="EnsureInitialized"/> has already run, WITHOUT triggering it as a side
		/// effect (unlike every other member here, including <see cref="Panes"/>) - lets a test
		/// distinguish "the ILSpy layout's activation hook already initialized this" from "my own
		/// diagnostic call just initialized it," which every other status-reading DevFlow action
		/// can't do since they all call <see cref="EnsureInitialized"/> themselves.
		/// </summary>
		public static bool IsInitialized => initialized;

		public static AssemblyTreeModel AssemblyTreeModel {
			get {
				EnsureInitialized();
				return assemblyTreeModel;
			}
		}

		public static DecompilerTextView DecompilerTextView {
			get {
				EnsureInitialized();
				return decompilerTextView;
			}
		}

		public static SearchPaneModel SearchPane {
			get {
				EnsureInitialized();
				return searchPane;
			}
		}

		public static AnalyzerTreeViewModel AnalyzerPane {
			get {
				EnsureInitialized();
				return analyzerPane;
			}
		}

		/// <summary>
		/// ILSpy's own settings service - source of <c>AssemblyListManager.AssemblyLists</c> and
		/// <c>SessionSettings.ActiveAssemblyList</c> for the toolbar's assembly-list dropdown.
		/// </summary>
		public static SettingsService SettingsService {
			get {
				EnsureInitialized();
				return App.ExportProvider.GetExportedValue<SettingsService>();
			}
		}

		/// <summary>
		/// ILSpy's own language service - source of <c>AllLanguages</c>/<c>Language</c>/
		/// <c>LanguageVersion</c> for the toolbar's language and language-version dropdowns.
		/// </summary>
		public static LanguageService LanguageService {
			get {
				EnsureInitialized();
				return App.ExportProvider.GetExportedValue<LanguageService>();
			}
		}

		/// <summary>
		/// The hosted ILSpy's API-visibility level (which types/members the assembly tree shows) -
		/// real ILSpy's three toolbar CheckBoxes are a radio group over this one enum. Changing it
		/// needs no explicit refresh: AssemblyTreeModel subscribes to LanguageSettings'
		/// PropertyChanged and calls Refresh() for any property other than the language ones.
		/// </summary>
		public static ICSharpCode.ILSpyX.ApiVisibility GetApiVisibility()
		{
			EnsureInitialized();
			return App.ExportProvider.GetExportedValue<SettingsService>()
				.SessionSettings.LanguageSettings.ShowApiLevel;
		}

		public static void SetApiVisibility(ICSharpCode.ILSpyX.ApiVisibility level)
		{
			EnsureInitialized();
			App.ExportProvider.GetExportedValue<SettingsService>()
				.SessionSettings.LanguageSettings.ShowApiLevel = level;
		}

		/// <summary>
		/// Activates one of this addin's tool panes by title WITHOUT re-registering its anchorable.
		/// Deliberately not the remove-then-re-add that od.ilspy.show-pane does: that was needed back
		/// when runtime-added panes didn't reliably dock, but is destructive now that the ILSpy layout
		/// template actually restores - measured, after one such re-registration, activating a
		/// *different* pane fails to materialize it at all, and repeated churn leaves none of the
		/// three rendered (see doc/technotes/ilspy.md).
		/// </summary>
		public static void ActivatePane(string title)
		{
			var pane = Panes.FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));
			if (pane == null)
				return;
			pane.Show();
			pane.IsActive = true;
		}

		/// <summary>
		/// Re-activates the decompiled-code document tab. Activating any tool pane (e.g. via
		/// od.ilspy.show-pane) makes that pane the dock's ActiveContent, which leaves the workbench
		/// with no active *document* at all - so a test that inspects a pad needs a way to put things
		/// back for whatever runs next against the same shared app instance.
		/// </summary>
		public static void ActivateDecompiledDocument()
		{
			EnsureInitialized();
			decompiledCodeView?.WorkbenchWindow?.SelectWindow();
		}

		/// <summary>
		/// The four pads this addin registers, for status/diagnostics (e.g. the
		/// od.ilspy.status DevFlow action). Exposed after <see cref="EnsureInitialized"/>.
		/// </summary>
		public static IEnumerable<ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel> Panes {
			get {
				EnsureInitialized();
				return new ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel[] { assembliesPane, searchPane, analyzerPane };
			}
		}

		// Maps OpenDevelop's IdeThemeService theme names to one of ThemeManager.AllThemes
		// ("Light", "Dark", "VS Code Light+", "VS Code Dark+", "R# Light", "R# Dark"). OpenDevelop
		// doesn't have a VS-Code/R# equivalent concept, so only Light/Dark carry across; "Blue"
		// (OpenDevelop's third built-in dock theme) has no ILSpy analog and falls back to Light -
		// same "Light/Dark only, initially" scope as the rest of the theming work this pass.
		static string ToIlSpyTheme(string ideTheme)
		{
			return ideTheme == ICSharpCode.SharpDevelop.Workbench.IdeThemeService.Dark ? "Dark" : "Light";
		}

		public static void EnsureInitialized()
		{
			if (initialized)
				return;
			initialized = true;

			var exportProvider = App.Initialize();

			// TomsToolbox.Wpf.Composition.ExportProviderLocator.GetExportProvider(DependencyObject)
			// walks up the visual tree looking for an ancestor with the ExportProvider attached
			// property set, falling back to whatever was registered via Register() (real
			// App.xaml.cs calls this too). Our panes' visual tree ancestor is OpenDevelop's own
			// workbench window, which never sets that attached property - register the fallback
			// so lookups like ContextMenuProvider's (used by DecompilerTextView's constructor)
			// succeed regardless of where in the tree they end up.
			ExportProviderLocator.Register(exportProvider);

			// ILSpy's real views (AssemblyListPane, SearchPane, AnalyzerTreeView, ...) are never
			// constructed directly - they're registered via [DataTemplate(typeof(TheViewModel))]
			// and resolved by WPF's implicit DataTemplate lookup when a ContentPresenter's
			// content is the view-model instance itself. Real ILSpy wires this up once in
			// App.xaml.cs via `Resources.MergedDictionaries.Add(DataTemplateManager.
			// CreateDynamicDataTemplates(ExportProvider))`; since we don't link App.xaml.cs,
			// merge the same dynamic templates into OpenDevelop's own Application.Resources so
			// the adapters below (which just set Content = the raw view-model) resolve the same
			// way real ILSpy does.
			Application.Current.Resources.MergedDictionaries.Add(DataTemplateManager.CreateDynamicDataTemplates(exportProvider));

			// Theme resources (ICSharpCode.ILSpy.Themes.ResourceKeys.* - brushes/pens that
			// DecompilerTextView's BracketHighlightRenderer looks up via FindResource) are never
			// merged anywhere since we don't link App.xaml/ThemeManager's own theme-switching.
			// Tried loading a XAML resource dictionary (Themes/HostedTheme.Light.xaml) for this,
			// but its "urn:TomsToolbox.Wpf.Styles" xmlns forces WPF to eagerly load *every*
			// assembly registered under that XML namespace via [XmlnsDefinition] across the whole
			// process - including ICSharpCode.WpfDesign.Designer (also registered there), which
			// isn't present in this addin's own folder and isn't needed here at all. Register just
			// the two ResourceKeys BracketHighlightRenderer actually reads directly in code
			// instead, sidestepping XAML/xmlns resolution entirely.
			Application.Current.Resources[ResourceKeys.BracketHighlightBackgroundBrush] =
				new SolidColorBrush(Color.FromArgb(0x16, 0x00, 0x00, 0xFF));
			Application.Current.Resources[ResourceKeys.BracketHighlightBorderPen] =
				new Pen(new SolidColorBrush(Color.FromArgb(0x34, 0x00, 0x00, 0xFF)), 1);

			// What real ILSpy's App ctor does after InitializeComponent/DI (App.xaml.cs):
			// tree node icons + the ILSpy theme (which also pulls in Themes/generic.xaml's default
			// control styles through ThemeManager's "/themes/Theme.*.xaml" load, whose resources
			// are now linked at the assembly root - see ILSpyAddIn.csproj). Without the images
			// provider, SharpTreeView nodes render no icons; without the theme, the pane controls
			// fall back to unstyled rendering.
			SharpTreeNode.SetImagesProvider(new WpfWindowsTreeNodeImagesProvider());

			// ThemeManager.UpdateTheme's relative-pack-URI resolution bug (see
			// Themes/ThemeManager.cs) is fixed at its source now - see the assembly-qualified
			// pack URI there instead of trying to force Application.ResourceAssembly here (WPF
			// throws InvalidOperationException on any attempt to set it after the framework has
			// already set it once, which happens before this addin ever loads - confirmed at
			// runtime, not just theoretical).
			// Theme bridge (doc/technotes/ilspy.md "Full application theming" / "Immediate next
			// actions" #5 follow-up, 2026-08-02): ILSpy's own ThemeManager drives real, functional
			// behavior - DecompilerTextView.cs applies ThemeManager.Current's syntax colors on every
			// decompile, and ThemeAwareHighlightingColorizer reads ThemeManager.Current.IsDarkTheme
			// to pick a fallback text color - so leaving it seeded once from ILSpy's own
			// (unrelated, independently persisted) SessionSettings.Theme and never touched again
			// would leave decompiled code in the wrong colors whenever the user switches
			// OpenDevelop's own IDE theme (IdeThemeService). Seed from OpenDevelop's current theme
			// instead, and keep them in sync via IdeThemeService.ThemeChanged for the rest of the
			// process, rather than running two independent, unsynchronized theme authorities.
			ThemeManager.Current.Theme = ToIlSpyTheme(ICSharpCode.SharpDevelop.Workbench.IdeThemeService.CurrentTheme);
			ICSharpCode.SharpDevelop.Workbench.IdeThemeService.ThemeChanged += (_, theme) => ThemeManager.Current.Theme = ToIlSpyTheme(theme);

			// ILSpy's control style dictionaries (SearchBox, ZoomScrollViewer, SortableGridViewColumn,
			// ...) are compiled into this assembly's theme dictionary (themes/generic.xaml), but the
			// per-assembly theme lookup does not resolve them in this host (LibreWPF): controls end
			// up style-less (the SearchBox input box rendered as a blank gap, and ZoomScrollViewer
			// silently fell back to its base ScrollViewer template). Loading the dictionary into
			// Application.Resources makes the {x:Type ...} implicit styles reachable through the
			// ordinary resource lookup instead.
			Application.Current.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary {
				Source = new Uri("pack://application:,,,/ILSpyAddIn;component/themes/generic.xaml", UriKind.Absolute)
			});

			assemblyTreeModel = exportProvider.GetExportedValue<AssemblyTreeModel>();
			var searchPaneModel = exportProvider.GetExportedValue<SearchPaneModel>();
			var analyzerTreeViewModel = exportProvider.GetExportedValue<AnalyzerTreeViewModel>();

			// AssemblyTreeModel.TreeView_SelectionChanged (fired the moment OpenFiles() below
			// selects a node) unconditionally reads ILSpy's own Docking.DockWorkspace.
			// ActiveTabPage.GetState() - even though we don't use ILSpy's tab/document hosting at
			// all (see file header), that dependency isn't optional/skippable. Give it one real,
			// otherwise-unused TabPageModel so that read doesn't NRE; its content is never shown
			// anywhere (we render decompiled output through our own DecompiledCodeViewContent
			// document tab instead).
			var ilSpyDockWorkspace = exportProvider.GetExportedValue<ICSharpCode.ILSpy.Docking.DockWorkspace>();
			ilSpyDockWorkspace.ActiveTabPage = ilSpyDockWorkspace.AddTabPage();

			// Real ILSpy calls AssemblyTreeModel.Initialize() from a MessageBus<
			// MainWindowLoadedEventArgs> subscription wired up in its constructor, fired by the
			// excluded MainWindow's Loaded event. Without it, AssemblyTreeModel's internal
			// assemblyListTreeNode/AssemblyList never get set up, so OpenFiles() below silently
			// finds no tree node to select and nothing ever decompiles. Call it directly instead
			// of faking a MainWindowLoadedEventArgs message.
			assemblyTreeModel.Initialize();

			decompilerTextView = new DecompilerTextView(exportProvider);

			// SearchPaneModel/AnalyzerTreeViewModel set their own real ILSpy titles ("Search",
			// "Analyze") in their constructors, so no override is needed for them. AssemblyTreeModel
			// sets Title = Resources.Assemblies (an ILSpy-localized string, not necessarily
			// "Assemblies") - override it explicitly, matching what the (now-removed)
			// IlSpyToolPaneAdapter used to do for this pane.
			assemblyTreeModel.Title = "Assemblies";
			assembliesPane = assemblyTreeModel;
			searchPane = searchPaneModel;
			analyzerPane = analyzerTreeViewModel;

			DockWorkspaceExtensibility.AddToolPane(assembliesPane);
			DockWorkspaceExtensibility.AddToolPane(searchPane);
			DockWorkspaceExtensibility.AddToolPane(analyzerPane);

			// "Switching to the ILSpy layout": make the hosted pads visible/active as a group
			// rather than leaving them registered-but-hidden.
			assembliesPane.Show();
			searchPane.Show();
			analyzerPane.Show();

			// Decompiled output opens as a document tab (a read-only, virtual file), not a pad.
			decompiledCodeView = new DecompiledCodeViewContent(decompilerTextView);
			SD.Workbench.ShowView(decompiledCodeView);

			MessageBus<AssemblyTreeSelectionChangedEventArgs>.Subscribers += (sender, e) => lastDecompile = OnSelectionChangedAsync();

			// Language / language-version changes need their own subscription, for the same reason
			// the selection one above exists: upstream handles them in AssemblyTreeModel's settings
			// handler by calling RefreshDecompiledView(), which decompiles into
			// DockWorkspace.ActiveTabPage's text view - ILSpy's own tab system, which this host
			// deliberately does not render (see the file header; it only gets one dummy TabPageModel
			// so upstream reads of ActiveTabPage don't NRE). So without this, picking IL in the
			// toolbar's language dropdown left the visible document still showing the previous C#
			// output while the dropdown said IL - user-reported, then reproduced by
			// IlSpyAddInTests' multi-pad workflow coverage.
			var languageService = exportProvider.GetExportedValue<LanguageService>();
			languageService.PropertyChanged += (_, e) => {
				if (e.PropertyName is nameof(LanguageService.Language) or nameof(LanguageService.LanguageVersion))
					lastDecompile = RefreshDecompiledViewAsync();
			};
		}

		// Phase 1 of "decompiled code as a normal OpenDevelop document" (doc/technotes/ilspy.md
		// "Unify C# document hosting"): a single selected TypeTreeNode now opens/reuses a plain
		// OpenDevelop document (DecompiledViewContent, backed by an ilspy:// FileName and
		// OpenDevelop's own CodeEditor) via the same NavigateToDecompiledEntityService.NavigateTo
		// path "go to definition" already uses - instead of writing into the shared bespoke
		// DecompilerTextView/decompiledCodeView pane. This was previously reverted-but-kept because
		// ILSpyDecompilerService.DecompileType couldn't resolve external references for anything
		// decompiled this way (ResolutionException: "Failed to resolve assembly: System.Runtime");
		// that's now fixed (ILSpyDecompilerService.CreateDecompiler reuses the already-loaded
		// LoadedAssembly's resolver), so this is safe to wire up. Everything else (assembly/module
		// nodes, namespace nodes, member nodes, multi-selection) is intentionally left on the old
		// bespoke-pane path for now - see the technote for why (whole-module native support is a
		// trivial follow-up via DecompiledTypeReference.IsWholeModule; multi-select and reference
		// hyperlink navigation are the genuinely hard remaining pieces).
		private static Task OnSelectionChangedAsync()
		{
			var nodes = assemblyTreeModel.SelectedNodes.ToArray();
			if (nodes.Length == 1 && nodes[0] is ICSharpCode.ILSpy.TreeNodes.TypeTreeNode typeNode) {
				var topLevelName = typeNode.TypeDefinition.FullTypeName.TopLevelTypeName.ReflectionName;
				var assemblyFile = FileName.Create(typeNode.ParentAssemblyNode.LoadedAssembly.FileName);
				return NavigateToDecompiledEntityService.NavigateTo(assemblyFile, topLevelName, memberKey: null);
			}
			// Step 3 (doc/technotes/ilspy.md "Unify C# document hosting") was attempted and
			// REVERTED: routing single-AssemblyTreeNode (whole-module) selection here breaks
			// tests/OpenDevelop.IntegrationTests/IlSpyAddInTests.cs's
			// OpenAssembly_ShowsIlSpyPadsWithRealContent, which explicitly asserts the active view
			// after opening an assembly is ICSharpCode.ILSpyAddIn.DecompiledCodeViewContent (the
			// bespoke pane) and that a "Decompiled Code" tab renders in the UI tree - both would
			// fail against the native "[Module]" document instead. NavigateToModule/
			// DecompiledViewContent's whole-module support (and the DecompilationTask plumbing) is
			// still real and still used by od.ilspy.navigate-to-type-style direct exercising - just
			// not reachable through tree selection, on purpose, until that test (or the behavior it
			// pins) is deliberately updated. See the technote for the full story - this was caught
			// only by actually reading the existing test file, not by the earlier "verified live"
			// checks, which is itself the lesson: check existing test assertions before wiring up
			// a routing change, not just build+run-once.
			return RefreshDecompiledViewAsync();
		}

		// Tracks the in-flight decompile kicked off by the AssemblyTreeSelectionChangedEventArgs
		// subscriber above (fired synchronously from within OpenFiles() below, as soon as it
		// selects the newly opened assembly's node), so OpenAssemblyAsync can await the *same* task
		// instead of starting a second, redundant DecompileAsync call - ILSpy's decompiler
		// cancels an in-progress decompilation when a new one starts, so racing two calls here
		// just cancels one of them (surfaced as an unhandled TaskCanceledException).
		private static Task lastDecompile = Task.CompletedTask;

		public static async Task OpenAssemblyAsync(string fileName)
		{
			EnsureInitialized();

			// Opening an assembly means entering the ILSpy workbench layout: the layout's
			// onActivating hook (IlSpyLayoutTemplateProvider) already ran EnsureInitialized, and
			// CurrentLayoutName's setter restores ILSpy.xml (docking the three panes) after it -
			// only switch when the user isn't already in the ILSpy layout.
			if (LayoutConfiguration.CurrentLayoutName != "ILSpy")
				LayoutConfiguration.CurrentLayoutName = "ILSpy";

			assemblyTreeModel.OpenFiles(new[] { fileName });

			// DecompilerTextView.DecompileAsync's own doc comment: "If the operation is
			// cancelled (by starting another decompilation action), the returned task is marked
			// as cancelled." OpenFiles can trigger more than one selection-changed event (e.g. an
			// initial UnselectAll() before the real node gets selected), each superseding
			// `lastDecompile` - a cancelled task just means a newer one took over, not failure, so
			// swallow it and wait briefly for the text view to actually settle instead of
			// asserting on whichever specific Task instance happened to finish.
			try
			{
				await lastDecompile;
			}
			catch (TaskCanceledException)
			{
				for (int i = 0; i < 20 && string.IsNullOrEmpty(decompilerTextView.textEditor.Text); i++)
					await Task.Delay(50);
			}
		}

		private static Task RefreshDecompiledViewAsync()
		{
			var exportProvider = App.ExportProvider;
			var languageService = exportProvider.GetExportedValue<LanguageService>();
			var settingsService = exportProvider.GetExportedValue<SettingsService>();
			var options = new DecompilationOptions(languageService.LanguageVersion, settingsService.DecompilerSettings, settingsService.DisplaySettings);

			var nodes = assemblyTreeModel.SelectedNodes.ToArray();
			if (nodes.Length == 0)
				return Task.CompletedTask;

			return decompilerTextView.DecompileAsync(languageService.Language, nodes, null, options);
		}
	}
}
