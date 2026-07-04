// Clean-room constants for dependency item browse-object rules.
// See docs/project-system.md.

namespace Microsoft.VisualStudio.ProjectSystem;

public static class DependencyRuleNames
{
    public const string Reference = "Reference";
    public const string ProjectReference = "ProjectReference";
    public const string PackageReference = "PackageReference";
    public const string Analyzer = "Analyzer";
    public const string ComReference = "COMReference";
    public const string FrameworkReference = "FrameworkReference";
    public const string SdkReference = "SDKReference";
}
