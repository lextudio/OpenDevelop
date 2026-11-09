#nullable enable
namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed record NuGetPackageUpdateResult(
		string Id,
		string CurrentVersion,
		string LatestVersion,
		string SourceName,
		bool RequireLicenseAcceptance = false,
		string? LicenseUrl = null,
		global::NuGet.Protocol.Core.Types.IPackageSearchMetadata? Metadata = null);
}
