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
