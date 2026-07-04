// Clean-room stub. See docs/project-system.md.

using System.Collections.Immutable;

namespace Microsoft.VisualStudio.ProjectSystem.Properties;

/// <summary>
/// Minimal stub of the CPS IProjectCatalogSnapshot — provides access to property page catalogs.
/// Only the members used by dotnet/project-system's dependency snapshot types are declared.
/// </summary>
public interface IProjectCatalogSnapshot
{
    IImmutableDictionary<string, IPropertyPagesCatalog> NamedCatalogs { get; }
    ConfiguredProject? Project { get; }
}

/// <summary>Minimal stub for the property pages catalog.</summary>
public interface IPropertyPagesCatalog
{
    IRule? BindToContext(string schemaName, object projectInstance, string? itemType, string? itemName);
}
