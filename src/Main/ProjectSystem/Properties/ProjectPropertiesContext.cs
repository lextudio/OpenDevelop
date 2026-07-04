// Clean-room stub. ProjectPropertiesContext is a CPS SDK type.
// Reconstructed from dotnet/project-system MIT usage (DependenciesTreeBuilder.CreateProjectItemTreeNode).

namespace Microsoft.VisualStudio.ProjectSystem.Properties;

/// <summary>
/// Minimal stub of CPS's ProjectPropertiesContext.
/// Provides the static factory used by DependenciesTreeBuilder.
/// </summary>
public sealed class ProjectPropertiesContext : IProjectPropertiesContext
{
    public bool IsProjectFile { get; }
    public string File { get; }
    public string? ItemType { get; }
    public string? ItemName { get; }

    private ProjectPropertiesContext(bool isProjectFile, string file, string? itemType, string? itemName)
    {
        IsProjectFile = isProjectFile;
        File = file;
        ItemType = itemType;
        ItemName = itemName;
    }

    public static IProjectPropertiesContext GetContext(
        UnconfiguredProject project,
        string? file,
        string? itemType,
        string? itemName) =>
        new ProjectPropertiesContext(
            isProjectFile: false,
            file: file ?? project.FullPath,
            itemType: itemType,
            itemName: itemName);
}
