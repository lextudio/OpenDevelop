using System;
using System.Reflection;

using Microsoft.VisualStudio.Composition;

namespace ICSharpCode.SharpDevelop.Workbench;

internal static class OpenDevelopMefHost
{
    private static readonly Lazy<ExportProvider> LazyExportProvider = new(BuildExportProvider);

    public static ExportProvider ExportProvider => LazyExportProvider.Value;

    private static ExportProvider BuildExportProvider()
    {
        var discovery = new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true);
        var discoveredParts = discovery.CreatePartsAsync(Assembly.GetExecutingAssembly()).GetAwaiter().GetResult();
        var catalog = ComposableCatalog.Create(Resolver.DefaultInstance).AddParts(discoveredParts.Parts);
        var configuration = CompositionConfiguration.Create(catalog);
        return configuration.CreateExportProviderFactory().CreateExportProvider();
    }
}
