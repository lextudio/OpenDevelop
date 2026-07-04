// Slice 6: wires the SharpDevelop/MSBuild project model to the CPS tree model.
// See docs/project-system.md.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using ICSharpCode.SharpDevelop.Project;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Tree.Dependencies;

namespace ICSharpCode.SharpDevelop.Services;

/// <summary>
/// Builds a CPS <see cref="MutableProjectTree"/> from a SharpDevelop <see cref="IProject"/>.
/// This is the bridge between the SD project model and the CPS tree that the Solution
/// Explorer will eventually consume instead of <see cref="SolutionExplorerTreeBuilder"/>.
/// </summary>
internal sealed class SharpDevelopProjectTreeProvider : ProjectTreeProviderBase
{
    private readonly IProject _project;
    private readonly string? _projectPath;
    private readonly string? _displayName;
    private readonly bool _showAllFiles;

    public SharpDevelopProjectTreeProvider(IProject project, bool showAllFiles = false)
    {
        _project = project;
        _projectPath = project.FileName.ToString();
        _displayName = project.Name;
        _showAllFiles = showAllFiles;
    }

    public SharpDevelopProjectTreeProvider(string projectPath, string? displayName = null, bool showAllFiles = false)
    {
        _project = null!;
        _projectPath = projectPath;
        _displayName = displayName;
        _showAllFiles = showAllFiles;
    }

    // ── ProjectTreeProviderBase ───────────────────────────────────────────────

    public override MutableProjectTree BuildTree()
    {
        var projectFile = _projectPath ?? _project.FileName.ToString();
        var projectDir  = Path.GetDirectoryName(projectFile) ?? projectFile;

        var root = new MutableProjectTree(GetProjectName(projectFile), projectFile)
        {
            Flags = ProjectTreeFlags.Common.ProjectRoot
                  + ProjectTreeFlags.Common.FileSystemEntity,
        };

        AddSpecialNodes(root, projectFile, projectDir);
        AddProjectContentNodes(root, projectFile, projectDir);

        return root;
    }

    /// <summary>Async counterpart of <see cref="BuildTree"/>, routing the dependencies node through the real CPS dataflow pipeline. See docs/project-system.md (Slice 46).</summary>
    public async Task<MutableProjectTree> BuildTreeAsync()
    {
        var projectFile = _projectPath ?? _project.FileName.ToString();
        var projectDir  = Path.GetDirectoryName(projectFile) ?? projectFile;

        var root = new MutableProjectTree(GetProjectName(projectFile), projectFile)
        {
            Flags = ProjectTreeFlags.Common.ProjectRoot
                  + ProjectTreeFlags.Common.FileSystemEntity,
        };

        await AddSpecialNodesAsync(root, projectFile, projectDir);
        AddProjectContentNodes(root, projectFile, projectDir);

        return root;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string GetProjectName(string projectFile)
    {
        return string.IsNullOrWhiteSpace(_displayName)
            ? Path.GetFileNameWithoutExtension(projectFile)
            : _displayName!;
    }

    private void AddSpecialNodes(MutableProjectTree root, string projectFile, string projectDir)
    {
        var (dependencies, projectItemsByInclude, targetFrameworks) = GatherDependencyItems(projectFile, projectDir);

        var dependenciesNode = DependencyTreeBridgeBuilder.BuildDependenciesTree(projectFile, dependencies, targetFrameworks);
        AttachDependenciesNode(root, dependenciesNode, projectItemsByInclude);
    }

    /// <summary>
    /// Async counterpart of <see cref="AddSpecialNodes"/> that routes through the real CPS
    /// dataflow pipeline (<see cref="SharpDevelopDependenciesSnapshotFactory.BuildTreeAsync"/>,
    /// slice 46) instead of the imperative <see cref="DependencyTreeBridgeBuilder"/> — falling back
    /// to that same imperative path if the dataflow pipeline doesn't produce a snapshot in time.
    /// See docs/project-system.md (Slice 46).
    /// </summary>
    private async Task AddSpecialNodesAsync(MutableProjectTree root, string projectFile, string projectDir)
    {
        var (dependencies, projectItemsByInclude, targetFrameworks) = GatherDependencyItems(projectFile, projectDir);
        var itemsByTargetFramework = GroupItemsByTargetFramework(dependencies, targetFrameworks);

        var dependenciesNode = await SharpDevelopDependenciesSnapshotFactory.BuildTreeAsync(projectFile, itemsByTargetFramework)
            ?? DependencyTreeBridgeBuilder.BuildDependenciesTree(projectFile, dependencies, targetFrameworks);

        AttachDependenciesNode(root, dependenciesNode, projectItemsByInclude);
    }

    private static void AttachDependenciesNode(MutableProjectTree root, MutableProjectTree? dependenciesNode, IReadOnlyDictionary<string, ProjectItem> projectItemsByInclude)
    {
        if (dependenciesNode is not null)
        {
            NormalizeDependencyTree(dependenciesNode);
            root.AddChild(dependenciesNode);
        }
    }

    private (List<DependencyBridgeItem> Dependencies, Dictionary<string, ProjectItem> ProjectItemsByInclude, ImmutableArray<string> TargetFrameworks) GatherDependencyItems(string projectFile, string projectDir)
    {
        var dependencies = new List<DependencyBridgeItem>();
        var projectItemsByInclude = new Dictionary<string, ProjectItem>(StringComparer.OrdinalIgnoreCase);
        var targetFrameworks = GetTargetFrameworks(projectFile).ToImmutableArray();

        if (_project is not null)
        {
            foreach (var item in _project.Items.CreateSnapshot())
            {
                if (item.ItemType.ItemName == "PackageReference")
                {
                    AddDependencyItem(dependencies, item.ItemType.ItemName, item.Include, ResolveAbsolutePath(item, projectDir), GetProjectItemMetadata(item));
                    projectItemsByInclude[item.Include] = item;
                }
                else if (IsRefItem(item.ItemType.ItemName))
                {
                    AddDependencyItem(dependencies, item.ItemType.ItemName, item.Include, ResolveAbsolutePath(item, projectDir), GetProjectItemMetadata(item));
                    projectItemsByInclude[item.Include] = item;
                }
            }
        }
        else
        {
            foreach (var item in ReadProjectItems(projectFile))
            {
                var include = item.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                switch (item.Name.LocalName)
                {
                    case "Reference":
                        AddDependencyItem(dependencies, "Reference", include, include, GetProjectItemMetadata(item), GetConditionTargetFrameworks(item, targetFrameworks));
                        break;
                    case "ProjectReference":
                        AddDependencyItem(dependencies, "ProjectReference", include,
                            Path.GetFullPath(Path.Combine(projectDir, include.Replace('\\', Path.DirectorySeparatorChar))),
                            GetProjectItemMetadata(item),
                            GetConditionTargetFrameworks(item, targetFrameworks));
                        break;
                    case "Analyzer":
                    case "COMReference":
                    case "FrameworkReference":
                    case "SDKReference":
                        AddDependencyItem(dependencies, item.Name.LocalName, include, include, GetProjectItemMetadata(item), GetConditionTargetFrameworks(item, targetFrameworks));
                        break;
                    case "PackageReference":
                        AddDependencyItem(dependencies, "PackageReference", include, include, GetProjectItemMetadata(item), GetConditionTargetFrameworks(item, targetFrameworks));
                        break;
                }
            }
        }

        return (dependencies, projectItemsByInclude, targetFrameworks);
    }

    /// <summary>
    /// Buckets the flat, per-item-TFM-filtered dependency list into one list per target framework,
    /// the shape <see cref="SharpDevelopDependenciesSnapshotFactory.BuildTreeAsync"/> expects. An item
    /// with a null <see cref="DependencyBridgeItem.TargetFrameworks"/> applies to every slice; a
    /// project with no target frameworks gets a single empty-string ("no TFM slicing") bucket.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<DependencyBridgeItem>> GroupItemsByTargetFramework(
        List<DependencyBridgeItem> dependencies, ImmutableArray<string> targetFrameworks)
    {
        var tfmKeys = targetFrameworks.IsDefaultOrEmpty ? ImmutableArray.Create("") : targetFrameworks;

        return tfmKeys.ToDictionary(
            tfm => tfm,
            IReadOnlyList<DependencyBridgeItem> (tfm) => dependencies
                .Where(item => item.TargetFrameworks is null || item.TargetFrameworks.Contains(tfm))
                .ToList());
    }

    private static void AddDependencyItem(List<DependencyBridgeItem> dependencies, string typeName, string include, string? path,
        IImmutableDictionary<string, string> metadata,
        IImmutableSet<string>? targetFrameworks = null)
    {
        dependencies.Add(new DependencyBridgeItem(GetDependencyKind(typeName), include, path, metadata, targetFrameworks));
    }

    private static IImmutableDictionary<string, string> GetProjectItemMetadata(ProjectItem item)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var name in item.MetadataNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                builder[name] = item.GetEvaluatedMetadata(name) ?? string.Empty;
            }
        }

        return builder.ToImmutable();
    }

    private static IImmutableSet<string>? GetConditionTargetFrameworks(XElement item, IReadOnlyCollection<string> allTargetFrameworks)
    {
        return IntersectTargetFrameworks(
            GetTargetFrameworksFromCondition(item.Parent?.Attribute("Condition")?.Value, allTargetFrameworks),
            GetTargetFrameworksFromCondition(item.Attribute("Condition")?.Value, allTargetFrameworks));
    }

    private static IImmutableSet<string>? GetTargetFrameworksFromCondition(string? condition, IReadOnlyCollection<string> allTargetFrameworks)
    {
        if (string.IsNullOrWhiteSpace(condition) || !condition.Contains("TargetFramework", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var matches = Regex.Matches(condition,
            @"(?<left>'[^']+'|""[^""]+""|\$\(\s*TargetFramework\s*\))\s*(?<op>==|!=)\s*(?<right>'[^']+'|""[^""]+""|\$\(\s*TargetFramework\s*\))",
            RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return null;
        }

        var includes = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var excludes = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            if (!TryGetTargetFrameworkComparisonValue(match, out var targetFramework))
            {
                continue;
            }

            if (match.Groups["op"].Value == "==")
            {
                includes.Add(targetFramework);
            }
            else
            {
                excludes.Add(targetFramework);
            }
        }

        if (includes.Count > 0)
        {
            return includes.Except(excludes).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (excludes.Count == 0 || allTargetFrameworks.Count == 0)
        {
            return null;
        }

        return allTargetFrameworks
            .Where(framework => !excludes.Contains(framework))
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetTargetFrameworkComparisonValue(Match match, out string targetFramework)
    {
        targetFramework = string.Empty;

        var left = match.Groups["left"].Value.Trim();
        var right = match.Groups["right"].Value.Trim();
        var leftIsTargetFramework = IsTargetFrameworkProperty(left);
        var rightIsTargetFramework = IsTargetFrameworkProperty(right);
        if (leftIsTargetFramework == rightIsTargetFramework)
        {
            return false;
        }

        targetFramework = Unquote(leftIsTargetFramework ? right : left);
        return targetFramework.Length > 0;
    }

    private static bool IsTargetFrameworkProperty(string value) =>
        Regex.IsMatch(Unquote(value), @"^\$\(\s*TargetFramework\s*\)$", RegexOptions.IgnoreCase);

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2
            && ((value[0] == '\'' && value[^1] == '\'')
                || (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private static IImmutableSet<string>? IntersectTargetFrameworks(IImmutableSet<string>? first, IImmutableSet<string>? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return first.Intersect(second);
    }

    private static IImmutableDictionary<string, string> GetProjectItemMetadata(XElement item)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (var attribute in item.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration
                && !string.Equals(attribute.Name.LocalName, "Include", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(attribute.Name.LocalName))
            {
                builder[attribute.Name.LocalName] = attribute.Value;
            }
        }

        foreach (var metadataElement in item.Elements())
        {
            if (!string.IsNullOrWhiteSpace(metadataElement.Name.LocalName))
            {
                builder[metadataElement.Name.LocalName] = metadataElement.Value;
            }
        }

        return builder.ToImmutable();
    }

    private static void NormalizeDependencyTree(MutableProjectTree dependenciesNode)
    {
        dependenciesNode.Flags += ProjectTreeFlags.Common.DependenciesFolder;
        NormalizeDependencyChildren(dependenciesNode);
    }

    private static void NormalizeDependencyChildren(MutableProjectTree node)
    {
        foreach (var child in node.MutableChildren)
        {
            child.Flags += GetDependencyGroupFlag(child.Caption);
            child.Flags += GetDependencyLeafFlag(child.BrowseObjectProperties?.ItemType);
            NormalizeDependencyChildren(child);
        }
    }

    private IEnumerable<string> GetTargetFrameworks(string projectFile)
    {
        if (_project is MSBuildBasedProject msbuildProject)
        {
            return SplitTargetFrameworks(msbuildProject.GetEvaluatedProperty(ConfigurationGeneral.TargetFrameworksProperty))
                .DefaultIfEmpty(msbuildProject.GetEvaluatedProperty(ConfigurationGeneral.TargetFrameworkProperty) ?? string.Empty)
                .Where(framework => !string.IsNullOrWhiteSpace(framework));
        }

        return GetTargetFrameworksFromProjectFile(projectFile);
    }

    private static ProjectTreeFlags GetDependencyGroupFlag(string groupName) =>
        groupName switch
        {
            "Packages" => ProjectTreeFlags.Common.PackagesFolder,
            "Assemblies" or "Projects" or "Analyzers" or "COM" or "Frameworks" or "SDKs" => ProjectTreeFlags.Common.ReferencesFolder,
            _ => ProjectTreeFlags.Empty
        };

    private static ProjectTreeFlags GetDependencyLeafFlag(string? schemaName) =>
        schemaName switch
        {
            DependencyRuleNames.ProjectReference => ProjectTreeFlags.Common.ProjectReference,
            DependencyRuleNames.PackageReference => ProjectTreeFlags.Common.PackageReference,
            _ => ProjectTreeFlags.Empty
        };

    private static DependencyBridgeItemKind GetDependencyKind(string typeName) =>
        typeName switch
        {
            "ProjectReference" => DependencyBridgeItemKind.Project,
            "PackageReference" => DependencyBridgeItemKind.Package,
            "Analyzer" => DependencyBridgeItemKind.Analyzer,
            "COMReference" => DependencyBridgeItemKind.Com,
            "FrameworkReference" => DependencyBridgeItemKind.Framework,
            "SDKReference" => DependencyBridgeItemKind.Sdk,
            _ => DependencyBridgeItemKind.Assembly
        };

    private void AddProjectContentNodes(MutableProjectTree root, string projectFile, string projectDir)
    {
        var projectItems = _project is not null
            ? ProjectDisplayItems.GetProjectDisplayItems(_project)
            : Array.Empty<ProjectDisplayItems.ProjectDisplayItem>();
        var projectItemPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in projectItems)
        {
            var itemPath = Path.GetFullPath(item.PhysicalPath);
            projectItemPaths.Add(itemPath);
            if (string.Equals(itemPath, Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddProjectItemNode(root, projectDir, item);
        }

        if (!_showAllFiles || !Directory.Exists(projectDir))
        {
            return;
        }

        foreach (var file in EnumeratePhysicalProjectFiles(projectDir))
        {
            var fullPath = Path.GetFullPath(file);
            if (projectItemPaths.Contains(fullPath)
                || string.Equals(fullPath, Path.GetFullPath(projectFile), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddGhostProjectFileNode(root, projectDir, fullPath);
        }
    }

    private static void AddProjectItemNode(MutableProjectTree root, string projectDir, ProjectDisplayItems.ProjectDisplayItem item)
    {
        var segments = SplitDisplayPath(item.DisplayPath);
        if (segments.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.DependentUpon))
        {
            AddDependentProjectItemNode(root, projectDir, item, segments);
            return;
        }

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = GetOrAddFolder(current, segments[i], Path.Combine(projectDir, BuildDisplayPath(segments.Take(i + 1))), isGhost: false);
        }

        current.AddChild(CreateFileNode(segments[^1], item.PhysicalPath, isLinked: item.IsLinked, isMissing: !item.Exists, isGhost: false,
            isProjectItem: true));
    }

    private static void AddGhostProjectFileNode(MutableProjectTree root, string projectDir, string fullPath)
    {
        var segments = SplitDisplayPath(Path.GetRelativePath(projectDir, fullPath));
        if (segments.Length == 0)
        {
            return;
        }

        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = GetOrAddFolder(current, segments[i], Path.Combine(projectDir, BuildDisplayPath(segments.Take(i + 1))), isGhost: true);
        }

        current.AddChild(CreateFileNode(segments[^1], fullPath, isLinked: false, isMissing: false, isGhost: true, isProjectItem: false));
    }

    private static void AddDependentProjectItemNode(MutableProjectTree root, string projectDir, ProjectDisplayItems.ProjectDisplayItem item, string[] childSegments)
    {
        var current = root;
        for (var i = 0; i < childSegments.Length - 1; i++)
        {
            current = GetOrAddFolder(current, childSegments[i], Path.Combine(projectDir, BuildDisplayPath(childSegments.Take(i + 1))), isGhost: false);
        }

        var parentName = item.DependentUpon!;
        var parentPath = Path.GetFullPath(Path.Combine(projectDir, BuildDisplayPath(childSegments.Take(childSegments.Length - 1)), parentName));
        var parent = GetOrAddFile(current, parentName, parentPath, isLinked: false, isMissing: !File.Exists(parentPath), isGhost: false, isProjectItem: false);
        parent.AddChild(CreateFileNode(childSegments[^1], item.PhysicalPath, item.IsLinked, !item.Exists, isGhost: false,
            isProjectItem: true));
    }

    private static MutableProjectTree GetOrAddFolder(MutableProjectTree parent, string name, string fullPath, bool isGhost)
    {
        var existing = parent.MutableChildren.FirstOrDefault(child =>
            child.IsFolder && string.Equals(child.Caption, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!isGhost)
            {
                existing.Flags = existing.Flags.Except(ProjectTreeFlags.Common.IncludeInProjectCandidate);
            }

            return existing;
        }

        var flags = ProjectTreeFlags.Common.Folder + ProjectTreeFlags.Common.FileSystemEntity;
        if (isGhost)
        {
            flags += ProjectTreeFlags.Common.IncludeInProjectCandidate;
            flags += ProjectTreeFlags.Common.VisibleOnlyInShowAllFiles;
        }

        var folder = new MutableProjectTree(name, fullPath) { Flags = flags };
        parent.AddChild(folder);
        return folder;
    }

    private static MutableProjectTree GetOrAddFile(MutableProjectTree parent, string name, string fullPath, bool isLinked, bool isMissing, bool isGhost, bool isProjectItem)
    {
        var existing = parent.MutableChildren.FirstOrDefault(child =>
            !child.IsFolder && string.Equals(child.Caption, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var file = CreateFileNode(name, fullPath, isLinked, isMissing, isGhost, isProjectItem);
        parent.AddChild(file);
        return file;
    }

    private static MutableProjectTree CreateFileNode(string name, string fullPath, bool isLinked, bool isMissing, bool isGhost,
        bool isProjectItem)
    {
        var flags = ProjectTreeFlags.Common.SourceFile;
        if (!isMissing)
        {
            flags += ProjectTreeFlags.Common.FileSystemEntity;
        }

        if (isGhost)
        {
            flags += ProjectTreeFlags.Common.IncludeInProjectCandidate;
            flags += ProjectTreeFlags.Common.VisibleOnlyInShowAllFiles;
        }

        if (isLinked)
        {
            flags += ProjectTreeFlags.Common.LinkedFile;
        }

        return new MutableProjectTree(name, fullPath)
        {
            Flags = flags,
            IsProjectItem = isProjectItem,
        };
    }

    private static string[] SplitDisplayPath(string displayPath)
    {
        return displayPath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\', '/')
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();
    }

    private static string BuildDisplayPath(IEnumerable<string> segments)
    {
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string? ResolveAbsolutePath(ProjectItem item, string projectDir)
    {
        try
        {
            var path = item.FileName?.ToString();
            if (path is null) return null;
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(projectDir, path));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRefItem(string name) =>
        name is "Reference" or "ProjectReference" or "PackageReference"
               or "Analyzer" or "COMReference" or "FrameworkReference" or "SDKReference";

    private static IEnumerable<XElement> ReadProjectItems(string projectFile)
    {
        if (!File.Exists(projectFile))
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFile);
        }
        catch
        {
            yield break;
        }

        foreach (var item in document.Descendants().Where(element => element.Parent?.Name.LocalName == "ItemGroup"))
        {
            yield return item;
        }
    }

    private static IEnumerable<string> GetTargetFrameworksFromProjectFile(string projectFile)
    {
        if (!File.Exists(projectFile))
        {
            yield break;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFile);
        }
        catch
        {
            yield break;
        }

        var frameworks = ImmutableArray.CreateBuilder<string>();
        foreach (var targetFrameworks in document.Descendants()
            .Where(element => element.Name.LocalName == ConfigurationGeneral.TargetFrameworksProperty)
            .Select(element => element.Value))
        {
            frameworks.AddRange(SplitTargetFrameworks(targetFrameworks));
        }

        if (frameworks.Count > 0)
        {
            foreach (var framework in frameworks.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return framework;
            }

            yield break;
        }

        var targetFramework = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == ConfigurationGeneral.TargetFrameworkProperty)
            ?.Value;
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            yield return targetFramework.Trim();
        }
    }

    private static IEnumerable<string> SplitTargetFrameworks(string? targetFrameworks) =>
        (targetFrameworks ?? string.Empty)
            .Split(';')
            .Select(framework => framework.Trim())
            .Where(framework => framework.Length > 0 && !framework.Contains("$(", StringComparison.Ordinal));

    private static IEnumerable<string> EnumeratePhysicalProjectFiles(string projectDir)
    {
        foreach (var file in Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories))
        {
            if (IsExcludedProjectPath(file, projectDir) || !IsSupportedProjectTreePath(file))
            {
                continue;
            }

            yield return file;
        }
    }

    private static bool IsExcludedProjectPath(string path, string projectDir)
    {
        var relative = Path.GetRelativePath(projectDir, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSupportedProjectTreePath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".props", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".targets", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase);
    }
}
