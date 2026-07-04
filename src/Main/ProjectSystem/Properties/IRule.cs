// Clean-room stub. See docs/project-system.md.

namespace Microsoft.VisualStudio.ProjectSystem.Properties;

/// <summary>
/// Minimal stub of the CPS IRule interface (Properties window data source).
/// Only the members accessed by dotnet/project-system are declared here.
/// </summary>
public interface IRule
{
    string Name { get; }
    string? ItemType { get; }
    string? ItemName { get; }
    IRuleSchema? Schema { get; }
}

public interface IRuleSchema
{
    string Name { get; }
}
