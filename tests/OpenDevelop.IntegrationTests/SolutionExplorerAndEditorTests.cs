using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of the flow this session's AvalonEdit.AddIn/Roslyn work was meant to fix:
// open a solution, confirm Solution Explorer sees its real project/file structure, open a .cs
// file from it, and confirm AvalonEdit actually rendered the file's content (not a crash, not a
// blank view). Drives the app via the od.* DevFlow actions (OpenDevelopDevFlowActions.cs) since
// the native Open-file dialog isn't reachable from the WPF-embedded DevFlow agent.
[Collection("OpenDevelop app")]
public sealed class SolutionExplorerAndEditorTests
{
    readonly OpenDevelopAppFixture _app;

    public SolutionExplorerAndEditorTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task OpenSolution_LoadsSolutionExplorerFixture()
    {
        var result = await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        Assert.True(result.GetProperty("success").GetBoolean(), $"OpenSolutionOrProject returned false for {_app.SolutionExplorerFixturePath}");
        Assert.Equal(_app.SolutionExplorerFixturePath, result.GetProperty("currentSolution").GetString());
    }

    [Fact]
    public async Task SolutionTree_MatchesFixtureProjectStructure()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        var tree = await _app.InvokeAsync("od.solution-tree");

        Assert.Equal(_app.SolutionExplorerFixturePath, tree.GetProperty("solutionFile").GetString());

        var projects = tree.GetProperty("projects").EnumerateArray().ToList();
        Assert.Single(projects);

        var sampleApp = projects[0];
        Assert.Equal("SampleApp", sampleApp.GetProperty("name").GetString());

        var files = sampleApp.GetProperty("files").EnumerateArray()
            .Select(f => f.GetString())
            .Select(f => f!.Replace('\\', '/'))
            .ToList();

        Assert.Contains(files, f => f.EndsWith("Program.cs"));
        Assert.Contains(files, f => f.EndsWith("Models/Widget.cs"));
        Assert.Contains(files, f => f.EndsWith("Services/WidgetService.cs"));
    }

    [Fact]
    public async Task OpenFile_DisplaysInAvalonEdit()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
        var widgetPath = Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp", "Models", "Widget.cs");

        var openResult = await _app.InvokeAsync("od.open-file", widgetPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean(), $"Failed to open {widgetPath}");

        var activeView = await _app.InvokeAsync("od.active-view");

        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.True(activeView.GetProperty("isAvalonEdit").GetBoolean(),
            $"Expected AvalonEditViewContent, got {activeView.GetProperty("typeName").GetString()}");

        var fileName = activeView.GetProperty("fileName").GetString()!.Replace('\\', '/');
        Assert.EndsWith("Models/Widget.cs", fileName);

        // Confirm AvalonEdit actually loaded the real file content, not a blank/error view
        // (this is exactly the crash this session's fixes were about: AutoDetectDisplayBinding
        // throwing when no display binding was found, and the null FormattingStrategy crash).
        var textPreview = activeView.GetProperty("textPreview").GetString();
        Assert.Contains("class Widget", textPreview);
        Assert.Contains("namespace SampleApp.Models", textPreview);
    }
}
