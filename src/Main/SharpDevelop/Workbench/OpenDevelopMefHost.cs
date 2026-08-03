using System;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using TomsToolbox.Composition;
using TomsToolbox.Composition.MicrosoftExtensions;

namespace ICSharpCode.SharpDevelop.Workbench;

// TomsToolbox composition migration (doc/technotes/ilspy.md "Immediate next actions" #6,
// 2026-08-02): OpenDevelop's own MEF usage was always narrow (this host, plus
// ProjectBrowserViewModel as the one real [Export]-attributed part), so replacing
// Microsoft.VisualStudio.Composition here is the whole migration, not step one of a larger
// sweep - no [ImportingConstructor]/[Import] graph existed anywhere to untangle. Mirrors
// src/AddIns/DisplayBindings/ILSpyAddIn/ILSpyCompositionHost.cs's App.Initialize() (ILSpy already
// uses this same TomsToolbox-over-Microsoft.Extensions.DependencyInjection pattern), so both
// composition containers in the process now use the same underlying technology - the small
// registration API named in "Composition boundary" above (IToolPaneProvider/IDocumentPaneFactory)
// can follow later as the one seam both feed, rather than needing to bridge two different MEF
// implementations.
internal static class OpenDevelopMefHost
{
    private static readonly Lazy<IExportProvider> LazyExportProvider = new(BuildExportProvider);

    public static IExportProvider ExportProvider => LazyExportProvider.Value;

    private static IExportProvider BuildExportProvider()
    {
        var services = new ServiceCollection();

        // BindExports scans the given assembly's System.Composition-attributed types
        // ([Export]/[Shared]) - the same attributes ProjectBrowserViewModel already carries
        // (it was written against System.Composition from the start, so no attribute changes were
        // needed on that class for this migration).
        services.BindExports(Assembly.GetExecutingAssembly());

        IExportProvider exportProvider = null;
        // The export provider must be resolvable by parts that ask for it directly (mirrors
        // ILSpyCompositionHost.cs's identical registration).
        services.AddSingleton(_ => exportProvider);

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });

        exportProvider = new ExportProviderAdapter(serviceProvider);

        return exportProvider;
    }
}
