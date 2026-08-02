using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of the .NET SDK discovery/selection surface exposed to the DevFlow agent
// (od.sdk.list / od.sdk.select -> DotNetSdkService): the app must discover the installed SDKs,
// resolve an effective (currently selected or system-default) one, and honor an explicit
// selection. Selection is a process-global setting, so the round-trip test always restores the
// system default before finishing - later tests in this shared collection (builds, etc.) rely on
// the effective SDK being the default.
//
// Prerequisites:
//   1. Build OpenDevelop in Debug:
//        dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
[Collection("OpenDevelop app")]
public sealed class SdkTests
{
    readonly OpenDevelopAppFixture _app;

    public SdkTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task SdkList_ReturnsDiscoveredSdksAndEffectiveSdk()
    {
        var result = await _app.InvokeAsync("od.sdk.list");

        // The DevFlow action serializes the anonymous type with its implicit PascalCase member
        // names (Label/RootPath/HighestSdkVersion), only "origin" and "selectedRootPath" are
        // explicitly cased.
        var effective = result.GetProperty("effective");
        Assert.False(string.IsNullOrEmpty(effective.GetProperty("Label").GetString()),
            "Expected an effective SDK label to be resolved");
        Assert.False(string.IsNullOrEmpty(effective.GetProperty("RootPath").GetString()),
            "Expected the effective SDK to have a root path");
        Assert.False(string.IsNullOrEmpty(effective.GetProperty("HighestSdkVersion").GetString()),
            "Expected the effective SDK to report its highest version");

        var sdks = result.GetProperty("sdks").EnumerateArray().ToList();
        Assert.True(sdks.Count > 0, "Expected at least one discovered .NET SDK");
        Assert.Contains(sdks, s => s.GetProperty("RootPath").GetString() == effective.GetProperty("RootPath").GetString());
    }

    [Fact]
    public async Task SdkSelect_RoundTripsBetweenExplicitSdkAndSystemDefault()
    {
        var list = await _app.InvokeAsync("od.sdk.list");
        var sdks = list.GetProperty("sdks").EnumerateArray().ToList();
        Assert.True(sdks.Count > 0, "Expected at least one discovered .NET SDK");
        var target = sdks[0].GetProperty("RootPath").GetString()!;

        try
        {
            var selected = await _app.InvokeAsync("od.sdk.select", target);
            Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
            Assert.Equal(target, selected.GetProperty("effective").GetProperty("RootPath").GetString());
        }
        finally
        {
            // Restore the system default so the shared app instance keeps behaving normally
            // for every later test in this collection.
            var restored = await _app.InvokeAsync("od.sdk.select", "");
            Assert.True(restored.GetProperty("success").GetBoolean(), restored.ToString());

            var after = await _app.InvokeAsync("od.sdk.list");
            var effectiveRoot = after.GetProperty("effective").GetProperty("RootPath").GetString();
            Assert.False(string.IsNullOrEmpty(effectiveRoot));
        }
    }
}
