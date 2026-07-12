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
using System.Windows;

using ICSharpCode.ILSpy;
using ICSharpCode.ILSpy.Analyzers;
using ICSharpCode.ILSpy.AssemblyTree;
using ICSharpCode.ILSpy.Search;
using ICSharpCode.ILSpy.TextView;
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

			assemblyTreeModel = exportProvider.GetExportedValue<AssemblyTreeModel>();
			var searchPaneModel = exportProvider.GetExportedValue<SearchPaneModel>();
			var analyzerTreeViewModel = exportProvider.GetExportedValue<AnalyzerTreeViewModel>();

			decompilerTextView = new DecompilerTextView(exportProvider);

			assembliesPane = new IlSpyToolPaneAdapter(assemblyTreeModel, assemblyTreeModel) { Title = "Assemblies" };
			searchPane = new IlSpyToolPaneAdapter(searchPaneModel, searchPaneModel) { Title = "Search" };
			analyzerPane = new IlSpyToolPaneAdapter(analyzerTreeViewModel, analyzerTreeViewModel) { Title = "Analyzer" };
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

			MessageBus<AssemblyTreeSelectionChangedEventArgs>.Subscribers += (sender, e) => RefreshDecompiledView();
		}

		public static void OpenAssembly(string fileName)
		{
			EnsureInitialized();
			assemblyTreeModel.OpenFiles(new[] { fileName });
			RefreshDecompiledView();
		}

		private static void RefreshDecompiledView()
		{
			var exportProvider = App.ExportProvider;
			var languageService = exportProvider.GetExportedValue<LanguageService>();
			var settingsService = exportProvider.GetExportedValue<SettingsService>();
			var options = new DecompilationOptions(languageService.LanguageVersion, settingsService.DecompilerSettings, settingsService.DisplaySettings);

			var nodes = assemblyTreeModel.SelectedNodes.ToArray();
			if (nodes.Length == 0)
				return;

			_ = decompilerTextView.DecompileAsync(languageService.Language, nodes, null, options);
		}
	}
}
