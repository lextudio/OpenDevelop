using System.Text.Json;
using Xunit;

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
    /// <summary>
    /// Selects <paramref name="root"/> as the SDK builds use (Tools &gt; Options &gt; .NET SDK) and checks that it became the
    /// effective one. od.sdk.select answers success even when the selection is unusable, so only the effective root
    /// shows whether it took; a journey whose global.json pins an SDK 11 fails with a confusing "SDK not found" otherwise.
    /// </summary>
    public static async Task SelectAsync(IsolatedIde ide, string root)
    {
        var selected = await ide.InvokeAsync("od.sdk.select", root);
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
        Assert.True(selected.TryGetProperty("effective", out var effective) && effective.ValueKind == JsonValueKind.Object,
            "The selected SDK root is not usable: " + selected);
        Assert.Equal(root.TrimEnd('/', '\\'), effective.GetProperty("RootPath").GetString()!.TrimEnd('/', '\\'), ignoreCase: true);
    }

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

        // The same candidates DOTNET_ROOT and the well-known install layouts use. The device lanes put an
        // SDK 11 into ~/.dotnet without touching PATH, so the journey and the SDK the job runs on never clash.
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
        Add("/usr/local/share/dotnet");
        Add("/usr/share/dotnet");
        Add("/opt/homebrew/opt/dotnet/libexec");

        return roots.Distinct(StringComparer.Ordinal);
    }
}
