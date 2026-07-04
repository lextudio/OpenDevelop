// A genuine VS MEF (Microsoft.VisualStudio.Composition — real, MIT, github.com/microsoft/vs-mef)
// composition host, discovering the stateless (parameterless-constructible) [Export]-attributed
// parts linked from upstream project-system — currently just the seven IMSBuildDependencyFactory
// implementations. Parts requiring per-project runtime data in their constructor (e.g.
// MSBuildDependencySubscriber, which needs a specific UnconfiguredProject instance) aren't
// resolvable through simple attributed discovery — real CPS handles that via a per-project
// hierarchical MEF scope, which this shim does not model. Those still use
// Dataflow/ManualComposition.cs's reflection-based field injection (see
// Bridge/SharpDevelopDependenciesSnapshotFactory.cs). See docs/project-system.md (Slice 44).

using Microsoft.VisualStudio.Composition;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions.MSBuildDependencies;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies;

internal static class RealMefHost
{
    private static readonly Lazy<ExportProvider> LazyExportProvider = new(BuildExportProvider);

    public static ExportProvider ExportProvider => LazyExportProvider.Value;

    private static ExportProvider BuildExportProvider()
    {
        var discovery = new AttributedPartDiscovery(Resolver.DefaultInstance, isNonPublicSupported: true);
        var discoveredParts = discovery.CreatePartsAsync(typeof(AssemblyDependencyFactory).Assembly).GetAwaiter().GetResult();

        var catalog = ComposableCatalog.Create(Resolver.DefaultInstance).AddParts(discoveredParts.Parts);
        var configuration = CompositionConfiguration.Create(catalog);

        // Not calling ThrowOnErrors(): other parts in this assembly (e.g. MSBuildDependencySubscriber)
        // need a per-project UnconfiguredProject instance that plain attributed discovery can't
        // supply, and will show up as unrelated composition errors here. That's expected — we only
        // resolve IMSBuildDependencyFactory below, which has no such unsatisfiable imports.
        return configuration.CreateExportProviderFactory().CreateExportProvider();
    }
}
