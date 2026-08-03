using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of OpenDevelop's update checker (ICSharpCode.SharpDevelop.Updates,
// src/Main/Base/Project/Src/Updates/ + the linked ILSpy AvailableVersionInfo.cs): the running
// version comes from RevisionClass, and od.update.check resolves the latest release from
// lextudio/OpenDevelop's GitHub Releases API. The check is inherently network-dependent, so the
// assertions only pin down the response shape and version-reporting - a failed/offline check
// (checkFailed: true) is as valid a pass as a successful one, as long as the app itself neither
// throws nor hangs.
//
// Prerequisites:
//   1. Build OpenDevelop in Debug:
//        dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
[Collection("OpenDevelop app")]
public sealed class UpdateTests
{
    readonly OpenDevelopAppFixture _app;

    public UpdateTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task UpdateCheck_ReportsCurrentVersionAndCheckOutcome()
    {
        var result = await _app.InvokeAsync("od.update.check");

        var currentVersion = result.GetProperty("currentVersion").GetString();
        Assert.False(string.IsNullOrEmpty(currentVersion), "Expected the running version to be reported");

        // Must be a parseable four-part version (RevisionClass shape).
        Assert.True(Version.TryParse(currentVersion, out var parsed) && parsed.Revision >= 0,
            $"Expected a Major.Minor.Build.Revision version, got '{currentVersion}'");

        if (result.TryGetProperty("checkFailed", out var failed) && failed.GetBoolean())
        {
            // Offline / GitHub rate-limited: the checker must degrade gracefully.
            Assert.True(result.TryGetProperty("error", out _));
            return;
        }

        // No published release on the repository yet (GitHub /releases/latest returns 404) is a
        // legitimate "nothing to update to" - reported as updateAvailable=false, not a failure.
        Assert.True(result.TryGetProperty("latestVersion", out var latest));
        Assert.True(result.TryGetProperty("updateAvailable", out var available));
        Assert.True(available.ValueKind is JsonValueKind.True or JsonValueKind.False);
        if (latest.ValueKind == JsonValueKind.Null)
        {
            Assert.False(available.GetBoolean());
            return;
        }
        Assert.False(string.IsNullOrEmpty(latest.GetString()));
        Assert.True(result.TryGetProperty("automaticCheckEnabled", out _));

        // A downloaded release must point back at the project's own repository.
        if (available.GetBoolean())
        {
            var url = result.GetProperty("downloadUrl").GetString();
            Assert.NotNull(url);
            Assert.Contains("lextudio/OpenDevelop", url);
        }
    }

    [Fact]
    public async Task UpdateCheck_RunningVersionMatchesAssemblyVersion()
    {
        var result = await _app.InvokeAsync("od.update.check");

        // The About/update surface must report the same version the assembly carries -
        // RevisionClass (GlobalAssemblyInfo.cs) and [AssemblyVersion] must stay in sync.
        var currentVersion = result.GetProperty("currentVersion").GetString()!;
        var assemblyVersion = result.GetProperty("assemblyVersion").GetString()!;
        Assert.Equal(assemblyVersion, currentVersion);
    }
}
