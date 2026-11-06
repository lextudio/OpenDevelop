using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Issue #23: without a usable .NET SDK the IDE used to start and then fail the first project load
/// with MSBuild's "The SDK 'Microsoft.NET.Sdk' specified could not be found" and a stack. A .NET 10
/// SDK is a prerequisite, so the IDE must say so when it starts and leave instead.
///
/// <c>OD_IGNORE_DOTNET_SDKS</c> makes discovery return nothing, so this journey runs on a machine
/// that does have SDK 10 - the real state is otherwise only reachable by uninstalling it.
/// </summary>
[Collection("90 SDK selected at startup (isolated IDE)")]
public sealed class MissingSdkStartupIntegrationTests
{
    [Fact]
    public async Task Ide_ReportsTheMissingSdkAndExits_WhenNoSdkIsDiscovered()
    {
        string work = Path.Combine(Path.GetTempPath(), "opendevelop-no-sdk-" + Guid.NewGuid().ToString("N"));
        string configDir = Path.Combine(work, "config");
        Directory.CreateDirectory(configDir);
        try
        {
            var environment = new Dictionary<string, string> { ["OD_IGNORE_DOTNET_SDKS"] = "1" };
            var (exitCode, output) = await IsolatedIde.RunToExitAsync(configDir, environment, TimeSpan.FromMinutes(3));

            Assert.NotEqual(0, exitCode);
            // The dialog cannot be shown under OD_TEST_MODE, so the log is the record: it carries the
            // same text the Build pad and the dialog show.
            Assert.Contains("No usable .NET SDK was found for this process", output);
            Assert.Contains(".NET SDK 10 or newer: NOT FOUND", output);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* a lingering handle; the temp folder is swept eventually */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
