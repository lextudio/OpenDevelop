// Clean-room stub. See docs/project-system.md.

namespace Microsoft.VisualStudio.ProjectSystem.Properties;

/// <summary>
/// Minimal stub — identifies a project item for the Properties window.
/// </summary>
public interface IProjectPropertiesContext
{
    bool IsProjectFile { get; }
    string File { get; }
    string? ItemType { get; }
    string? ItemName { get; }
}
