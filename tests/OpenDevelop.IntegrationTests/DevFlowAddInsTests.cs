using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class DevFlowAddInsTests
{
    readonly OpenDevelopAppFixture _app;

    public DevFlowAddInsTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task AddInsList_ContainsSharpDevelopAddIns()
    {
        var result = await _app.InvokeAsync("od.addins");

        var addins = result.GetProperty("addins").EnumerateArray().ToList();

        // "name" is the AddIn's display Name attribute (e.g. "SharpDevelop"), not its manifest
        // Identity/file name, so match on fileName instead of assuming "name" carries the
        // "ICSharpCode.SharpDevelop" identity string.
        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("ICSharpCode.SharpDevelop.addin"));
    }

    [Fact]
    public async Task EmptyStartup_LoadsStartPageAddInAndShowsStartPage()
    {
        var addInsResult = await _app.InvokeAsync("od.addins");
        var addins = addInsResult.GetProperty("addins").EnumerateArray().ToList();

        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("StartPage.addin"));

        var activeView = await _app.InvokeAsync("od.active-view");

        Assert.True(activeView.GetProperty("active").GetBoolean(), "Expected an active Start Page view.");
        Assert.Equal("ICSharpCode.StartPage.StartPageViewContent", activeView.GetProperty("typeName").GetString());
    }

    [Fact]
    public async Task UnitTestsPad_DefaultsVisibleInLeftPane()
    {
        var result = await _app.InvokeAsync("od.pads");
        var pads = result.EnumerateArray().ToList();

        var testsPad = Assert.Single(pads, p =>
            p.GetProperty("className").GetString() == "ICSharpCode.UnitTesting.UnitTestsPad");

        Assert.Equal("Left", testsPad.GetProperty("defaultPosition").GetString());
        Assert.Equal("Unit Tests", testsPad.GetProperty("title").GetString());
    }
}
