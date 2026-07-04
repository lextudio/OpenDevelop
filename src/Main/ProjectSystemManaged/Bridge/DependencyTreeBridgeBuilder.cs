using System.Reflection;
using System.Security;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.ProjectSystem.Properties;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Snapshot;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies.Subscriptions.MSBuildDependencies;

namespace Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies;

public enum DependencyBridgeItemKind
{
    Assembly,
    Project,
    Package,
    Analyzer,
    Com,
    Framework,
    Sdk
}

public sealed record DependencyBridgeItem(
    DependencyBridgeItemKind Kind,
    string Id,
    string? FilePath,
    IImmutableDictionary<string, string> Metadata,
    IImmutableSet<string>? TargetFrameworks = null);

public static class DependencyTreeBridgeBuilder
{
    public static MutableProjectTree? BuildDependenciesTree(string projectPath, IEnumerable<DependencyBridgeItem> items,
        IEnumerable<string>? targetFrameworks = null)
    {
        var itemArray = items.ToImmutableArray();
        if (itemArray.Length == 0)
        {
            return null;
        }

        var unconfiguredProject = new BridgeUnconfiguredProject(projectPath);
        var builder = new DependenciesTreeBuilder(unconfiguredProject)
        {
            TreeConstruction = new BridgeTreeOperations()
        };

        var slices = CreateSlices(targetFrameworks);
        var dependenciesBySlice = ImmutableDictionary<ProjectConfigurationSlice, DependenciesSnapshotSlice>.Empty;
        foreach (var slice in slices)
        {
            var dependenciesByType = CreateDependenciesByType(itemArray, GetTargetFramework(slice));
            DependenciesSnapshotSlice? snapshotSlice = null;
            DependenciesSnapshotSlice.Update(
                ref snapshotSlice,
                slice,
                new BridgeConfiguredProject(unconfiguredProject, slice),
                new BridgeCatalogSnapshot(),
                new[] { dependenciesByType });

            dependenciesBySlice = dependenciesBySlice.Add(slice, snapshotSlice);
        }

        var snapshot = new DependenciesSnapshot(
            slices[0],
            dependenciesBySlice,
            ImmutableDictionary<DependencyGroupType, ImmutableArray<IDependency>>.Empty);

        return (MutableProjectTree)builder.BuildTreeAsync(null, snapshot, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string? GetTargetFramework(ProjectConfigurationSlice slice) =>
        slice.Dimensions.TryGetValue(ConfigurationGeneral.TargetFrameworkProperty, out var targetFramework)
            ? targetFramework
            : null;

    private static ImmutableArray<ProjectConfigurationSlice> CreateSlices(IEnumerable<string>? targetFrameworks)
    {
        var frameworks = targetFrameworks?
            .Select(framework => framework.Trim())
            .Where(framework => framework.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (frameworks is null || frameworks.Length <= 1)
        {
            return ImmutableArray.Create(ProjectConfigurationSlice.Create(ImmutableDictionary<string, string>.Empty));
        }

        return frameworks
            .Select(framework => ProjectConfigurationSlice.Create(
                ImmutableDictionary<string, string>.Empty.Add(ConfigurationGeneral.TargetFrameworkProperty, framework)))
            .ToImmutableArray();
    }

    private static ImmutableDictionary<DependencyGroupType, ImmutableArray<IDependency>> CreateDependenciesByType(IEnumerable<DependencyBridgeItem> items, string? targetFramework)
    {
        var groups = new Dictionary<DependencyGroupType, List<IDependency>>();

        foreach (var item in items)
        {
            if (!AppliesToTargetFramework(item, targetFramework))
            {
                continue;
            }

            var factory = GetFactory(item.Kind);
            var dependency = CreateDependency(factory, item);
            if (dependency is null)
            {
                continue;
            }

            var groupType = factory.DependencyGroupType;
            if (!groups.TryGetValue(groupType, out var dependencies))
            {
                dependencies = new List<IDependency>();
                groups.Add(groupType, dependencies);
            }

            dependencies.Add(dependency);
        }

        return groups.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableArray());
    }

    private static bool AppliesToTargetFramework(DependencyBridgeItem item, string? targetFramework)
    {
        if (item.TargetFrameworks is null)
        {
            return true;
        }

        if (item.TargetFrameworks.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return true;
        }

        return item.TargetFrameworks.Contains(targetFramework);
    }

    internal static MSBuildDependencyFactoryBase GetFactory(DependencyBridgeItemKind kind) =>
        kind switch
        {
            DependencyBridgeItemKind.Project => new ProjectDependencyFactory(),
            DependencyBridgeItemKind.Package => new PackageDependencyFactory(),
            DependencyBridgeItemKind.Analyzer => new AnalyzerDependencyFactory(),
            DependencyBridgeItemKind.Com => new ComDependencyFactory(),
            DependencyBridgeItemKind.Framework => new FrameworkDependencyFactory(),
            DependencyBridgeItemKind.Sdk => new SdkDependencyFactory(),
            _ => new AssemblyDependencyFactory()
        };

    private static IDependency? CreateDependency(MSBuildDependencyFactoryBase factory, DependencyBridgeItem item)
    {
        var metadata = MergeMetadata(item);

        var evaluation = (ItemSpec: item.Id, Properties: (IImmutableDictionary<string, string>)metadata);
        (string ItemSpec, IImmutableDictionary<string, string> Properties)? build = null;
        if (item.Kind is not DependencyBridgeItemKind.Sdk)
        {
            var buildItemSpec = string.IsNullOrWhiteSpace(item.FilePath) ? item.Id : item.FilePath!;
            build = (ItemSpec: buildItemSpec, Properties: (IImmutableDictionary<string, string>)metadata);
        }

        return factory.CreateDependency(
            id: item.Id,
            evaluation: evaluation,
            build: build,
            projectFullPath: string.Empty,
            hasBuildError: false,
            isEvaluationOnlySnapshot: build is null);
    }

    internal static IImmutableDictionary<string, string> MergeMetadata(DependencyBridgeItem item)
    {
        var builder = ImmutableStringDictionary<string>.EmptyOrdinal.ToBuilder();
        SetDefault(ProjectItemMetadata.OriginalItemSpec, item.Id);
        SetDefault(ProjectItemMetadata.Name, item.Id);
        SetDefault(ProjectItemMetadata.Visible, bool.TrueString);
        SetDefault(ProjectItemMetadata.IsImplicitlyDefined, bool.FalseString);
        SetDefault(ProjectItemMetadata.DefiningProjectFullPath, string.Empty);
        SetDefault(Folder.IdentityProperty, item.Id);
        SetDefault(Folder.FullPathProperty, item.FilePath ?? string.Empty);

        foreach (var pair in item.Metadata)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                builder[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        if (item.Kind == DependencyBridgeItemKind.Assembly && !builder.ContainsKey(ResolvedAssemblyReference.FusionNameProperty)
            && !string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath))
        {
            try
            {
                builder[ResolvedAssemblyReference.FusionNameProperty] = AssemblyName.GetAssemblyName(item.FilePath).FullName;
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or SecurityException)
            {
                // Not a loadable managed assembly (e.g. a native DLL reference); fall back to the raw item spec.
            }
        }

        return builder.ToImmutable();

        void SetDefault(string name, string value)
        {
            if (!builder.ContainsKey(name))
            {
                builder[name] = value;
            }
        }
    }

    internal sealed class BridgeTreeOperations : IProjectTreeOperations
    {
        public ValueTask<IRule?> GetDependencyBrowseObjectRuleAsync(IDependencyWithBrowseObject dependency, ConfiguredProject? configuredProject, IProjectCatalogSnapshot? catalogs) =>
            ValueTask.FromResult<IRule?>(new SimpleRule(
                dependency.SchemaName ?? Folder.SchemaName,
                dependency.SchemaItemType,
                dependency.BrowseObjectProperties.GetValueOrDefault(ProjectItemMetadata.OriginalItemSpec)
                    ?? dependency.BrowseObjectProperties.GetValueOrDefault(Folder.IdentityProperty)
                    ?? dependency.FilePath));

        public IProjectTree2 NewTree(string caption, string? filePath = null, IRule? browseObjectProperties = null, ProjectImageMoniker? icon = null, ProjectImageMoniker? expandedIcon = null, bool visible = true, ProjectTreeFlags? flags = null, int displayOrder = 0) =>
            new MutableProjectTree(caption, filePath)
            {
                BrowseObjectProperties = browseObjectProperties,
                Icon = icon,
                ExpandedIcon = expandedIcon,
                Visible = visible,
                Flags = flags ?? ProjectTreeFlags.Empty,
                DisplayOrder = displayOrder
            };

        public IProjectItemTree2 NewTree(string caption, IProjectPropertiesContext item, IPropertySheet? propertySheet, IRule? browseObjectProperties = null, ProjectImageMoniker? icon = null, ProjectImageMoniker? expandedIcon = null, bool visible = true, ProjectTreeFlags? flags = null, bool isLinked = false, int displayOrder = 0) =>
            new MutableProjectItemTree(caption, item, item.File)
            {
                PropertySheet = propertySheet,
                BrowseObjectProperties = browseObjectProperties,
                Icon = icon,
                ExpandedIcon = expandedIcon,
                Visible = visible,
                Flags = flags ?? ProjectTreeFlags.Empty,
                IsLinked = isLinked,
                DisplayOrder = displayOrder
            };
    }

    internal sealed class BridgeUnconfiguredProject(string fullPath) : UnconfiguredProject
    {
        public override string FullPath { get; } = fullPath;
    }

    internal sealed class BridgeConfiguredProject(UnconfiguredProject unconfiguredProject, ProjectConfigurationSlice slice) : ConfiguredProject
    {
        public override UnconfiguredProject UnconfiguredProject { get; } = unconfiguredProject;
        public override ProjectConfiguration ProjectConfiguration { get; } = new("Bridge", slice.Dimensions);
    }

    internal sealed class BridgeCatalogSnapshot : IProjectCatalogSnapshot
    {
        public IImmutableDictionary<string, IPropertyPagesCatalog> NamedCatalogs { get; } = ImmutableDictionary<string, IPropertyPagesCatalog>.Empty;
        public ConfiguredProject? Project => null;
    }
}
