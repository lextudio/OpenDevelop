using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class SlnxLoadingTests
{
    readonly OpenDevelopAppFixture _app;

    public SlnxLoadingTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task OpenSlnx_LoadsSolutionExplorerFixture()
    {
        var result = await _app.InvokeAsync("od.open-solution", _app.SlnxFixturePath);

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(_app.SlnxFixturePath, result.GetProperty("currentSolution").GetString());
    }

    [Fact]
    public async Task SolutionTree_ListsAllProjects()
    {
        await _app.InvokeAsync("od.open-solution", _app.SlnxFixturePath);

        var tree = await _app.InvokeAsync("od.solution-tree");

        Assert.Equal(_app.SlnxFixturePath, tree.GetProperty("solutionFile").GetString());

        var projects = tree.GetProperty("projects").EnumerateArray().ToList();
        Assert.Equal(2, projects.Count);

        var lib = projects.Single(p => p.GetProperty("name").GetString() == "Lib");
        var appProj = projects.Single(p => p.GetProperty("name").GetString() == "App");

        var libFiles = lib.GetProperty("files").EnumerateArray().Select(f => f.GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(libFiles, f => f.EndsWith("Class1.cs"));

        var appFiles = appProj.GetProperty("files").EnumerateArray().Select(f => f.GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(appFiles, f => f.EndsWith("Program.cs"));
        Assert.Contains(appFiles, f => f.EndsWith("Utils/Helper.cs"));
    }

    [Fact]
    public async Task OpenSlnxFile_DisplaysInAvalonEdit()
    {
        await _app.InvokeAsync("od.open-solution", _app.SlnxFixturePath);

        var programPath = Path.Combine(Path.GetDirectoryName(_app.SlnxFixturePath)!, "App", "Program.cs");
        var openResult = await _app.InvokeAsync("od.open-file", programPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean());

        var activeView = await _app.InvokeAsync("od.active-view");
        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.True(activeView.GetProperty("isAvalonEdit").GetBoolean());

        var textPreview = activeView.GetProperty("textPreview").GetString();
        Assert.Contains("class Program", textPreview);
    }
}
