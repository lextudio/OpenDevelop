namespace ICSharpCode.SharpDevelop.NuGet
{
    /// <summary>
    /// Presentation-shaped search hit — decoupled from <c>NuGet.Protocol</c>'s
    /// <c>IPackageSearchMetadata</c> the same way <c>LanguageServiceProjectSnapshot</c> decouples
    /// from Roslyn types, so callers (and tests) don't need a live network search to construct one.
    /// </summary>
    public sealed record NuGetSearchResult(
        string Id,
        string Version,
        string? Description,
        long? DownloadCount,
        string? IconUrl,
        string SourceName,
        bool RequireLicenseAcceptance = false,
        string? LicenseUrl = null);
}
