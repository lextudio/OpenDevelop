namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The local folders that hold an SDK 11, found the way a user looks for one. The IDE deliberately
/// does not discover an SDK major newer than .NET 10 (see
/// <c>DotNetSdkService.HighestAutomaticallyDiscoveredSdkMajor</c>), so a journey that needs .NET 11
/// first registers its folder as a custom root - the same opt-in as
/// Tools &gt; Options &gt; .NET SDK &gt; Add Custom Path.
/// </summary>
static class SdkRootCandidates
{
    /// <summary>Existing DOTNET_ROOT-style folders whose sdk/ directory holds an 11.x SDK.</summary>
    public static IEnumerable<string> WithSdk11()
    {
        var roots = new List<string>();
        void Add(string? root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;
            string sdkDirectory = Path.Combine(root, "sdk");
            if (!Directory.Exists(sdkDirectory))
                return;
            if (!Directory.EnumerateDirectories(sdkDirectory)
                .Any(directory => Path.GetFileName(directory).StartsWith("11.", StringComparison.Ordinal)))
                return;
            roots.Add(Path.GetFullPath(root));
        }

        // The same candidates DOTNET_ROOT and the well-known install layouts use; the machines
        // these journeys run on are macOS only, so the Windows Program Files variants are omitted.
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
        Add("/usr/local/share/dotnet");
        Add("/opt/homebrew/opt/dotnet/libexec");

        return roots.Distinct(StringComparer.Ordinal);
    }
}
