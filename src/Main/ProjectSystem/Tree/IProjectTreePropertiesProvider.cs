// Clean-room stubs for CPS tree property provider interfaces.
// These come from the proprietary CPS SDK; we reproduce only the surface
// consumed by dotnet/project-system's AbstractSpecialFolderProjectTreePropertiesProvider.
// See docs/project-system.md.

using System.Collections.Immutable;

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>
/// Provides customization of tree node property values during tree construction.
/// </summary>
public interface IProjectTreePropertiesProvider
{
    void CalculatePropertyValues(
        IProjectTreeCustomizablePropertyContext propertyContext,
        IProjectTreeCustomizablePropertyValues propertyValues);
}

/// <summary>
/// Context passed to <see cref="IProjectTreePropertiesProvider"/> during tree build.
/// </summary>
public interface IProjectTreeCustomizablePropertyContext
{
    string? ItemName { get; }
    string? ItemType { get; }
    IImmutableDictionary<string, string> Metadata { get; }
    ProjectTreeFlags ParentNodeFlags { get; }
    bool ExistsOnDisk { get; }
    bool IsFolder { get; }
    bool IsNonFileSystemProjectItem { get; }
    IImmutableDictionary<string, string> ProjectTreeSettings { get; }
}

/// <summary>
/// Mutable property bag written to by <see cref="IProjectTreePropertiesProvider"/>.
/// </summary>
public interface IProjectTreeCustomizablePropertyValues
{
    ProjectTreeFlags Flags { get; set; }
    ProjectImageMoniker? Icon { get; set; }
    ProjectImageMoniker? ExpandedIcon { get; set; }
}
