// Clean-room stub reconstructed from MIT dotnet/project-system usage.
// ReferencesProjectTreeCustomizablePropertyValues is a CPS SDK type from
// Microsoft.VisualStudio.ProjectSystem.References — reconstructed as a data bag.

namespace Microsoft.VisualStudio.ProjectSystem.References;

/// <summary>
/// Mutable property bag for customising the "Dependencies" / "References" tree root node.
/// Implements IProjectTreeCustomizablePropertyValues so that IProjectTreePropertiesProvider
/// implementations can update Caption, Icon, ExpandedIcon, and Flags.
/// </summary>
public sealed class ReferencesProjectTreeCustomizablePropertyValues
    : IProjectTreeCustomizablePropertyValues
{
    /// <summary>MEF contract name used with [ImportMany] to collect tree property providers.</summary>
    public const string ContractName = "ReferencesProjectTreeCustomizablePropertyValues";

    public string Caption { get; set; } = string.Empty;
    public ProjectImageMoniker Icon { get; set; }
    public ProjectImageMoniker ExpandedIcon { get; set; }
    public ProjectTreeFlags Flags { get; set; }

    // Explicit interface implementation to bridge non-nullable concrete properties to nullable interface members
    ProjectImageMoniker? IProjectTreeCustomizablePropertyValues.Icon
    {
        get => Icon;
        set => Icon = value ?? default;
    }

    ProjectImageMoniker? IProjectTreeCustomizablePropertyValues.ExpandedIcon
    {
        get => ExpandedIcon;
        set => ExpandedIcon = value ?? default;
    }
}
