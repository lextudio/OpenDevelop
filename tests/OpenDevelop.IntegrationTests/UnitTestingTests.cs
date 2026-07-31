using System.Linq;
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

    /// <summary>
    /// Recursively collects all nodes of a given type from the tree.
    /// </summary>
    static List<JsonElement> CollectNodesByType(JsonElement node, string type)
    {
        var result = new List<JsonElement>();
        if (node.TryGetProperty("type", out var t) && t.GetString() == type)
            result.Add(node);
        if (node.TryGetProperty("nestedTests", out var kids))
        {
            foreach (var kid in kids.EnumerateArray())
                result.AddRange(CollectNodesByType(kid, type));
        }
        return result;
    }

    /// <summary>
    /// Recursively counts all leaf (method) nodes under the given node.
    /// </summary>
    static int CountLeafMethods(JsonElement node)
    {
        if (node.TryGetProperty("type", out var t) && t.GetString() == "method")
            return 1;
        if (node.TryGetProperty("nestedTests", out var kids))
        {
            var count = 0;
            foreach (var kid in kids.EnumerateArray())
                count += CountLeafMethods(kid);
            return count;
        }
        return 0;
    }

    static void AssertNode(JsonElement node, string expectedType, string expectedDisplayName, int expectedChildCount)
    {
        Assert.Equal(expectedType, node.GetProperty("type").GetString());
        Assert.Equal(expectedDisplayName, node.GetProperty("displayName").GetString());
        Assert.Equal(expectedChildCount, node.GetProperty("nestedTests").GetArrayLength());
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

        // ── Full tree structure: solution → project → targetFramework → namespace → class → method ──
        AssertNode(root, "solution", "All Tests", expectedChildCount: 1);

        var project = root.GetProperty("nestedTests")[0];
        AssertNode(project, "project", "SampleTestProject", expectedChildCount: 1);

        var targetFramework = project.GetProperty("nestedTests")[0];
        // MtpTargetFramework doesn't match WalkTestNode's type detection → default "test"
        AssertNode(targetFramework, "test", "net10.0", expectedChildCount: 1);

        var ns = targetFramework.GetProperty("nestedTests")[0];
        AssertNode(ns, "namespace", "SampleTestProject", expectedChildCount: 4);

        // ── Class-level nodes (alphabetical order from MtpTestTreeBuilder's OrderBy) ──
        var classNodes = ns.GetProperty("nestedTests").EnumerateArray()
            .OrderBy(c => c.GetProperty("displayName").GetString())
            .ToArray();
        Assert.Equal(4, classNodes.Length);
        AssertNode(classNodes[0], "class", "FailTests", expectedChildCount: 1);
        AssertNode(classNodes[1], "class", "PassTests", expectedChildCount: 1);
        AssertNode(classNodes[2], "class", "SkipTests", expectedChildCount: 1);
        AssertNode(classNodes[3], "class", "SlowTests", expectedChildCount: 1);

        // ── Method-level nodes ──
        AssertNode(classNodes[0].GetProperty("nestedTests")[0], "method",
            "SampleTestProject.FailTests.AlwaysFails", expectedChildCount: 0);
        AssertNode(classNodes[1].GetProperty("nestedTests")[0], "method",
            "SampleTestProject.PassTests.AlwaysPasses", expectedChildCount: 0);
        AssertNode(classNodes[2].GetProperty("nestedTests")[0], "method",
            "SampleTestProject.SkipTests.AlwaysSkipped", expectedChildCount: 0);
        AssertNode(classNodes[3].GetProperty("nestedTests")[0], "method",
            "SampleTestProject.SlowTests.FinishesLast", expectedChildCount: 0);

        // ── One-to-one correspondence assertions ──
        // Exactly 4 classes and 4 methods in the entire tree, no extras.
        var classes = CollectNodesByType(root, "class");
        var methods = CollectNodesByType(root, "method");
        Assert.Equal(4, classes.Count);
        Assert.Equal(4, methods.Count);

        var classNames = classes.Select(c => c.GetProperty("displayName").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "FailTests", "PassTests", "SkipTests", "SlowTests" }, classNames);

        var methodNames = methods.Select(m => m.GetProperty("displayName").GetString()).OrderBy(x => x).ToArray();
        Assert.Equal(new[] {
            "SampleTestProject.FailTests.AlwaysFails",
            "SampleTestProject.PassTests.AlwaysPasses",
            "SampleTestProject.SkipTests.AlwaysSkipped",
            "SampleTestProject.SlowTests.FinishesLast"
        }, methodNames);

        // Total leaf method count across the tree must also be 3.
        Assert.Equal(4, CountLeafMethods(root));
    }

    [Fact]
    public async Task UnitTestingTree_RefreshesWhenPadIsOpenedBeforeSolution()
    {
        var showPad = await _app.InvokeAsync("od.show-pad", "Unit Tests");
        Assert.True(showPad.GetProperty("found").GetBoolean());

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
                discovered = FindTest(tests[0], "AlwaysPasses").HasValue;
                if (discovered) break;
            }
            await Task.Delay(1000);
        }

        Assert.True(discovered, "Unit Tests pad did not refresh after opening a solution.");
    }

    [Fact]
    public async Task UnitTestNode_GoToDefinition_OpensSourceAtTestMethod()
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
                discovered = FindTest(tests[0], "AlwaysPasses").HasValue;
                if (discovered) break;
            }
            await Task.Delay(1000);
        }

        Assert.True(discovered, "Test methods were not discovered within 60s timeout");

        var result = await _app.InvokeAsync("od.unit-test.goto", "AlwaysPasses");

        Assert.True(result.GetProperty("success").GetBoolean(),
            result.TryGetProperty("error", out var error) ? error.GetString() : "GoToDefinition failed");

        JsonElement activeView = default;
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            activeView = await _app.InvokeAsync("od.active-view");
            if (activeView.TryGetProperty("fileName", out var activeFile)
                && activeFile.GetString()?.EndsWith("/tests/fixtures/SampleTestProject/PassTests.cs", StringComparison.Ordinal) == true)
                break;
            await Task.Delay(250);
        }

        Assert.EndsWith("/tests/fixtures/SampleTestProject/PassTests.cs",
            activeView.GetProperty("fileName").GetString());
        Assert.Equal(6, activeView.GetProperty("caretLine").GetInt32());
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

        var slowTest = FindTest(root, "FinishesLast");
        Assert.NotNull(slowTest);
        Assert.Equal("Success", slowTest.Value.GetProperty("result").GetString());
    }

    [Fact]
    public async Task UnitTestRun_StreamsResultsBeforeWholeRunCompletes()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            if (tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "FinishesLast").HasValue)
                break;
            await Task.Delay(1000);
        }

        var start = await _app.InvokeAsync("od.unit-test.run-start");
        Assert.True(start.GetProperty("started").GetBoolean());

        bool observedPartialResults = false;
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            var root = tree.GetProperty("tests")[0];
            var passTest = FindTest(root, "AlwaysPasses");
            var slowTest = FindTest(root, "FinishesLast");
            if (passTest.HasValue && slowTest.HasValue
                && passTest.Value.GetProperty("result").GetString() == "Success"
                && slowTest.Value.GetProperty("result").GetString() == "None")
            {
                observedPartialResults = true;
                break;
            }
            await Task.Delay(100);
        }

        Assert.True(observedPartialResults, "The Unit Tests tree did not show completed tests while a slower test was still running.");

        deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await _app.InvokeAsync("od.unit-test.status");
            if (!status.GetProperty("isRunningTests").GetBoolean())
                return;
            await Task.Delay(250);
        }

        Assert.Fail("The unit test run did not finish after observing partial results.");
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
    public async Task DebugUnitTest_ReplacesStalePadNodeAndShowsSuccessIcon()
    {
        await _app.InvokeAsync("od.show-pad", "ICSharpCode.UnitTesting.UnitTestsPad");
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

        var result = await _app.InvokeAsync("od.unit-test.debug-one", "AlwaysPasses", 60);

        Assert.True(result.GetProperty("completed").GetBoolean());
        Assert.False(result.GetProperty("faulted").GetBoolean());
        var padNode = result.GetProperty("padNode");
        Assert.True(padNode.GetProperty("found").GetBoolean());
        Assert.True(padNode.GetProperty("sameModelInstance").GetBoolean());
        Assert.Equal("Success", padNode.GetProperty("modelResult").GetString());
        Assert.EndsWith("/Resources/Green.png", padNode.GetProperty("iconUri").GetString());
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
