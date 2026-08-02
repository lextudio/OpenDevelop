using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// Coverage for the plain-text Find/Replace engine (SearchManager, driven via the new od.search.*
// actions in SearchAndReplaceDevFlowActions.cs) - distinct from the Roslyn-based symbol
// find-references/rename already covered by RoslynRefactoringTests.cs. Previously zero coverage
// (no "find"/"replace" references anywhere in this test suite).
[Collection("OpenDevelop app")]
public sealed class SearchAndReplaceTests
{
    readonly OpenDevelopAppFixture _app;

    public SearchAndReplaceTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    string SampleAppDirectory => Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp");

    [Fact]
    public async Task Find_InSolution_FindsTermAcrossMultipleFiles()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        // "Widget" appears in both Models/Widget.cs (class declaration) and Services/WidgetService.cs
        // (usage) in the real fixture files - a genuine cross-file plain-text match, not a rename.
        var result = await _app.InvokeAsync("od.search.find", "Widget", "solution");

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.True(result.GetProperty("matchCount").GetInt32() > 1);
        Assert.True(result.GetProperty("fileCount").GetInt32() >= 2);

        var files = result.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("file").GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(files, f => f.EndsWith("Models/Widget.cs"));
        Assert.Contains(files, f => f.EndsWith("Services/WidgetService.cs"));

        var widgetFileMatches = result.GetProperty("files").EnumerateArray()
            .First(f => f.GetProperty("file").GetString()!.Replace('\\', '/').EndsWith("Models/Widget.cs"))
            .GetProperty("matches").EnumerateArray().ToList();
        Assert.NotEmpty(widgetFileMatches);
        Assert.True(widgetFileMatches[0].GetProperty("line").GetInt32() > 0);
    }

    [Fact]
    public async Task Find_MatchCase_RespectsCaseSensitivity()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        var caseInsensitive = await _app.InvokeAsync("od.search.find", "WIDGET", "solution", false, false, false);
        Assert.True(caseInsensitive.GetProperty("matchCount").GetInt32() > 0);

        var caseSensitive = await _app.InvokeAsync("od.search.find", "WIDGET", "solution", true, false, false);
        Assert.Equal(0, caseSensitive.GetProperty("matchCount").GetInt32());
    }

    [Fact]
    public async Task Find_UseRegex_MatchesPattern()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        var result = await _app.InvokeAsync("od.search.find", @"Widget\w*", "solution", false, false, true);

        Assert.True(result.GetProperty("success").GetBoolean());
        var files = result.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("file").GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(files, f => f.EndsWith("Services/WidgetService.cs"));
    }

    [Fact]
    public async Task ShowResults_PopulatesSearchResultsPadUiTree()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

        // od.search.find is headless; od.search.show-results goes through the same real
        // SearchManager.FindAllParallel engine but also feeds SearchManager.ShowSearchResults ->
        // SearchResultsPad, which the Find-in-Files dialog path never does. The pad's ResultsTreeView
        // renders each SearchNode's Text via a ContentPresenter - the TextBlocks are built from
        // Inlines (which TextBlock.Text does not expose), so the nodes carry AutomationIds
        // (SearchRootNode/SearchFileNode/SearchResultNode, see the Gui/SearchNode*.cs CreateText
        // methods) for the UI tree to identify. Poll for the async results to arrive.
        var showResult = await _app.InvokeAsync("od.search.show-results", "Widget", "solution");
        Assert.True(showResult.GetProperty("success").GetBoolean());

        // The pad is registered with defaultPosition "Bottom, Hidden" (auto-hide), whose content is
        // not realized until the pad is actually activated - the same pattern as the other pad tests.
        var showPadResult = await _app.InvokeAsync("od.show-pad", "Search Results");
        Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the Search Results pad");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement tree = default;
        List<JsonElement> elements = new();
        while (DateTime.UtcNow < deadline)
        {
            tree = await _app.GetUITreeAsync();
            elements = FlattenElements(tree).ToList();
            if (elements.Count(e =>
                e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchResultNode"
                && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()) >= 2)
                break;
            await Task.Delay(500);
        }

        Assert.True(elements.Any(e =>
            e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchRootNode"
            && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()),
            "Expected the Search Results pad root node to be rendered and visible");

        // The default grouping is Flat (no per-file nodes); the match rows themselves are the
        // rendered content. "Widget" matches in both Models/Widget.cs and Services/WidgetService.cs
        // (see Find_InSolution_FindsTermAcrossMultipleFiles), so at least two real match rows must
        // be visible.
        Assert.True(elements.Count(e =>
            e.TryGetProperty("automationId", out var a) && a.GetString() == "SearchResultNode"
            && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()) >= 2,
            "Expected at least two match nodes to be rendered and visible");
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

    [Fact]
    public async Task Replace_InOpenFile_UpdatesEditorButNotDiskUntilSaved()
    {
        var scratchPath = Path.Combine(SampleAppDirectory, "ScratchReplaceTarget.cs");
        try
        {
            File.WriteAllText(scratchPath, "namespace SampleApp { class ScratchReplaceTarget { string Value = \"NeedleValue\"; } }");

            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.open-file", scratchPath);

            var replaceResult = await _app.InvokeAsync("od.search.replace", "NeedleValue", "ReplacedValue", "current-document");
            Assert.True(replaceResult.GetProperty("success").GetBoolean());
            Assert.Equal(1, replaceResult.GetProperty("replacedCount").GetInt32());

            // Replaced in the open editor buffer...
            var activeView = await _app.InvokeAsync("od.active-view");
            Assert.Contains("ReplacedValue", activeView.GetProperty("textPreview").GetString());

            var dirtyStatus = await _app.InvokeAsync("od.file.is-dirty", scratchPath);
            Assert.True(dirtyStatus.GetProperty("isDirty").GetBoolean());

            // ...but SearchManager.ReplaceAll only edits the in-memory document - disk is untouched
            // until an explicit save.
            Assert.Contains("NeedleValue", File.ReadAllText(scratchPath));

            await _app.InvokeAsync("od.file.save", scratchPath);
            Assert.Contains("ReplacedValue", File.ReadAllText(scratchPath));
            Assert.DoesNotContain("NeedleValue", File.ReadAllText(scratchPath));
        }
        finally
        {
            TryDelete(scratchPath);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
