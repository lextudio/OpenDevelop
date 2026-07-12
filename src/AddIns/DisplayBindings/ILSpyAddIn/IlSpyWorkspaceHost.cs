// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
//
// Bridges real ILSpy panes (AssemblyTreeModel/AssemblyListPane, SearchPaneModel/SearchPane,
// AnalyzerTreeViewModel/AnalyzerTreeView) and a real ILSpy DecompilerTextView into OpenDevelop's
// own pads (DockWorkspace.ToolPanes), instead of standing up ILSpy's own separate
// DockWorkspace/DockingManager. ILSpy's own document/tab system (Docking.DockWorkspace,
// TabPageModel) is intentionally NOT used here - AssemblyTreeModel.DecompileSelectedNodes() calls
// into it, so instead of reusing that call, selection changes are observed directly via
// MessageBus<AssemblyTreeSelectionChangedEventArgs> and decompiled straight into one dedicated
// DecompilerTextView hosted as an OpenDevelop pad, mirroring what TabPageModelExtensions.
// CreateDecompilationOptions()/DecompilerTextView.DecompileAsync() do internally.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Analyzers;
using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Search;
using ICSharpCode.ILSpy.TextView;
using ICSharpCode.ILSpy.Themes;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.ILSpy.ViewModels;
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
		private static IlSpyToolPaneAdapter assembliesPane;
		private static IlSpyToolPaneAdapter searchPane;
		private static IlSpyToolPaneAdapter analyzerPane;
		private static DecompiledCodeToolPaneModel decompiledCodePane;
		private static bool initialized;

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

		/// <summary>
		/// The four pads this addin registers, for status/diagnostics (e.g. the
		/// od.ilspy.status DevFlow action). Exposed after <see cref="EnsureInitialized"/>.
		/// </summary>
		public static IEnumerable<ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel> Panes {
			get {
				EnsureInitialized();
				return new ICSharpCode.SharpDevelop.ViewModels.ToolPaneModel[] { assembliesPane, searchPane, analyzerPane, decompiledCodePane };
			}
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

			assemblyTreeModel = exportProvider.GetExportedValue<AssemblyTreeModel>();
			var searchPaneModel = exportProvider.GetExportedValue<SearchPaneModel>();
			var analyzerTreeViewModel = exportProvider.GetExportedValue<AnalyzerTreeViewModel>();

			// AssemblyTreeModel.TreeView_SelectionChanged (fired the moment OpenFiles() below
			// selects a node) unconditionally reads ILSpy's own Docking.DockWorkspace.
			// ActiveTabPage.GetState() - even though we don't use ILSpy's tab/document hosting at
			// all (see file header), that dependency isn't optional/skippable. Give it one real,
			// otherwise-unused TabPageModel so that read doesn't NRE; its content is never shown
			// anywhere (we render decompiled output through our own DecompiledCodeToolPaneModel
			// pad instead).
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
			// "Analyze") in their constructors - IlSpyToolPaneAdapter mirrors those live (see its
			// PropertyChanged sync), so any override here just gets clobbered moments later
			// anyway. AssemblyTreeModel never sets its own pane Title at all (real ILSpy names
			// this pane via a static XAML anchorable header, not a Title binding), so it needs an
			// explicit one.
			assembliesPane = new IlSpyToolPaneAdapter(assemblyTreeModel, assemblyTreeModel) { Title = "Assemblies" };
			searchPane = new IlSpyToolPaneAdapter(searchPaneModel, searchPaneModel);
			analyzerPane = new IlSpyToolPaneAdapter(analyzerTreeViewModel, analyzerTreeViewModel);
			decompiledCodePane = new DecompiledCodeToolPaneModel(decompilerTextView);

			DockWorkspaceExtensibility.AddToolPane(assembliesPane);
			DockWorkspaceExtensibility.AddToolPane(searchPane);
			DockWorkspaceExtensibility.AddToolPane(analyzerPane);
			DockWorkspaceExtensibility.AddToolPane(decompiledCodePane);

			// "Switching to the ILSpy layout": make the hosted pads visible/active as a group
			// rather than leaving them registered-but-hidden.
			assembliesPane.Show();
			searchPane.Show();
			analyzerPane.Show();
			decompiledCodePane.Show();

			MessageBus<AssemblyTreeSelectionChangedEventArgs>.Subscribers += (sender, e) => lastDecompile = RefreshDecompiledView();
		}

		// Tracks the in-flight decompile kicked off by the AssemblyTreeSelectionChangedEventArgs
		// subscriber above (fired synchronously from within OpenFiles() below, as soon as it
		// selects the newly opened assembly's node), so OpenAssembly can await the *same* task
		// instead of starting a second, redundant DecompileAsync call - ILSpy's decompiler
		// cancels an in-progress decompilation when a new one starts, so racing two calls here
		// just cancels one of them (surfaced as an unhandled TaskCanceledException).
		private static Task lastDecompile = Task.CompletedTask;

		public static async Task OpenAssembly(string fileName)
		{
			EnsureInitialized();
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

		private static Task RefreshDecompiledView()
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
