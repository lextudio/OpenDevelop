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

    // The Project Browser pad renders its own tree in the real WPF visual tree (ProjectBrowserView.xaml's
    // HierarchicalDataTemplate -> TextBlock per node). od.solution-tree covers the backing model; this
    // locks in that opening a plain (non-git) solution actually displays the project, root file and
    // folder nodes as visible UI. Folder nodes render even when collapsed; files nested under a folder
    // (Widget.cs under Models/) are only realized once the folder is expanded, which has no DevFlow hook.
    [Fact]
    public async Task OpenSolution_ProjectBrowserPadRendersRealNodes()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        // The pad's TreeView content is only realized by AvalonDock once the pad is shown/activated
        // (same pattern as GitAddInTests).
        var showPadResult = await _app.InvokeAsync("od.show-pad", "ProjectBrowserPad");
        Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the ProjectBrowser pad");

        var tree = await _app.GetUITreeAsync();
        var texts = FlattenElements(tree)
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
            .Select(e => e.GetProperty("text").GetString())
            .ToList();

        Assert.Contains("SampleApp", texts);
        Assert.Contains("Program.cs", texts);
        Assert.Contains("Models", texts);
        Assert.Contains("Services", texts);
    }

    static IEnumerable<JsonElement> FlattenElements(JsonElement tree)
    {
        foreach (var root in tree.GetProperty("elements").EnumerateArray())
            foreach (var node in Flatten(root))
                yield return node;
    }

    static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Flatten(child))
                    yield return descendant;
    }
}
