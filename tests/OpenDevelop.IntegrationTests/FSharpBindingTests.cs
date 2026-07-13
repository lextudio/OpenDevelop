using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class FSharpBindingTests
{
    readonly OpenDevelopAppFixture _app;

    public FSharpBindingTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task FSharpAddIn_IsLoaded()
    {
        var result = await _app.InvokeAsync("od.addins");

        var addins = result.GetProperty("addins").EnumerateArray().ToList();

        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("FSharpBinding.addin"));
    }

    [Fact]
    public async Task OpenFSharpSolution_LoadsFSharpFixture()
    {
        var result = await _app.InvokeAsync("od.open-solution", _app.FSharpFixtureSolutionPath);

        Assert.True(result.GetProperty("success").GetBoolean(), $"OpenSolutionOrProject returned false for {_app.FSharpFixtureSolutionPath}");
        Assert.Equal(_app.FSharpFixtureSolutionPath, result.GetProperty("currentSolution").GetString());
    }

    [Fact]
    public async Task FSharpSolutionTree_ShowsSourceFileNode()
    {
        await _app.InvokeAsync("od.open-solution", _app.FSharpFixtureSolutionPath);

        var tree = await _app.InvokeAsync("od.solution-tree");
        var project = tree.GetProperty("projects").EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString() == "FSharpFixture");
        Assert.True(project.ValueKind != JsonValueKind.Undefined, $"FSharpFixture project not found in solution tree: {tree}");

        var files = project.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains(files, f => f != null && f.EndsWith("Program.fs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenFSharpFile_DisplaysInAvalonEdit()
    {
        await _app.InvokeAsync("od.open-solution", _app.FSharpFixtureSolutionPath);
        var fsPath = Path.Combine(Path.GetDirectoryName(_app.FSharpFixtureSolutionPath)!, "Program.fs");

        var openResult = await _app.InvokeAsync("od.open-file", fsPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean(), $"Failed to open {fsPath}");

        var activeView = await _app.InvokeAsync("od.active-view");

        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.True(activeView.GetProperty("isAvalonEdit").GetBoolean(),
            $"Expected AvalonEditViewContent, got {activeView.GetProperty("typeName").GetString()}");

        var textPreview = activeView.GetProperty("textPreview").GetString();
        Assert.Contains("module Program", textPreview);
        Assert.Contains("printfn", textPreview);

        Assert.Equal("F#", activeView.GetProperty("syntaxHighlighting").GetString());
    }

    [Fact]
    public async Task FSharpBuild_CompilesFixtureProject()
    {
        await _app.InvokeAsync("od.open-solution", _app.FSharpFixtureSolutionPath);
        var preBuild = Path.Combine(Path.GetDirectoryName(_app.FSharpFixtureSolutionPath)!, "bin", "Debug", "net8.0", "FSharpFixture.dll");
        if (File.Exists(preBuild))
            File.Delete(preBuild);

        var result = await _app.InvokeAsync("od.build-solution", "FSharpFixture");

        // od.build-solution's JSON only has an "error" property for the early-exit cases (no
        // solution open / project not found) - once a build actually runs, "success" is always
        // true (the DevFlow call itself didn't throw) and the real pass/fail signal is "result".
        Assert.Equal("Success", result.GetProperty("result").GetString());
    }
}
