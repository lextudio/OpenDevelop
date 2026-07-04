// Clean-room stub. ProjectCapabilities is a CPS SDK type.
// Only the constants referenced by dotnet/project-system's ProjectCapability.cs are included.
// Reconstructed from MIT usage + public MSBuild/CPS documentation.

namespace Microsoft.VisualStudio.ProjectSystem;

/// <summary>Well-known CPS project capability string constants.</summary>
public static class ProjectCapabilities
{
    public const string CSharp = "CSharp";
    public const string VB = "VB";
    public const string HandlesOwnReload = "HandlesOwnReload";
    public const string PackageReferences = "PackageReferences";
    public const string ProjectReferences = "ProjectReferences";
    public const string AssemblyReferences = "AssemblyReferences";
    public const string WinRTReferences = "WinRTReferences";
    public const string SdkReferences = "SdkReferences";
    public const string ComReferences = "ComReferences";
    public const string ProjectConfigurationsDeclaredDimensions = "ProjectConfigurationsDeclaredDimensions";
    public const string UseProjectEvaluationCache = "UseProjectEvaluationCache";
    public const string SortByDisplayOrder = "SortByDisplayOrder";
    public const string SharedAssetsProject = "SharedAssetsProject";
}
