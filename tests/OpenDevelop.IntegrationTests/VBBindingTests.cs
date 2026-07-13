using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of the VB.NET Roslyn language service added in doc/technotes/roslyn.md:
// the addin loads, the file opens/renders in AvalonEdit, the project still builds via vbc, and
// SD.ParserService actually has a real (RoslynParser-backed) ICompilation for the .vb file - the
// concrete acceptance bar that doc calls for.
[Collection("OpenDevelop app")]
public sealed class VBBindingTests
{
    readonly OpenDevelopAppFixture _app;

    public VBBindingTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task VBAddIn_IsLoaded()
    {
        var result = await _app.InvokeAsync("od.addins");

        var addins = result.GetProperty("addins").EnumerateArray().ToList();

        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("VBBinding.addin"));
    }

    [Fact]
    public async Task OpenVBSolution_LoadsVBFixture()
    {
        var result = await _app.InvokeAsync("od.open-solution", _app.VBFixtureSolutionPath);

        Assert.True(result.GetProperty("success").GetBoolean(), $"OpenSolutionOrProject returned false for {_app.VBFixtureSolutionPath}");
        Assert.Equal(_app.VBFixtureSolutionPath, result.GetProperty("currentSolution").GetString());
    }

    [Fact]
    public async Task VBSolutionTree_ShowsSourceFileNode()
    {
        await _app.InvokeAsync("od.open-solution", _app.VBFixtureSolutionPath);

        var tree = await _app.InvokeAsync("od.solution-tree");
        var project = tree.GetProperty("projects").EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString() == "VBFixture");
        Assert.True(project.ValueKind != JsonValueKind.Undefined, $"VBFixture project not found in solution tree: {tree}");

        var files = project.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains(files, f => f != null && f.EndsWith("Class1.vb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenVBFile_DisplaysInAvalonEditAndParses()
    {
        await _app.InvokeAsync("od.open-solution", _app.VBFixtureSolutionPath);
        var vbPath = Path.Combine(Path.GetDirectoryName(_app.VBFixtureSolutionPath)!, "Class1.vb");

        var openResult = await _app.InvokeAsync("od.open-file", vbPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean(), $"Failed to open {vbPath}");

        var activeView = await _app.InvokeAsync("od.active-view");

        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.True(activeView.GetProperty("isAvalonEdit").GetBoolean(),
            $"Expected AvalonEditViewContent, got {activeView.GetProperty("typeName").GetString()}");

        var textPreview = activeView.GetProperty("textPreview").GetString();
        Assert.Contains("Public Class Class1", textPreview);
        Assert.Contains("AddNumbers", textPreview);

        Assert.Equal("VB", activeView.GetProperty("syntaxHighlighting").GetString());

        // The actual point of this session's work: RoslynWorkspaceHelper (the integration point
        // GoToDefinition/completion/etc. actually use) has a real Roslyn Document for this .vb
        // file, in the VisualBasic language - not the previous "no language service for VB at
        // all" state.
        var parserStatus = await _app.InvokeAsync("od.parser.status", vbPath);
        Assert.True(parserStatus.GetProperty("hasDocument").GetBoolean(),
            $"Expected a real Roslyn Document for {vbPath}: {parserStatus}");
        Assert.Equal("Visual Basic", parserStatus.GetProperty("language").GetString());
    }

    [Fact]
    public async Task VBBuild_CompilesFixtureProject()
    {
        await _app.InvokeAsync("od.open-solution", _app.VBFixtureSolutionPath);
        var preBuild = Path.Combine(Path.GetDirectoryName(_app.VBFixtureSolutionPath)!, "bin", "Debug", "net10.0", "VBFixture.dll");
        if (File.Exists(preBuild))
            File.Delete(preBuild);

        var result = await _app.InvokeAsync("od.build-solution", "VBFixture");

        Assert.Equal("Success", result.GetProperty("result").GetString());
    }
}
