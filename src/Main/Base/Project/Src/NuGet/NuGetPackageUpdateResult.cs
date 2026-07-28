namespace ICSharpCode.SharpDevelop.NuGet
{
	public sealed record NuGetPackageUpdateResult(
		string Id,
		string CurrentVersion,
		string LatestVersion,
		string SourceName);
}
