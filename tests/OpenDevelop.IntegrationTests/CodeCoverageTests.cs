using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("40 Code coverage fixture")]
public sealed class CodeCoverageTests
{
    readonly OpenDevelopAppFixture _app;

    public CodeCoverageTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    static JsonElement? FindTest(JsonElement node, string displayName)
    {
        var name = node.TryGetProperty("displayName", out var n) ? n.GetString() : null;
        // Method-level nodes report the fully-qualified test name (e.g.
        // "SampleTestProject.PassTests.AlwaysPasses"), not the bare method name - match either.
        if (name == displayName || (name != null && name.EndsWith("." + displayName, StringComparison.Ordinal)))
            return node;
        if (node.TryGetProperty("nestedTests", out var kids))
        {
            foreach (var kid in kids.EnumerateArray())
            {
                var found = FindTest(kid, displayName);
                if (found.HasValue)
                    return found;
            }
        }
        return null;
    }

    async Task WaitForTestDiscoveryAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            if (tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue)
                return;
            await Task.Delay(1000);
        }
        throw new TimeoutException("Test methods were not discovered within 60s.");
    }

    [Fact]
    public async Task CodeCoverageService_IsAvailable()
    {
        var result = await _app.InvokeAsync("od.code-coverage.status");

        Assert.True(result.GetProperty("available").GetBoolean(),
            "CodeCoverageService should be available (CodeCoverage addin loaded)");
    }

    [Fact]
    public async Task RunWithCodeCoverage_ProducesModuleResultsAndCanBeCleared()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);
        await WaitForTestDiscoveryAsync();
        await _app.InvokeAsync("od.code-coverage.clear");

        var runResult = await _app.InvokeAsync("od.code-coverage.run", 180);
        Assert.True(runResult.GetProperty("started").GetBoolean(), runResult.ToString());
        Assert.True(runResult.GetProperty("completed").GetBoolean(),
            $"Code coverage run did not complete within timeout: {runResult}");

        var results = await _app.InvokeAsync("od.code-coverage.results");
        Assert.True(results.GetProperty("available").GetBoolean());

        var modules = results.GetProperty("modules");
        Assert.True(modules.GetArrayLength() > 0, "Expected at least one instrumented module in the coverage results.");

        // The fixture project's own assembly should show up with at least one method actually
        // exercised - AlwaysPasses/AlwaysFails/etc. run as part of this same test run.
        bool anyMethodVisited = false;
        foreach (var module in modules.EnumerateArray())
        {
            // Modules without locally-resolvable source spans can have zero computed
            // character length even though AltCover reports visited sequence/branch points.
            if (module.GetProperty("visitedCodeLength").GetInt32() > 0
                || module.GetProperty("branchCoveragePercent").GetDecimal() > 0)
            {
                anyMethodVisited = true;
                break;
            }
        }
		// A coverage run must still travel through the normal MTP result pipeline so
		// the Unit Tests pad receives ResultChanged and paints pass/fail node icons.
		var tree = await _app.InvokeAsync("od.unit-test.tree");
		var root = tree.GetProperty("tests")[0];
		var passingTest = FindTest(root, "AlwaysPasses");
		var failingTest = FindTest(root, "AlwaysFails");
		Assert.True(passingTest.HasValue, "AlwaysPasses was not present in the Unit Tests tree.");
		Assert.True(failingTest.HasValue, "AlwaysFails was not present in the Unit Tests tree.");
		Assert.Equal("Success", passingTest.Value.GetProperty("result").GetString());
		Assert.Equal("Failure", failingTest.Value.GetProperty("result").GetString());

        Assert.True(anyMethodVisited, "Expected at least one module to show non-zero visited code length.");

        var clearResult = await _app.InvokeAsync("od.code-coverage.clear");
        Assert.True(clearResult.GetProperty("success").GetBoolean());

        var afterClear = await _app.InvokeAsync("od.code-coverage.results");
        Assert.Equal(0, afterClear.GetProperty("modules").GetArrayLength());
    }

    [Fact]
    public async Task CodeCoverageRun_AddsEditorMarkersOnOpenSourceFile()
    {
        // The CoverageFixture solution pairs a test project with a *referenced library*
        // (CoverageLib). AltCover's default include filter instruments exactly the assemblies
        // produced by project references - the test project's own assembly is deliberately left
        // pristine (self-hosted runner protection), so only the library's source can carry
        // coverage sequence points.
        await _app.InvokeAsync("od.open-solution", _app.CoverageFixtureSolutionPath);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            if (tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "Add_ReturnsSum").HasValue)
                break;
            await Task.Delay(1000);
        }

        // Calculator.cs must be the active editor when results land: RunTestWithCodeCoverageCommand
        // enables the editor overlay right before ShowResults, and RefreshCodeCoverageHighlights
        // paints only views that are already open.
        var sourcePath = Path.Combine(
            Path.GetDirectoryName(_app.CoverageFixtureSolutionPath)!, "CoverageLib", "Calculator.cs");
        var openFileResult = await _app.InvokeAsync("od.open-file", sourcePath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean(), $"Failed to open {sourcePath}");

        var runResult = await _app.InvokeAsync("od.code-coverage.run", 180);
        Assert.True(runResult.GetProperty("completed").GetBoolean(), runResult.ToString());

        // The run brings the Code Coverage pad to the front, which leaves no active *view*;
        // the editor-markers action inspects the active view, so reactivate the source file.
        // The document was already open during ShowResults, so its markers were painted then
        // (and ViewOpened re-paints files opened after results exist).
        var reactivate = await _app.InvokeAsync("od.open-file", sourcePath);
        Assert.True(reactivate.GetProperty("opened").GetBoolean());

        var markers = await WaitForEditorMarkersAsync();
        Assert.Equal(sourcePath, markers.GetProperty("fileName").GetString());
        Assert.True(markers.GetProperty("markerServiceAvailable").GetBoolean());
        Assert.True(markers.GetProperty("markerCount").GetInt32() > 0,
            "Expected code-coverage text markers on the open library source after a coverage run");
        Assert.True(markers.GetProperty("coloredMarkerCount").GetInt32() > 0,
            "Expected at least one marker to be colored (visited sequence points)");
    }

    async Task<JsonElement> WaitForEditorMarkersAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement markers = default;
        while (DateTime.UtcNow < deadline)
        {
            markers = await _app.InvokeAsync("od.code-coverage.editor-markers");
            if (markers.TryGetProperty("markerCount", out var count) && count.GetInt32() > 0)
                return markers;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Code-coverage editor markers never appeared. Last status: {markers}");
    }
}
