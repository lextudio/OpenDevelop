using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
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
    public async Task RunWithCodeCoverage_ProducesModuleResults()
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
    }

    [Fact]
    public async Task ClearCodeCoverageResults_EmptiesResults()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);
        await WaitForTestDiscoveryAsync();

        var runResult = await _app.InvokeAsync("od.code-coverage.run", 180);
        Assert.True(runResult.GetProperty("completed").GetBoolean(), runResult.ToString());

        var beforeClear = await _app.InvokeAsync("od.code-coverage.results");
        Assert.True(beforeClear.GetProperty("modules").GetArrayLength() > 0);

        var clearResult = await _app.InvokeAsync("od.code-coverage.clear");
        Assert.True(clearResult.GetProperty("success").GetBoolean());

        var afterClear = await _app.InvokeAsync("od.code-coverage.results");
        Assert.Equal(0, afterClear.GetProperty("modules").GetArrayLength());
    }
}
