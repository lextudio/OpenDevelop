// This file is NEW glue code written for OpenDevelop. It is NOT linked from the ILSpy
// submodule. It exists because ILSpy's real composition/DI bootstrap lives in
// externals/ilspy/ILSpy/App.xaml.cs, which we deliberately do NOT link (it also owns WPF
// Application lifecycle, single-instance handling, and MainWindow ownership - all out of
// scope for hosting ILSpy panes inside OpenDevelop).
//
// ILSpy's own linked source (e.g. ViewModels/PaneModel.cs, ViewModels/ToolPaneModel.cs)
// references an unqualified "App" type from within the "ICSharpCode.ILSpy.ViewModels"
// namespace (e.g. "App.ExportProvider"). Because C# unqualified name lookup walks up
// enclosing namespaces, and "ICSharpCode.ILSpy.ViewModels" is nested inside
// "ICSharpCode.ILSpy", providing our own type named "App" directly in the "ICSharpCode.ILSpy"
// namespace satisfies that reference without needing to link or modify App.xaml.cs itself.
//
// This mirrors (a strict subset of) App.xaml.cs's InitializeDependencyInjection: build a
// Microsoft.Extensions.DependencyInjection ServiceCollection, bind TomsToolbox
// [Export]-attributed parts from ILSpyX and from this assembly, and wrap the resulting
// IServiceProvider in a TomsToolbox ExportProviderAdapter exposed as IExportProvider.
// Alias shim: DockLayoutSettings.cs and DockWorkspace.wpf.cs (linked, unmodified) do
// `using AvalonDock.Layout.Serialization;` and then reference the unqualified types
// `XmlLayoutSerializer` / `LayoutSerializationCallbackEventArgs`. That namespace matches
// upstream ILSpy's Dirkster.AvalonDock.Themes.VS2013 dependency, but OpenDevelop's own vendored
// AvalonDock fork (src/Libraries/AvalonDock) places the equivalent types under
// AvalonDock.Serializer.Xml and AvalonDock.Core.Serialization instead. Rather than editing the
// linked ILSpy source, declare the (otherwise-missing) namespace so the `using` succeeds, and
// project-wide `global using` alias the real fork types to the names ILSpy's source expects.
global using XmlLayoutSerializer = AvalonDock.Serializer.Xml.XmlLayoutSerializer;
global using LayoutSerializationCallbackEventArgs = AvalonDock.Core.LayoutSerializationCallbackEventArgs;
// DockLayoutSettings.cs calls the TextReader/TextWriter Serialize/Deserialize overloads as
// extension methods; those live in AvalonDock.Core.Serialization (LayoutSerializerExtensions),
// not in the AvalonDock.Layout.Serialization shim namespace above, so bring it into scope here.
global using static AvalonDock.Core.LayoutSerializerExtensions;

using System;
using System.Collections.Generic;
using System.Reflection;

using ICSharpCode.ILSpy.AppEnv;
using ICSharpCode.ILSpy.Docking;
using ICSharpCode.ILSpy.Util;
using ICSharpCode.ILSpyX.Analyzers;

using Microsoft.Extensions.DependencyInjection;

using TomsToolbox.Composition;
using TomsToolbox.Composition.MicrosoftExtensions;

namespace AvalonDock.Layout.Serialization
{
}

namespace ICSharpCode.ILSpy
{
	/// <summary>
	/// Minimal, non-Application composition host that stands in for ILSpy's real
	/// <c>App</c> class (see file header). Only <see cref="ExportProvider"/> is required by
	/// linked ILSpy view-model source; callers that want to start the host should call
	/// <see cref="Initialize"/> once, from OpenDevelop's own composition bootstrap.
	/// </summary>
	public static class App
	{
		public static IExportProvider ExportProvider { get; private set; }

		/// <summary>
		/// Real WPF <see cref="System.Windows.Application"/> of the hosting OpenDevelop process
		/// (OpenDevelop is itself a WPF app, so this always resolves once it's running). Linked
		/// ILSpy source only ever uses <c>App.Current.Dispatcher</c>, never WPF's own
		/// <c>Application</c> members, so proxying to the real current application is correct
		/// and needs no ILSpy-specific stub.
		/// </summary>
		public static System.Windows.Application Current => System.Windows.Application.Current;

		/// <summary>
		/// Mirrors the real (unlinked) App.xaml.cs's parsed command line. Since this addin is
		/// hosted inside OpenDevelop rather than launched as `ILSpy.exe file.dll`, there are no
		/// ILSpy-specific command line arguments to parse - always empty.
		/// </summary>
		public static CommandLineArguments CommandLineArguments { get; } = CommandLineArguments.Create(Array.Empty<string>());

		/// <summary>
		/// Mirrors the real App.xaml.cs's startup-exception list (populated before MainWindow is
		/// shown, when running as a standalone app). Hosted-as-a-pad, there is no separate
		/// startup phase, so this stays empty.
		/// </summary>
		public static IList<ExceptionData> StartupExceptions { get; } = new List<ExceptionData>();

		/// <summary>
		/// Mirrors the real App.xaml.cs's static UnhandledException(Exception) method
		/// (AppDomain/Dispatcher unhandled-exception handlers call it there). OpenDevelop has its
		/// own top-level unhandled-exception handling, so this addin only needs to make the
		/// linked call sites (e.g. AssemblyTreeNode.cs) resolve; it does not re-wire global
		/// exception handlers itself.
		/// </summary>
		public static void UnhandledException(Exception exception)
		{
			System.Diagnostics.Debug.WriteLine(exception);
		}

		/// <summary>
		/// Mirrors the nested record of the same name in the real (unlinked) App.xaml.cs, which
		/// ExtensionMethods.cs and AssemblyTreeModel.cs reference as <c>App.ExceptionData</c>.
		/// </summary>
		public record ExceptionData(Exception Exception)
		{
			public string PluginName { get; init; }
		}

		/// <summary>
		/// Builds the DI container backing <see cref="ExportProvider"/>. Safe to call once;
		/// subsequent calls are no-ops.
		/// </summary>
		public static IExportProvider Initialize()
		{
			if (ExportProvider != null)
				return ExportProvider;

			var services = new ServiceCollection();

			// Built-in parts: first ILSpyX (analyzers, search, tree infrastructure), then
			// whatever [Export]-attributed types live in this assembly (the linked ILSpy
			// pane/tree/search/analyzer view-models).
			services.BindExports(typeof(IAnalyzer).Assembly);
			services.BindExports(Assembly.GetExecutingAssembly());

			// The export provider must be resolvable by parts that ask for it directly.
			services.AddSingleton<IExportProvider>(_ => ExportProvider);

			// MainWindow is a placeholder (see its own doc comment) - not exported via
			// [Export]/BindExports, so DecompilerTextView's constructor (which resolves it
			// unconditionally) needs it registered explicitly.
			services.AddSingleton<MainWindow>();

			// SettingsService isn't [Export]-attributed either (real App.xaml.cs constructs it
			// manually and registers the instance - see App.xaml.cs's InitializeDependencyInjection).
			// Needed transitively by [Export][Shared] parts like LanguageService/DockWorkspace/
			// AssemblyTreeModel.
			services.AddSingleton(new SettingsService());

			var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });

			ExportProvider = new ExportProviderAdapter(serviceProvider);

			return ExportProvider;
		}
	}

	/// <summary>
	/// Placeholder standing in for ILSpy's real <c>MainWindow</c> (see
	/// externals/ilspy/ILSpy/MainWindow.xaml.cs), which we deliberately do not link because it
	/// owns WPF Application/window lifecycle - out of scope for hosting ILSpy panes as
	/// OpenDevelop pads. A handful of linked files (TextView/DecompilerTextView.cs,
	/// TextView/DocumentationUIBuilder.cs, ILSpySettingsFilePathProvider.cs,
	/// Options/ShowOptionsCommand.cs) reference this type by name and use real
	/// <see cref="System.Windows.Window"/> members on it (ActualWidth, CommandBindings,
	/// TaskbarItemInfo), so it derives from Window rather than being a bare stub. It is NOT
	/// wired into the composition container below, so anything that resolves
	/// <c>MainWindow</c> from <see cref="App.ExportProvider"/> at runtime will currently get
	/// nothing - replacing this with real OpenDevelop workbench wiring (e.g. an adapter
	/// exposing OpenDevelop's own dock manager) is part of the next phase.
	/// </summary>
	public sealed class MainWindow : System.Windows.Window
	{
		public AvalonDock.DockingManager DockManager { get; set; }
	}
}
