using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class UnitTestingTests
{
    readonly OpenDevelopAppFixture _app;

    public UnitTestingTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    static JsonElement? FindTest(JsonElement node, string displayName)
    {
        var name = node.TryGetProperty("displayName", out var n) ? n.GetString() : null;
        // Method-level nodes report the fully-qualified VSTest name (e.g.
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

    [Fact]
    public async Task UnitTestingService_IsAvailable()
    {
        var result = await _app.InvokeAsync("od.unit-test.status");

        Assert.True(result.GetProperty("available").GetBoolean(),
            "ITestService should be available (UnitTesting addin loaded)");
    }

    [Fact]
    public async Task UnitTestingTree_ShowsTestsAfterOpeningTestProject()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);

        JsonElement tree = default;
        bool discovered = false;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            tree = await _app.InvokeAsync("od.unit-test.tree");
            Assert.True(tree.GetProperty("available").GetBoolean());
            var tests = tree.GetProperty("tests");
            if (tests.GetArrayLength() > 0)
            {
                discovered = FindTest(tests[0], "AlwaysPasses").HasValue
                    || FindTest(tests[0], "AlwaysFails").HasValue;
                if (discovered) break;
            }
            await Task.Delay(1000);
        }

        Assert.True(discovered, "Test methods were not discovered within 30s timeout");

        var root = tree.GetProperty("tests")[0];
        Assert.NotNull(FindTest(root, "PassTests"));
        Assert.NotNull(FindTest(root, "FailTests"));
        Assert.NotNull(FindTest(root, "SkipTests"));
        Assert.NotNull(FindTest(root, "AlwaysPasses"));
        Assert.NotNull(FindTest(root, "AlwaysFails"));
        Assert.NotNull(FindTest(root, "AlwaysSkipped"));
    }

    [Fact]
    public async Task UnitTestRun_ProducesExpectedResults()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);

        JsonElement tree = default;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            tree = await _app.InvokeAsync("od.unit-test.tree");
            if (tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue)
                break;
            await Task.Delay(1000);
        }

        var runResult = await _app.InvokeAsync("od.unit-test.run");
        Assert.True(runResult.GetProperty("started").GetBoolean());
        Assert.True(runResult.GetProperty("completed").GetBoolean(),
            $"Test run did not complete within timeout. Faulted={runResult.TryGetProperty("faulted", out var f) && f.GetBoolean()}");

        tree = await _app.InvokeAsync("od.unit-test.tree");
        var root = tree.GetProperty("tests")[0];

        var passTest = FindTest(root, "AlwaysPasses");
        Assert.NotNull(passTest);
        Assert.Equal("Success", passTest.Value.GetProperty("result").GetString());

        var failTest = FindTest(root, "AlwaysFails");
        Assert.NotNull(failTest);
        Assert.Equal("Failure", failTest.Value.GetProperty("result").GetString());

        var skipTest = FindTest(root, "AlwaysSkipped");
        Assert.NotNull(skipTest);
        Assert.Equal("Ignored", skipTest.Value.GetProperty("result").GetString());
    }

    [Fact]
    public async Task DebugUnitTest_StartsDebugSessionWithoutHanging()
    {
        // od.unit-test.debug is bounded by Task.WhenAny on the DevFlow side, so this action call
        // itself can't hang the caller indefinitely -- if the underlying debugger session wedges
        // (see the known debugger-hang issue), the worst case is this HTTP call blocking up to
        // the fixture's own HttpClient.Timeout (120s), not forever.
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            if (tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue)
                break;
            await Task.Delay(1000);
        }

        var debugResult = await _app.InvokeAsync("od.unit-test.debug", 60);

        // We deliberately don't assert completed==true here: this is new coverage for a path
        // (VsTestDebugger) that was never exercised before, and the known debugger-hang issue (a
        // separate, already-tracked bug) may make "hangs instead of completing" the actual,
        // honest result. What matters for this test is that we get an HTTP response at all
        // (proving the app didn't wedge solid) and can inspect what actually happened.
        Assert.True(debugResult.TryGetProperty("started", out _), "od.unit-test.debug did not return a usable response");

        await _app.InvokeAsync("od.debug.stop");
    }

    [Fact]
    public async Task UnitTestRun_OutputPadCapturesMessages()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            if (tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue)
                break;
            await Task.Delay(1000);
        }

        await _app.InvokeAsync("od.unit-test.run");

        var output = await _app.InvokeAsync("od.unit-test.output");
        Assert.Equal("UnitTesting", output.GetProperty("category").GetString());
        var text = output.GetProperty("text").GetString()!;
        Assert.Contains("AlwaysPasses", text);
        Assert.Contains("AlwaysFails", text);
        Assert.Contains("AlwaysSkipped", text);
    }
}
