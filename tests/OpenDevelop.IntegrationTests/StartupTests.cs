using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// Empty-startup behavior can only be asserted against a *fresh* app instance: in the shared
// "OpenDevelop app" collection a previous test has already opened a solution, so the active view
// is a document, not the Start Page. This collection gets its own OpenDevelopAppFixture (a
// second, freshly launched app process on the same DevFlow port - safe because the whole test
// assembly runs with parallelization disabled, so collections execute one after another and the
// previous fixture's DisposeAsync has already killed its app).
[Collection("OpenDevelop startup")]
public sealed class StartupTests
{
    readonly OpenDevelopAppFixture _app;

    public StartupTests(OpenDevelopAppFixture app)
    {
        _app = app;
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
}

[CollectionDefinition("OpenDevelop startup")]
public sealed class OpenDevelopStartupCollection : ICollectionFixture<OpenDevelopAppFixture> { }
