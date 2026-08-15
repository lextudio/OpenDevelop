// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

// Consolidated add-in integration tests (ILSpy, NuGet, Git, WPF designer, class diagram,
// update check, F#/VB bindings, unit testing, Roslyn refactorings). Originally split across
// IlSpyAddInTests, NuGetAddInTests, GitAddInTests, WpfDesignerTests, ClassDiagramTests,
// UpdateTests, FSharpBindingTests, VBBindingTests, UnitTestingTests and RoslynRefactoringTests.

using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using System;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("30 Add-ins and specialized fixtures")]
public sealed class AddInTests : IAsyncDisposable
{
    readonly string _repoDir;

    const string TestPackageId = "OpenDevelop.TestPackage";
    readonly string _projectDir;

    static readonly string IlSpySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ICSharpCode", "ILSpy.xml");
    readonly string _ilSpySettingsBackup;

    readonly string _solutionDir;
    readonly string _solutionPath;
    readonly string _widgetPath;
    readonly string _widgetServicePath;

    // The WinUI/Uno editing tests mutate the page they design, so they must never point at the
    // tracked sample under src/Samples - same reasoning as _solutionDir above.
    readonly string _unoSampleDir;
    readonly string _unoSolutionPath;
    readonly string _unoPagePath;

    readonly OpenDevelopAppFixture _app;

    public AddInTests(OpenDevelopAppFixture app)
    {
        _app = app;
                _repoDir = Path.Combine(Path.GetTempPath(), "GitAddInTests-" + Guid.NewGuid().ToString("N"));
                SetUpGitRepo();
                // Installing a package mutates the .csproj on disk - copy the fixture to a temp dir so
                // the test doesn't write a PackageReference into this repo's tracked fixture file on
                // every run (the same reasoning as GitAddInTests' per-test temp git repo).
                _projectDir = Path.Combine(Path.GetTempPath(), "NuGetAddInTests-" + Guid.NewGuid().ToString("N"));
                CopyDirectory(app.NuGetFixtureTemplatePath, _projectDir);
                if (File.Exists(IlSpySettingsPath))
                {
                    _ilSpySettingsBackup = Path.Combine(Path.GetTempPath(), "ILSpy.xml." + Guid.NewGuid().ToString("N"));
                    File.Copy(IlSpySettingsPath, _ilSpySettingsBackup);
                    File.Delete(IlSpySettingsPath);
                }
                _solutionDir = Path.Combine(Path.GetTempPath(), "RoslynRefactoringTests-" + Guid.NewGuid().ToString("N"));
                CopyDirectoryOd(Path.GetDirectoryName(app.SolutionExplorerFixturePath)!, _solutionDir);
                _solutionPath = Path.Combine(_solutionDir, Path.GetFileName(app.SolutionExplorerFixturePath));
                _widgetPath = Path.Combine(_solutionDir, "SampleApp", "Models", "Widget.cs");
                _widgetServicePath = Path.Combine(_solutionDir, "SampleApp", "Services", "WidgetService.cs");
                _unoSampleDir = Path.Combine(Path.GetTempPath(), "WinUIDesignerTests-" + Guid.NewGuid().ToString("N"));
                CopyDirectoryOd(Path.GetDirectoryName(app.UnoXamlSampleSolutionPath)!, _unoSampleDir);
                _unoSolutionPath = Path.Combine(_unoSampleDir, Path.GetFileName(app.UnoXamlSampleSolutionPath));
                _unoPagePath = Path.Combine(_unoSampleDir, "MainPage.xaml");
    }

    [Fact]
    public async Task FSharpAddIn_IsLoaded()
    {
        var result = await _app.InvokeAsync("od.addins");

        var addins = result.GetProperty("addins").EnumerateArray().ToList();

        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("FSharpBinding.addin"));
    }

	[Fact]
	public async Task AspNetCoreAddIn_OpensBuildsAndRunsKestrelSample()
	{
		var addinsResult = await _app.InvokeAsync("od.addins");
		Assert.Contains(addinsResult.GetProperty("addins").EnumerateArray(),
			a => a.GetProperty("fileName").GetString()!.Contains("AspNetCore.addin", StringComparison.Ordinal));

		var opened = await _app.ReopenSolutionAsync(_app.AspNetCoreSampleSolutionPath);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
		var build = await _app.InvokeAsync("od.build-solution", "AspNetCoreSample");
		if (build.GetProperty("result").GetString() != "Success")
			Assert.Fail(await _app.DescribeBuildAsync(build));

		var razorPath = Path.Combine(Path.GetDirectoryName(_app.AspNetCoreSampleSolutionPath)!, "StatusCard.razor");
		var razorOpened = await _app.InvokeAsync("od.open-file", razorPath);
		Assert.True(razorOpened.GetProperty("opened").GetBoolean(), razorOpened.ToString());
		var razorView = await _app.InvokeAsync("od.active-view");
		Assert.True(razorView.GetProperty("isAvalonEdit").GetBoolean(), razorView.ToString());
		Assert.Equal("ASP.NET Core Razor", razorView.GetProperty("syntaxHighlighting").GetString());
		Assert.Contains("@code", razorView.GetProperty("textPreview").GetString());

		var status = await _app.InvokeAsync("od.aspnetcore.status", "AspNetCoreSample");
		Assert.True(status.GetProperty("success").GetBoolean(), status.ToString());
		Assert.True(status.GetProperty("startable").GetBoolean());
		Assert.Equal("http://localhost:5188", status.GetProperty("applicationUrls").GetString());
		Assert.Contains("run", status.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()));

		var started = await _app.InvokeAsync("od.aspnetcore.start", "AspNetCoreSample");
		Assert.True(started.GetProperty("success").GetBoolean(), started.ToString());
		try
		{
			using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
			var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
			while (DateTime.UtcNow < deadline)
			{
				try
				{
					Assert.Equal("healthy", await client.GetStringAsync("http://localhost:5188/health", TestContext.Current.CancellationToken));
					return;
				}
				catch (HttpRequestException) { }
				catch (TaskCanceledException) { }
				await Task.Delay(100, TestContext.Current.CancellationToken);
			}
			Assert.Fail("ASP.NET Core sample did not become reachable at http://localhost:5188/health");
		}
		finally
		{
			var stopped = await _app.InvokeAsync("od.aspnetcore.stop");
			Assert.True(stopped.GetProperty("success").GetBoolean(), stopped.ToString());
		}
	}

    [Fact]
    public async Task FSharpFixture_LoadsShowsSourceEditsAndBuilds()
    {
        var result = await _app.ReopenSolutionAsync(_app.FSharpFixtureSolutionPath);

        Assert.True(result.GetProperty("success").GetBoolean(), $"OpenSolutionOrProject returned false for {_app.FSharpFixtureSolutionPath}");
        // The fixture ships a .slnx beside its .sln, and the app adopts an existing .slnx without
        // regenerating it, so opening the .sln lands on the .slnx.
        Assert.Equal(Path.ChangeExtension(_app.FSharpFixtureSolutionPath, ".slnx"), result.GetProperty("currentSolution").GetString());

        var tree = await _app.InvokeAsync("od.solution-tree");
        var project = tree.GetProperty("projects").EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString() == "FSharpFixture");
        Assert.True(project.ValueKind != JsonValueKind.Undefined, $"FSharpFixture project not found in solution tree: {tree}");

        var files = project.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains(files, f => f != null && f.EndsWith("Program.fs", StringComparison.OrdinalIgnoreCase));

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

        var preBuild = Path.Combine(Path.GetDirectoryName(_app.FSharpFixtureSolutionPath)!, "bin", "Debug", "net8.0", "FSharpFixture.dll");
        if (File.Exists(preBuild))
            File.Delete(preBuild);

        var buildResult = await _app.InvokeAsync("od.build-solution", "FSharpFixture");

        // od.build-solution's JSON only has an "error" property for the early-exit cases (no
        // solution open / project not found) - once a build actually runs, "success" is always
        // true (the DevFlow call itself didn't throw) and the real pass/fail signal is "result".
        if (buildResult.GetProperty("result").GetString() != "Success")
            Assert.Fail(await _app.DescribeBuildAsync(buildResult));
    }

    [Fact]
    public async Task VBAddIn_IsLoaded()
    {
        var result = await _app.InvokeAsync("od.addins");

        var addins = result.GetProperty("addins").EnumerateArray().ToList();

        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("VBBinding.addin"));
    }

    [Fact]
    public async Task VBFixture_LoadsShowsSourceParsesAndBuilds()
    {
        var result = await _app.ReopenSolutionAsync(_app.VBFixtureSolutionPath);

        Assert.True(result.GetProperty("success").GetBoolean(), $"OpenSolutionOrProject returned false for {_app.VBFixtureSolutionPath}");
        // The fixture ships a .slnx beside its .sln, and the app adopts an existing .slnx without
        // regenerating it, so opening the .sln lands on the .slnx.
        Assert.Equal(Path.ChangeExtension(_app.VBFixtureSolutionPath, ".slnx"), result.GetProperty("currentSolution").GetString());

        var tree = await _app.InvokeAsync("od.solution-tree");
        var project = tree.GetProperty("projects").EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString() == "VBFixture");
        Assert.True(project.ValueKind != JsonValueKind.Undefined, $"VBFixture project not found in solution tree: {tree}");

        var files = project.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains(files, f => f != null && f.EndsWith("Class1.vb", StringComparison.OrdinalIgnoreCase));

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

        // The actual point of this session's work: the shared ILanguageService (the integration
        // point GoToDefinition/completion/etc. actually use) has a language service registered for
        // this .vb file - the Roslyn VisualBasic backend - not the previous "no language service
        // for VB at all" state. od.parser.status reports the registration, not the language name.
        var parserStatus = await _app.InvokeAsync("od.parser.status", vbPath);
        Assert.True(parserStatus.GetProperty("hasDocument").GetBoolean(),
            $"Expected a real Roslyn Document for {vbPath}: {parserStatus}");

        var preBuild = Path.Combine(Path.GetDirectoryName(_app.VBFixtureSolutionPath)!, "bin", "Debug", "net10.0", "VBFixture.dll");
        if (File.Exists(preBuild))
            File.Delete(preBuild);

        var buildResult = await _app.InvokeAsync("od.build-solution", "VBFixture");

        if (buildResult.GetProperty("result").GetString() != "Success")
            Assert.Fail(await _app.DescribeBuildAsync(buildResult));
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

    // Merged with UnitTestDiscovery_DoesNotShowSourceExcludedFromCompileItems below: both open the
    // same FixtureSolutionPath and only read back the discovered test tree - no run, no mutation -
    // so they share a single open instead of two.
    [Fact]
    public async Task UnitTestingTree_ShowsTestsAfterOpeningTestProject()
    {
        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);

        JsonElement tree = default;
        bool discovered = false;
        discovered = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            tree = await _app.InvokeAsync("od.unit-test.tree");
            Assert.True(tree.GetProperty("available").GetBoolean());
            var tests = tree.GetProperty("tests");
            if (tests.GetArrayLength() == 0)
                return false;
            return FindTest(tests[0], "AlwaysPasses").HasValue
                || FindTest(tests[0], "AlwaysFails").HasValue;
        }, TimeSpan.FromSeconds(60));

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

        // --- was: UnitTestDiscovery_DoesNotShowSourceExcludedFromCompileItems ---
        Assert.True(tree.GetProperty("tests").GetArrayLength() > 0,
            "The fixture test project was not discovered.");
        Assert.Null(FindTest(tree.GetProperty("tests")[0], "NotPartOfTheBuiltTestAssembly"));
    }

    [Fact]
    public async Task UnitTestingTree_RefreshesWhenPadIsOpenedBeforeSolution()
    {
        var showPad = await _app.InvokeAsync("od.show-pad", "Unit Tests");
        Assert.True(showPad.GetProperty("found").GetBoolean());

        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);

        JsonElement tree = default;
        bool discovered = false;
        discovered = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            tree = await _app.InvokeAsync("od.unit-test.tree");
            Assert.True(tree.GetProperty("available").GetBoolean());
            var tests = tree.GetProperty("tests");
            if (tests.GetArrayLength() == 0)
                return false;
            return FindTest(tests[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

        Assert.True(discovered, "Unit Tests pad did not refresh after opening a solution.");
    }

    [Fact]
    public async Task UnitTestNode_GoToDefinition_OpensSourceAtTestMethod()
    {
        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);

        JsonElement tree = default;
        bool discovered = false;
        discovered = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            tree = await _app.InvokeAsync("od.unit-test.tree");
            Assert.True(tree.GetProperty("available").GetBoolean());
            var tests = tree.GetProperty("tests");
            if (tests.GetArrayLength() == 0)
                return false;
            return FindTest(tests[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

        Assert.True(discovered, "Test methods were not discovered within 60s timeout");

        var result = await _app.InvokeAsync("od.unit-test.goto", "AlwaysPasses");

        Assert.True(result.GetProperty("success").GetBoolean(),
            result.TryGetProperty("error", out var error) ? error.GetString() : "GoToDefinition failed");

        JsonElement activeView = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            activeView = await _app.InvokeAsync("od.active-view");
            return activeView.TryGetProperty("fileName", out var activeFile)
                && activeFile.GetString()?.EndsWith("/tests/fixtures/SampleTestProject/PassTests.cs", StringComparison.Ordinal) == true;
        }, TimeSpan.FromSeconds(10), initialDelayMs: 50, maxDelayMs: 250);

        Assert.EndsWith("/tests/fixtures/SampleTestProject/PassTests.cs",
            activeView.GetProperty("fileName").GetString());
        Assert.Equal(6, activeView.GetProperty("caretLine").GetInt32());
    }

    [Fact]
    public async Task UnitTestRun_ProducesExpectedResults()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);

        JsonElement tree = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

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

        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "FinishesLast").HasValue;
        }, TimeSpan.FromSeconds(60));

        var start = await _app.InvokeAsync("od.unit-test.run-start");
        Assert.True(start.GetProperty("started").GetBoolean());

        bool observedPartialResults = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            var root = tree.GetProperty("tests")[0];
            var passTest = FindTest(root, "AlwaysPasses");
            var slowTest = FindTest(root, "FinishesLast");
            return passTest.HasValue && slowTest.HasValue
                && passTest.Value.GetProperty("result").GetString() == "Success"
                && slowTest.Value.GetProperty("result").GetString() == "None";
        }, TimeSpan.FromSeconds(20), initialDelayMs: 50, maxDelayMs: 100);

        Assert.True(observedPartialResults, "The Unit Tests tree did not show completed tests while a slower test was still running.");

        bool finished = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var status = await _app.InvokeAsync("od.unit-test.status");
            return !status.GetProperty("isRunningTests").GetBoolean();
        }, TimeSpan.FromSeconds(30), initialDelayMs: 50, maxDelayMs: 250);
        if (finished)
            return;

        Assert.Fail("The unit test run did not finish after observing partial results.");
    }

    [Fact]
    public async Task UnitTestPad_ShowsDiscoveredTotalInStatusBar()
    {
        // Regression coverage for the "Total: 0" bug (doc/technotes/ilspy.md "Legacy pad
        // migration", 2026-08-09): the pad's status bar only ever counted *runs*, so a populated
        // test tree next to "Total: 0" read as broken. LoadOpenSolution now counts the loaded
        // test tree's leaves, so the discovered count must show up without any test run.
        await _app.InvokeAsync("od.show-pad", "ICSharpCode.UnitTesting.UnitTestsPad");
        await _app.ReopenSolutionAsync(_app.FixtureSolutionPath);

        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

        // The status bar text is a plain TextBlock ("Total: N"); poll the visual tree until it
        // appears (the pad realizes its content only once shown).
        string? totalText = null;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var uiTree = await _app.GetUITreeAsync();
            totalText = FlattenElements(uiTree)
                .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                    && e.TryGetProperty("text", out var txt)
                    && txt.GetString()?.StartsWith("Total: ", StringComparison.Ordinal) == true)
                .Select(e => e.GetProperty("text").GetString())
                .FirstOrDefault();
            return totalText != null;
        }, TimeSpan.FromSeconds(30));

        Assert.NotNull(totalText);
        var total = int.Parse(totalText!.Substring("Total: ".Length));
        Assert.True(total > 0, $"Expected the discovered test count in the status bar, got '{totalText}'.");
    }

    [Fact]
    public async Task DebugUnitTest_StartsDebugSessionWithoutHanging()
    {
        // od.unit-test.debug is bounded by Task.WhenAny on the DevFlow side, so this action call
        // itself can't hang the caller indefinitely -- if the underlying debugger session wedges
        // (see the known debugger-hang issue), the worst case is this HTTP call blocking up to
        // the fixture's own HttpClient.Timeout (120s), not forever.
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);

        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

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

        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

        var padTree = await _app.InvokeAsync("od.unit-test.pad-tree");
        Assert.True(padTree.GetProperty("found").GetBoolean());
        Assert.True(padTree.GetProperty("rootChildCount").GetInt32() > 0,
            "The Unit Tests pad root did not reload after tests were discovered.");
        Assert.True(padTree.GetProperty("itemCount").GetInt32() > 1,
            "The Unit Tests pad only shows the project wrapper instead of expanding the single-project test hierarchy.");

        var result = await _app.InvokeAsync("od.unit-test.debug-one", "AlwaysPasses", 45);
        if (!result.GetProperty("completed").GetBoolean())
        {
            // A long fixture sequence can leave a timed-out MTP/DAP operation alive even though
            // the DevFlow request itself returned. Cancel both halves, wait for TestService to
            // become idle, reload the fixture, and retry once from a known state.
            await _app.InvokeAsync("od.unit-test.cancel");
            await _app.InvokeAsync("od.debug.stop");
            await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                var status = await _app.InvokeAsync("od.unit-test.status");
                return !status.GetProperty("isRunningTests").GetBoolean();
            }, TimeSpan.FromSeconds(15));

            await _app.ReopenSolutionAsync(_app.FixtureSolutionPath);
            await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                var tree = await _app.InvokeAsync("od.unit-test.tree");
                return tree.GetProperty("tests").GetArrayLength() > 0
                    && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
            }, TimeSpan.FromSeconds(60));
            result = await _app.InvokeAsync("od.unit-test.debug-one", "AlwaysPasses", 60);
        }

        Assert.True(result.GetProperty("completed").GetBoolean(), result.ToString());
        Assert.False(result.GetProperty("faulted").GetBoolean(), result.ToString());
        var padNode = result.GetProperty("padNode");
        Assert.True(padNode.GetProperty("found").GetBoolean());
        Assert.True(padNode.GetProperty("sameModelInstance").GetBoolean());
        Assert.Equal("Success", padNode.GetProperty("modelResult").GetString());
        Assert.Equal("System.Windows.Media.DrawingImage", padNode.GetProperty("iconType").GetString());
    }

    [Fact]
    public async Task UnitTestRun_OutputPadCapturesMessages()
    {
        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);

        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

        await _app.InvokeAsync("od.unit-test.run");

        var output = await _app.InvokeAsync("od.unit-test.output");
        Assert.Equal("UnitTesting", output.GetProperty("category").GetString());
        var text = output.GetProperty("text").GetString()!;
        Assert.Contains("AlwaysPasses", text);
        Assert.Contains("AlwaysFails", text);
        Assert.Contains("AlwaysSkipped", text);
    }

    [Fact]
    public async Task UnitTestPad_ExpandNode_RevealsChildNodesInPadTree()
    {
        await _app.InvokeAsync("od.show-pad", "ICSharpCode.UnitTesting.UnitTestsPad");
        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);

        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

        var expandResult = await _app.InvokeAsync("od.unit-test.expand-node", "SampleTestProject");
        Assert.True(expandResult.GetProperty("found").GetBoolean(),
            "Expected the SampleTestProject node to be present in the Unit Tests pad tree");
        var expandedNode = expandResult.GetProperty("node");
        Assert.Equal("SampleTestProject", expandedNode.GetProperty("displayName").GetString());
        Assert.True(expandedNode.GetProperty("childCount").GetInt32() > 0,
            "Expected the expanded project node to expose at least one child node");

        // pad-node's snapshot has no displayName/childCount - it reports the rendered node's
        // model identity (sameModelInstance vs. the ITestService model) and result state.
        var padNode = await _app.InvokeAsync("od.unit-test.pad-node", "SampleTestProject");
        Assert.True(padNode.GetProperty("found").GetBoolean(),
            "Expected the rendered Unit Tests pad node for SampleTestProject");
        Assert.True(padNode.GetProperty("sameModelInstance").GetBoolean(),
            "Expected the pad node to render the same test model instance the tree reports");
    }

    // The pad's TestTreeView renders each node's DisplayName as a real TextBlock in the WPF visual
    // tree (SharpTreeView template -> ContentPresenter Content="{Binding Text}"). od.unit-test.tree
    // covers the ITestService model; this locks in that the pad itself displays the discovered test
    // names as visible UI once the class node is expanded.
    [Fact]
    public async Task UnitTestPad_RendersTestNamesInUiTree()
    {
        await _app.InvokeAsync("od.show-pad", "ICSharpCode.UnitTesting.UnitTestsPad");
        await _app.EnsureSolutionOpenAsync(_app.FixtureSolutionPath);

        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.InvokeAsync("od.unit-test.tree");
            return tree.GetProperty("tests").GetArrayLength() > 0
                && FindTest(tree.GetProperty("tests")[0], "AlwaysPasses").HasValue;
        }, TimeSpan.FromSeconds(60));

        // The pad tree refreshes asynchronously after discovery and only auto-expands the single-
        // child chain up to the framework node, so walk the chain: project (occurrence 1 of
        // "SampleTestProject") -> framework ("net10.0") -> namespace (occurrence 2 - the namespace
        // displays the same name as its project) -> class ("PassTests"). Then the method child
        // ("AlwaysPasses") is realized and rendered.
        JsonElement expandResult = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            expandResult = await _app.InvokeAsync("od.unit-test.expand-node", "SampleTestProject", 1);
            return expandResult.TryGetProperty("found", out var f) && f.GetBoolean();
        }, TimeSpan.FromSeconds(30));
        Assert.True(expandResult.GetProperty("found").GetBoolean(),
            "Expected the SampleTestProject node to be expandable in the Unit Tests pad");

        expandResult = await _app.InvokeAsync("od.unit-test.expand-node", "net10.0");
        Assert.True(expandResult.GetProperty("found").GetBoolean(),
            "Expected the net10.0 framework node to be expandable in the Unit Tests pad");

        expandResult = await _app.InvokeAsync("od.unit-test.expand-node", "SampleTestProject", 2);
        Assert.True(expandResult.GetProperty("found").GetBoolean(),
            "Expected the SampleTestProject namespace node to be expandable in the Unit Tests pad");

        // The pad renders its tree items only while it's the pane group's selected tab
        // (LayoutAnchorablePaneControl is a TabControl - unselected content never lays out, so
        // SharpTreeView's virtualization realizes nothing). Opening the solution re-selected the
        // Projects tab next to it, so re-activate the pad before polling for rendered text.
        await _app.InvokeAsync("od.show-pad", "ICSharpCode.UnitTesting.UnitTestsPad");

        // The pad's TreeView renders its items asynchronously after the model is expanded, so
        // poll for the class name to appear in the visual tree before asserting (the method-name
        // assertion below already waits the same way).
        var uiTree = await _app.GetUITreeAsync();
        var texts = FlattenElements(uiTree)
            .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
            .Select(e => e.GetProperty("text").GetString())
            .ToList();
        bool passTestsFound = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            uiTree = await _app.GetUITreeAsync();
            texts = FlattenElements(uiTree)
                .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                    && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
                .Select(e => e.GetProperty("text").GetString())
                .ToList();
            return texts.Any(t => t == "PassTests");
        }, TimeSpan.FromSeconds(30));
        if (!passTestsFound)
            Assert.Fail($"no PassTests text; padTree={await _app.InvokeAsync("od.unit-test.pad-tree")}; panePos={await _app.InvokeAsync("od.layout.pane-position", "ICSharpCode.UnitTesting.UnitTestsPad")}; texts={string.Join("|", texts.Take(50))};");
        Assert.Contains(texts, t => t == "PassTests");

        expandResult = await _app.InvokeAsync("od.unit-test.expand-node", "PassTests");
        Assert.True(expandResult.GetProperty("found").GetBoolean(),
            "Expected the PassTests node to be expandable in the Unit Tests pad");
        if (!expandResult.GetProperty("found").GetBoolean())
            Assert.Fail($"expand PassTests failed: {expandResult}; padTree={await _app.InvokeAsync("od.unit-test.pad-tree")};");

        bool methodFound = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            uiTree = await _app.GetUITreeAsync();
            texts = FlattenElements(uiTree)
                .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                    && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
                .Select(e => e.GetProperty("text").GetString())
                .ToList();
            return texts.Any(t => t.EndsWith(".AlwaysPasses", StringComparison.Ordinal));
        }, TimeSpan.FromSeconds(30));
        if (!methodFound)
            Assert.Fail($"no AlwaysPasses text; padTree={await _app.InvokeAsync("od.unit-test.pad-tree")}; texts={string.Join("|", texts.Take(60))};");
        Assert.Contains(texts, t => t.EndsWith(".AlwaysPasses", StringComparison.Ordinal));
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
    public async Task OpenXamlFile_LoadsDesignerWithToolboxAndOutline()
    {
        await _app.EnsureSolutionOpenAsync(_app.WpfSampleSolutionPath);

        var xamlPath = Path.Combine(Path.GetDirectoryName(_app.WpfSampleSolutionPath)!, "MainWindow.xaml");
        var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean(), $"Failed to open {xamlPath}");

        var status = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "Window", timeoutSeconds: 30, reactivatePath: xamlPath);

        Assert.True(status.GetProperty("active").GetBoolean());
        Assert.True(status.GetProperty("designerLoaded").GetBoolean(),
            "Expected the WPF design surface to load the XAML root (not fall back to WpfDocumentError)");
        Assert.Equal("Window", status.GetProperty("rootItemType").GetString());

        // Toolbox: the popular-controls group plus grouped controls populate WpfToolbox.Instance.
        Assert.True(status.GetProperty("toolboxItemCount").GetInt32() > 0,
            "Expected the toolbox to list at least the popular WPF controls");
        Assert.True(status.GetProperty("toolboxGroupCount").GetInt32() > 0,
            "Expected the toolbox to show at least one control group");

        // Outline pad: the flattened element tree should include MainWindow.xaml's named controls.
        var outlineNames = status.GetProperty("outlineNames").EnumerateArray()
            .Select(n => n.GetString())
            .ToList();

        Assert.True(status.GetProperty("outlineChildCount").GetInt32() > 0,
            "Expected the Outline pad's root node to have at least one child");
        Assert.Contains("PrimaryButton", outlineNames);
        Assert.Contains("MainPane", outlineNames);
    }

    [Fact]
    public async Task OpenUnoXamlFile_UsesWinUIXamlDesignerInsteadOfWpfDesigner()
    {
        var openedSolution = await _app.ReopenSolutionAsync(_app.UnoXamlSampleSolutionPath);
        Assert.True(openedSolution.GetProperty("success").GetBoolean(), openedSolution.ToString());
        var xamlPath = Path.Combine(Path.GetDirectoryName(_app.UnoXamlSampleSolutionPath)!, "MainPage.xaml");
        var opened = await _app.InvokeAsync("od.open-file", xamlPath);
        Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());

        JsonElement status = default;
        // Materialization compiles the document through Roslyn and loads a collectible preview
        // assembly before ProGPU presents its first frame, so poll on "rendered", not on "active".
        var ready = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.winui-designer.status");
            return status.TryGetProperty("active", out var active) && active.GetBoolean()
                && status.GetProperty("rendered").GetBoolean();
        }, TimeSpan.FromSeconds(60), initialDelayMs: 100, maxDelayMs: 500);

        Assert.True(ready, status.ToString());
        Assert.Equal("Uno", status.GetProperty("framework").GetString());
        // The preview must come from ProGPU's compiled WinUI pipeline. A WPF XamlReader renderer
        // impersonating a WinUI designer is explicitly not an acceptable pass.
        Assert.Contains("Rendered by Uno design host", status.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Theme switching must re-resolve ThemeResource against the new theme and actually change
    /// the rendered pixels: UnoXamlSample's App.xaml maps PageBackgroundBrush to #EEEEEE (Light)
    /// and #222222 (Dark), so the sampled bitmap center must flip between the two.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_ThemeSwitch_ChangesRenderedBackgroundPixels()
    {
        await OpenUnoDesignerAsync();

        // The designer persists its theme across sessions, so a previous run (or manual
        // session) may have left it Dark - pin to Light and wait for the re-render first.
        await _app.InvokeAsync("od.winui-designer.theme", "Light");
        var lightBaseline = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var s = await _app.InvokeAsync("od.winui-designer.render-sample");
            return s.GetProperty("sample").GetString()?.Contains("center=#EEEEEE") == true;
        }, TimeSpan.FromSeconds(20));
        Assert.True(lightBaseline, "Light theme should render the Light PageBackgroundBrush");

        var light = await _app.InvokeAsync("od.winui-designer.render-sample");
        Assert.Contains("center=#EEEEEE", light.GetProperty("sample").GetString());

        var set = await _app.InvokeAsync("od.winui-designer.theme", "Dark");
        Assert.True(set.GetProperty("success").GetBoolean(), set.ToString());

        // The theme switch re-renders asynchronously; poll the actual pixels until the
        // Dark PageBackgroundBrush (#222222) shows up rather than trusting status.rendered,
        // which flips before the re-render has settled.
        JsonElement darkSample = default;
        var darkArrived = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            darkSample = await _app.InvokeAsync("od.winui-designer.render-sample");
            return darkSample.GetProperty("sample").GetString()?.Contains("center=#222222") == true;
        }, TimeSpan.FromSeconds(20));
        Assert.True(darkArrived, "Dark theme should re-render with #222222, got: " + darkSample);

        var query = await _app.InvokeAsync("od.winui-designer.theme", "query");
        Assert.Equal("Dark", query.GetProperty("theme").GetString());

        await _app.InvokeAsync("od.winui-designer.theme", "Light");
        var lightArrived = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var s = await _app.InvokeAsync("od.winui-designer.render-sample");
            return s.GetProperty("sample").GetString()?.Contains("center=#EEEEEE") == true;
        }, TimeSpan.FromSeconds(20));
        Assert.True(lightArrived, "Light theme should re-render with #EEEEEE");
    }

    /// <summary>The phone/tablet/desktop canvas presets must resize the rendered design.</summary>
    [Fact]
    public async Task WinUIDesigner_DesignSizePresets_ResizeCanvas()
    {
        await OpenUnoDesignerAsync();

        var phone = await _app.InvokeAsync("od.winui-designer.design-size", "phone");
        Assert.Equal("phone", phone.GetProperty("preset").GetString());
        var phoneStatus = await WaitForStatusContainingAsync("390");
        Assert.Contains("390", phoneStatus);

        var tablet = await _app.InvokeAsync("od.winui-designer.design-size", "tablet");
        Assert.Equal("tablet", tablet.GetProperty("preset").GetString());
        var tabletStatus = await WaitForStatusContainingAsync("768");
        Assert.Contains("768", tabletStatus);

        var reset = await _app.InvokeAsync("od.winui-designer.design-size", "reset");
        Assert.True(reset.GetProperty("success").GetBoolean(), reset.ToString());
        var resetStatus = await WaitForStatusContainingAsync("1280");
        Assert.Contains("1280", resetStatus);
    }

    /// <summary>Polls od.winui-designer.status until its status text contains the fragment
    /// (a re-render has settled), returning the status text.</summary>
    async Task<string> WaitForStatusContainingAsync(string fragment)
    {
        string last = "";
        var arrived = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var s = await _app.InvokeAsync("od.winui-designer.status");
            last = s.GetProperty("status").GetString() ?? "";
            return last.Contains(fragment, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(20));
        Assert.True(arrived, $"Status should contain '{fragment}', last was: {last}");
        return last;
    }

    /// <summary>
    /// Multi-select + align: the secondary element's right edge must land on the primary
    /// element's right edge (measured through the rendered surface's screen bounds).
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_MultiSelectAlign_MovesElementToMatchPrimary()
    {
        await OpenUnoDesignerAsync();

        var multi = await _app.InvokeAsync("od.winui-designer.multi-select", "TitleText,PrimaryButton");
        Assert.True(multi.GetProperty("success").GetBoolean(), multi.ToString());

        var before = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", "PrimaryButton");
        Assert.True(before.GetProperty("success").GetBoolean(), before.ToString());

        var aligned = await _app.InvokeAsync("od.winui-designer.align", "right");
        Assert.True(aligned.GetProperty("success").GetBoolean(), aligned.ToString());

        // Align lands as a source edit and re-renders asynchronously; poll the surface
        // bounds until the secondary element's right edge actually reaches the primary's.
        double primaryRight = 0, movedRight = 0;
        var converged = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var primary = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", "TitleText");
            var moved = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", "PrimaryButton");
            primaryRight = primary.GetProperty("x").GetDouble() + primary.GetProperty("width").GetDouble();
            movedRight = moved.GetProperty("x").GetDouble() + moved.GetProperty("width").GetDouble();
            return Math.Abs(primaryRight - movedRight) < 1.5;
        }, TimeSpan.FromSeconds(20));
        Assert.True(converged, $"Right edges should match after align right: primary={primaryRight} moved={movedRight}");
    }

    /// <summary>
    /// Context commands (copy/paste/wrap/delete) must land as source edits: pasting a copied
    /// element creates a uniquely named sibling, wrapping nests it in a Grid, and deleting the
    /// child leaves the wrapper in place.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_ContextCommands_CopyPasteWrapDeleteLandAsSourceEdits()
    {
        await OpenUnoDesignerAsync();

        var copied = await _app.InvokeAsync("od.winui-designer.context", "copy", "TitleText");
        Assert.True(copied.GetProperty("success").GetBoolean(), copied.ToString());
        var pasted = await _app.InvokeAsync("od.winui-designer.context", "paste", "RootStack");
        Assert.True(pasted.GetProperty("success").GetBoolean(), pasted.ToString());

        var status = await WaitForRenderedAsync();
        var names = status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Contains("TextBlock1", names);
        Assert.True(status.GetProperty("isDirty").GetBoolean(), "Pasting must dirty the document");

        var wrapped = await _app.InvokeAsync("od.winui-designer.context", "wrap-grid", "TextBlock1");
        Assert.True(wrapped.GetProperty("success").GetBoolean(), wrapped.ToString());
        status = await WaitForRenderedAsync();
        names = status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Contains("Grid1", names);

        var deleted = await _app.InvokeAsync("od.winui-designer.context", "delete", "TextBlock1");
        Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString());
        status = await WaitForRenderedAsync();
        names = status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.DoesNotContain("TextBlock1", names);
        Assert.Contains("Grid1", names);
    }

    /// <summary>
    /// Drives the full Phase 5 editing loop through the shell's own pads and asserts after every
    /// step that the change reached the XAML *source* - not just the runtime visual tree - and that
    /// ProGPU re-rendered from it. Insertion goes through the shared Toolbox pad's item list and
    /// the property change through the real Properties pad PropertyItem, so this cannot pass if
    /// the designer quietly grew its own private chrome.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_ToolboxInsertSelectEditDeleteUndoRedo_AllLandAsSourceEdits()
    {
        var status = await OpenUnoDesignerAsync();

        // The shared Toolbox pad is populated with WinUI/Uno controls.
        Assert.True(status.GetProperty("toolboxItemCount").GetInt32() > 0,
            "Expected the shared Toolbox pad to list WinUI/Uno controls: " + status);
        Assert.True(status.GetProperty("toolboxGroupCount").GetInt32() > 0,
            "Expected the Toolbox pad to show at least one control group: " + status);
        Assert.True(status.GetProperty("outlineChildCount").GetInt32() > 0,
            "Expected the Outline pad to show the page's element tree: " + status);

        // ---------- Toolbox insertion becomes a source edit ----------
        var inserted = await _app.InvokeAsync("od.winui-designer.toolbox.insert", "TextBlock", "");
        Assert.True(inserted.GetProperty("success").GetBoolean(), inserted.ToString());
        var insertedName = inserted.GetProperty("insertedName").GetString()!;

        status = await WaitForRenderedAsync();
        Assert.Contains(insertedName, status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(insertedName, status.GetProperty("selectedName").GetString());
        Assert.True(status.GetProperty("isDirty").GetBoolean(),
            "Inserting a control must dirty the file, proving it is a document edit and not a visual-tree-only change");

        // ---------- Selecting populates the SHARED Properties pad ----------
        var selected = await _app.InvokeAsync("od.winui-designer.select", insertedName);
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
        Assert.Equal(
            "ICSharpCode.WinUIXamlDesigner.WinUIXamlElementPropertyAdapter",
            selected.GetProperty("propertyPadSelectedType").GetString());

        // ---------- Editing through the real Properties pad rewrites the source ----------
        var edited = await _app.InvokeAsync("od.winui-designer.properties-pad.edit", "Name", insertedName + "Renamed");
        Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
        Assert.Equal(insertedName + "Renamed", edited.GetProperty("after").GetString());

        status = await WaitForRenderedAsync();
        Assert.Contains(insertedName + "Renamed",
            status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()));

        // Save, then read the file back: the edits must be real XAML text on disk.
        await _app.InvokeAsync("od.file.save", _unoPagePath);
        var onDisk = await File.ReadAllTextAsync(_unoPagePath);
        Assert.Contains("<TextBlock", onDisk);
        Assert.Contains(insertedName + "Renamed", onDisk);

        // ---------- Undo/Redo ----------
        var undo = await _app.InvokeAsync("od.winui-designer.undo");
        Assert.True(undo.GetProperty("success").GetBoolean(), undo.ToString());
        Assert.DoesNotContain(insertedName + "Renamed",
            undo.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()));

        var redo = await _app.InvokeAsync("od.winui-designer.redo");
        Assert.True(redo.GetProperty("success").GetBoolean(), redo.ToString());
        Assert.Contains(insertedName + "Renamed",
            redo.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()));

        // Undo all the way past the insertion: the element is gone from the document entirely.
        while ((await _app.InvokeAsync("od.winui-designer.status")).GetProperty("canUndo").GetBoolean())
            await _app.InvokeAsync("od.winui-designer.undo");
        status = await WaitForRenderedAsync();
        Assert.DoesNotContain(insertedName,
            status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()));

        // ---------- Delete ----------
        await _app.InvokeAsync("od.winui-designer.redo");
        var reinserted = (await _app.InvokeAsync("od.winui-designer.status"))
            .GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Contains(insertedName, reinserted);

        Assert.True((await _app.InvokeAsync("od.winui-designer.select", insertedName)).GetProperty("success").GetBoolean());
        var deleted = await _app.InvokeAsync("od.winui-designer.delete");
        Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString());
        Assert.DoesNotContain(insertedName,
            deleted.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()));

        // The preview must still be alive after all of that, not stuck on a stale/blank frame.
        status = await WaitForRenderedAsync();
        Assert.Contains("Rendered by Uno design host", status.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Editing the XAML in the Source view and switching back to Design must re-parse and
    /// re-render - the design surface is a view over the document, not an independent copy.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_SourceEditOutsideDesigner_RefreshesDesignSurface()
    {
        await OpenUnoDesignerAsync();

        // Go back to the Source tab and type into the real AvalonEdit document, exactly as a user
        // switching tabs and editing would - writing to disk behind the IDE's back would not reach
        // the open buffer at all, and would not be a test of the designer's refresh path.
        var switched = await _app.InvokeAsync("od.winui-designer.switch-to-source");
        Assert.True(switched.GetProperty("success").GetBoolean(), switched.ToString());

        var edit = await _app.InvokeAsync("od.search.replace", "Hello Uno", "Edited In Source", "solution");
        Assert.True(edit.GetProperty("success").GetBoolean(), edit.ToString());

        // Re-activating the Design view is what makes SharpDevelop hand this secondary view the
        // changed document; the designer must re-parse and re-render from it.
        var status = await WaitForRenderedAsync();
        Assert.Null(status.GetProperty("documentError").GetString());
        Assert.Contains("Rendered by Uno design host", status.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase);

        await _app.InvokeAsync("od.file.save-all");
        var onDisk = await File.ReadAllTextAsync(_unoPagePath);
        Assert.Contains("Edited In Source", onDisk);
        // The designer must not have rewritten a document it never edited.
        Assert.Contains("x:Class=\"UnoXamlSample.MainPage\"", onDisk);
    }

    /// <summary>
    /// Editing a control's property through the shared Properties pad lands as a source edit and
    /// the re-rendered design surface reflects it - the WinUI counterpart of the WPF designer's
    /// properties-pad coverage.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_PropertiesPadEdit_UpdatesSourceAndRender()
    {
        await OpenUnoDesignerAsync();

        var selected = await _app.InvokeAsync("od.winui-designer.select", "PrimaryButton");
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());

        var beforeBounds = (await _app.InvokeAsync("od.winui-designer.describe-element", "PrimaryButton"))
            .GetProperty("description").GetString();

        var edited = await WaitForWinUIPropertiesPadEditAsync("Content", "Changed through Properties", timeoutSeconds: 10);
        Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
        Assert.Equal("PrimaryButton", edited.GetProperty("selectedName").GetString());
        Assert.Equal("Content", edited.GetProperty("propertyName").GetString());
        Assert.Equal("Hello Uno", edited.GetProperty("before").GetString());
        Assert.Equal("Changed through Properties", edited.GetProperty("after").GetString());

        var dirty = await _app.InvokeAsync("od.file.is-dirty", _unoPagePath);
        Assert.True(dirty.GetProperty("isDirty").GetBoolean(),
            "Editing through the Properties pad should dirty the designer document");

        // The re-render must reflect the new content: a longer label widens the button. Poll
        // for the measured bounds to change - "rendered" stays true across re-renders, so
        // waiting on it would race the async render that follows the source edit.
        var widened = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var d = await _app.InvokeAsync("od.winui-designer.describe-element", "PrimaryButton");
            return d.GetProperty("success").GetBoolean()
                && d.GetProperty("description").GetString() != beforeBounds;
        }, TimeSpan.FromSeconds(20), initialDelayMs: 100, maxDelayMs: 500);
        Assert.True(widened,
            "Expected the re-render to widen the button after the Properties pad edit");

        var saved = await _app.InvokeAsync("od.file.save", _unoPagePath);
        Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
        var savedXaml = await File.ReadAllTextAsync(_unoPagePath);
        Assert.Contains("Content=\"Changed through Properties\"", savedXaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// Technote acceptance item: invalid XAML must produce a diagnostic without taking the IDE
    /// down, and going back to valid XAML must recover the preview.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_InvalidXamlReportsDiagnosticThenRecovers()
    {
        await OpenUnoDesignerAsync();

        // Break the markup through the editor, the way a half-typed tag appears in real use.
        Assert.True((await _app.InvokeAsync("od.winui-designer.switch-to-source")).GetProperty("success").GetBoolean());
        await _app.InvokeAsync("od.file.edit-text", _unoPagePath, "<Bad");

        JsonElement broken = default;
        var reported = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            broken = await _app.InvokeAsync("od.winui-designer.status");
            return broken.TryGetProperty("active", out var active) && active.GetBoolean()
                && broken.GetProperty("documentError").ValueKind != JsonValueKind.Null;
        }, TimeSpan.FromSeconds(60), initialDelayMs: 100, maxDelayMs: 500);
        Assert.True(reported, "Expected malformed XAML to surface a diagnostic instead of a silent blank surface: " + broken);

        // The IDE is still answering, i.e. the bad document did not take the process down.
        Assert.True((await _app.InvokeAsync("od.addins")).TryGetProperty("addins", out _));

        Assert.True((await _app.InvokeAsync("od.winui-designer.switch-to-source")).GetProperty("success").GetBoolean());
        var repaired = await _app.InvokeAsync("od.search.replace", "<Bad", "", "solution");
        Assert.True(repaired.GetProperty("success").GetBoolean(), repaired.ToString());

        var recovered = await WaitForRenderedAsync();
        Assert.Null(recovered.GetProperty("documentError").GetString());
        Assert.Contains("Rendered by Uno design host", recovered.GetProperty("status").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Regression for a bug found after this session's WPF drag-drop fixes were confirmed
    /// unrelated: the design surface rendered a completely blank white canvas even though
    /// HasPresentedFrame/resolvedNameCount proved the visual tree genuinely existed. Root cause -
    /// ThemeManager.CurrentTheme is a process-wide static that every real ProGPU host (Samples,
    /// Samples.Uno, Samples.Avalonia) sets explicitly on startup; our host never did, so it stayed
    /// at the library's default of Dark, which styles a Button with a near-white translucent
    /// background and fully-opaque white foreground - invisible against this host's plain white
    /// WPF canvas. Asserts the rendered button now uses dark-on-light colors, i.e. actually visible.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_RendersButtonWithVisibleLightThemeColors()
    {
        await OpenUnoDesignerAsync();

        var described = await _app.InvokeAsync("od.winui-designer.describe-element", "PrimaryButton");
        Assert.True(described.GetProperty("success").GetBoolean(), described.ToString());
        var description = described.GetProperty("description").GetString();

        // The Uno design host renders the Fluent theme (light by default); the button must have
        // materialized with real layout bounds - an invisible/blank render reports zero size.
        Assert.Contains("type=Button", description, StringComparison.Ordinal);
        Assert.DoesNotContain("0x0", description, StringComparison.Ordinal);

        var status = await _app.InvokeAsync("od.winui-designer.status");
        Assert.True(status.GetProperty("rendered").GetBoolean(), status.ToString());
        Assert.Null(status.GetProperty("documentError").GetString());
    }

    /// <summary>
    /// Clicking the rendered design surface must select the corresponding element in the XAML
    /// *source* and populate the shared Properties pad - the same end state an Outline pick
    /// produces. Uses real synthetic pointer input at the element's actual on-screen position, so
    /// it exercises ProGPU hit testing and the visual-to-source name mapping, not a shortcut API.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_ClickOnDesignSurface_SelectsSourceElementInPropertiesPad()
    {
        await OpenUnoDesignerAsync();

        // PrimaryButton is declared in the sample page, so it exists in both the source document
        // and the rendered tree - exactly the correspondence this test is about.
        // OD_TEST_MODE=1 sets ShowActivated=false so a test run never steals focus, but the
        // synthetic pointer below is real OS-level input that only routes correctly when this
        // window is actually frontmost - the WPF designer's drag tests do the same thing.
        await _app.InvokeAsync("od.activate");

        var bounds = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", "PrimaryButton");
        Assert.True(bounds.GetProperty("success").GetBoolean(), bounds.ToString());
        Assert.True(bounds.GetProperty("width").GetDouble() > 0 && bounds.GetProperty("height").GetDouble() > 0,
            "Expected the rendered button to have a real arranged size: " + bounds);

        var x = bounds.GetProperty("centerX").GetDouble();
        var y = bounds.GetProperty("centerY").GetDouble();

        JsonElement status = default;
        var selected = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            // Retry the synthetic click until the surface receives it - LibreWPF's pointer
            // delivery occasionally drops the first attempt.
            await _app.PressPointerAsync(x, y);
            await _app.ReleasePointerAsync(x, y);
            status = await _app.InvokeAsync("od.winui-designer.status");
            return status.GetProperty("selectedName").ValueKind != JsonValueKind.Null
                && status.GetProperty("selectedName").GetString() == "PrimaryButton";
        }, TimeSpan.FromSeconds(20), initialDelayMs: 100, maxDelayMs: 500);
        Assert.True(selected, "Clicking the design surface should select PrimaryButton: " + status);

        // The click must land in the same place an Outline pick would: the shared Properties pad,
        // backed by the XAML source element.
        var reselect = await _app.InvokeAsync("od.winui-designer.select", "PrimaryButton");
        Assert.Equal(
            "ICSharpCode.WinUIXamlDesigner.WinUIXamlElementPropertyAdapter",
            reselect.GetProperty("propertyPadSelectedType").GetString());

        // And the selection is live: editing through the pad rewrites that element's source.
        var edited = await _app.InvokeAsync("od.winui-designer.properties-pad.edit", "Content", "Clicked");
        Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
        await _app.InvokeAsync("od.file.save-all");
        Assert.Contains("Clicked", await File.ReadAllTextAsync(_unoPagePath));
    }

    /// <summary>
    /// The Toolbox-to-design-surface path driven by a REAL synthetic mouse drag (press/drag-move/
    /// release), not the insert action: it exercises the pad's own DoDragDrop, the surface's drop
    /// handling, and resolving the drop point to the container the user aimed at.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer()
    {
        var status = await OpenUnoDesignerAsync();
        var namesBefore = status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()).ToList();

        // The shared ToolsPad only realizes its content the first time it is actually shown, so
        // without this the rows exist but have no containers to press on.
        await _app.InvokeAsync("od.show-pad", "Tools");
        await _app.InvokeAsync("od.activate");

        var toolboxBounds = await _app.InvokeAsync("od.winui-designer.toolbox.query-item-bounds", "TextBlock");
        Assert.True(toolboxBounds.GetProperty("success").GetBoolean(), toolboxBounds.ToString());

        // Drop onto PrimaryButton's position: it resolves to the nearest source-backed element,
        // proving the drop point - not a hardcoded root - decides where the control lands.
        var target = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", "PrimaryButton");
        Assert.True(target.GetProperty("success").GetBoolean(), target.ToString());

        var fromX = toolboxBounds.GetProperty("centerX").GetDouble();
        var fromY = toolboxBounds.GetProperty("centerY").GetDouble();
        var toX = target.GetProperty("centerX").GetDouble();
        var toY = target.GetProperty("centerY").GetDouble();

        await _app.PressPointerAsync(fromX, fromY);
        for (var step = 1; step <= 8; step++)
            await _app.DragMovePointerAsync(fromX + (toX - fromX) * step / 8.0, fromY + (toY - fromY) * step / 8.0);
        await _app.ReleasePointerAsync(toX, toY);

        JsonElement after = default;
        var inserted = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            after = await _app.InvokeAsync("od.winui-designer.status");
            return after.GetProperty("elementNames").EnumerateArray()
                .Select(n => n.GetString()).Count() > namesBefore.Count;
        }, TimeSpan.FromSeconds(30), initialDelayMs: 100, maxDelayMs: 500);
        Assert.True(inserted, "A real toolbox drag should have added an element: " + after);

        var newName = after.GetProperty("elementNames").EnumerateArray()
            .Select(n => n.GetString()).Except(namesBefore).Single();
        Assert.StartsWith("TextBlock", newName);

        // And it is a genuine, well-formed source edit - not a visual-tree-only insertion, and not
        // just "the string '<TextBlock' appears somewhere in the file" (a substring check like that
        // would pass even if the insert corrupted the surrounding markup, or landed as a dangling
        // top-level sibling rather than inside the dropped-onto container).
        await _app.InvokeAsync("od.file.save-all");
        var onDisk = await File.ReadAllTextAsync(_unoPagePath);
        XDocument document = null;
        try
        {
            document = XDocument.Parse(onDisk);
        }
        catch (System.Xml.XmlException exception)
        {
            Assert.Fail($"Drop produced malformed XAML: {exception.Message}\n{onDisk}");
        }

        var ns = document.Root!.GetDefaultNamespace();
        var xNs = (XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml";
        var newElementMatches = document.Descendants().Where(e => (string)e.Attribute(xNs + "Name") == newName).ToList();
        Assert.True(newElementMatches.Count == 1,
            $"Expected exactly one element named '{newName}' in the saved document, found {newElementMatches.Count}:\n{onDisk}");
        var newElement = newElementMatches[0];
        Assert.Equal(ns + "TextBlock", newElement.Name);

        // "Drop onto PrimaryButton's position resolves to the nearest source-backed element" (this
        // test's whole point, per its own doc comment) means the new element must land in
        // PrimaryButton's own container - as its sibling - not merely anywhere in the document.
        var primaryButton = document.Descendants().Single(e => (string)e.Attribute(xNs + "Name") == "PrimaryButton");
        Assert.Equal(primaryButton.Parent, newElement.Parent);
    }

    /// <summary>
    /// Covers the OTHER drag-drop target for a WinUI/Uno toolbox item: the plain XAML text/source
    /// editor (AvalonEditViewContent, the file's default/primary view), not the ProGPU design
    /// surface (WinUIXamlHost, a secondary view - see
    /// WinUIDesigner_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer above). Mirrors
    /// the WPF designer's own DragToolboxItem_OntoXamlSourceEditor_InsertsMarkupAtDropPoint.
    ///
    /// Until this test, AvalonEditViewContent.IToolsHost.ToolsContent returned the *WPF* toolbox
    /// unconditionally for every .xaml file - including WinUI/Uno ones - because it never checked
    /// XamlFrameworkDetector.Detect the way the Design-tab secondary view binding
    /// (WinUIXamlDesignerDisplayBinding.CanAttachTo) already does. The Source tab of a Uno document
    /// showed WPF-only controls in the Tools pad, and WinUIXamlToolbox's own drag payload only ever
    /// carried its ProGPU-canvas-specific data format - never the "ComponentTypeName" format
    /// AvalonEditViewContent.TextArea_Drop looks for - so a WinUI/Uno tool dropped onto the source
    /// editor silently did nothing.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_DragToolboxItemOntoXamlSourceEditor_InsertsMarkupAtDropPoint()
    {
        var originalXaml = await File.ReadAllTextAsync(_unoPagePath);

        try
        {
            await OpenUnoDesignerAsync();

            // Realize the WinUI/Uno toolbox rows once (query-item-bounds needs a container to
            // press on) - this also confirms the Tools pad resolves to the *WinUI* toolbox, not
            // WPF's, via the design surface, before the source-view assertion below.
            await _app.InvokeAsync("od.show-pad", "Tools");
            await _app.InvokeAsync("od.activate");
            var toolboxBounds = await _app.InvokeAsync("od.winui-designer.toolbox.query-item-bounds", "TextBox");
            Assert.True(toolboxBounds.GetProperty("success").GetBoolean(), toolboxBounds.ToString());

            // query-item-bounds activates the Design tab as a side effect (ActivateDesigner) -
            // switch back to Source, the actual drop target for this test.
            var switched = await _app.InvokeAsync("od.winui-designer.switch-to-source");
            Assert.True(switched.GetProperty("success").GetBoolean(), switched.ToString());

            // Target the position right before "<TextBlock" - a sibling position where a
            // self-closing "<TextBox />" is well-formed regardless of surrounding whitespace.
            // Anchoring on a narrow single character like "<StackPanel>"'s own closing '>' leaves
            // no pixel slack: a drop that resolves even one character early lands INSIDE that tag
            // instead of after it (exactly what a first attempt at this test caught: the result was
            // "<StackPanel<TextBox />>"). The run of leading-whitespace indentation before
            // "<TextBlock" is a much wider target, so the same sub-pixel imprecision still resolves
            // to the same text offset.
            int dropOffset = originalXaml.IndexOf("<TextBlock", StringComparison.Ordinal);
            Assert.True(dropOffset >= 0, "Expected MainPage.xaml fixture to contain a <TextBlock anchor.");

            // Park the caret away from the drop point, so a regression back to caret-based
            // insertion (the same class of bug the WPF test guards against) is caught below.
            var caretSet = await _app.InvokeAsync("od.file.set-caret-offset", _unoPagePath, 0);
            Assert.True(caretSet.GetProperty("success").GetBoolean(), caretSet.ToString());

            var fromX = toolboxBounds.GetProperty("centerX").GetDouble();
            var fromY = toolboxBounds.GetProperty("centerY").GetDouble();

            var dropPoint = await _app.InvokeAsync("od.file.query-offset-screen-position", _unoPagePath, dropOffset);
            Assert.True(dropPoint.GetProperty("success").GetBoolean(), dropPoint.ToString());
            // query-offset-screen-position returns the sub-pixel-exact boundary BEFORE the target
            // offset's character - GetDropOffset's hit test (TextView.GetPositionFloor) resolves a
            // click landing exactly on (or a hair before, once cliclick truncates to a whole pixel)
            // that boundary to the PREVIOUS character, one short of the intended offset. This
            // reproduced 100% of the time before the +2px bias below was added (e.g. inserting into
            // "<StackPanel>" itself: "<StackPanel<TextBox />>"). A synthetic drag is the only actor
            // that would ever aim at that exact razor's-edge pixel - nudge a couple of pixels into
            // the target character's own cell instead, which is what a real drop would land within.
            var toX = dropPoint.GetProperty("x").GetDouble() + 2;
            var toY = dropPoint.GetProperty("y").GetDouble();

            string savedXaml = null;
            var inserted = false;
            for (int attempt = 1; attempt <= 4 && !inserted; attempt++)
            {
                var pressed = await _app.PressPointerAsync(fromX, fromY);
                Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());

                for (int step = 1; step <= 6; step++)
                {
                    var t = step / 6.0;
                    var moved = await _app.DragMovePointerAsync(fromX + (toX - fromX) * t, fromY + (toY - fromY) * t);
                    Assert.True(moved.GetProperty("ok").GetBoolean(), moved.ToString());
                    await Task.Delay(150);
                }

                var released = await _app.ReleasePointerAsync(toX, toY);
                Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());

                inserted = await OpenDevelopAppFixture.PollUntilAsync(async () =>
                {
                    var saved = await _app.InvokeAsync("od.file.save", _unoPagePath);
                    Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
                    savedXaml = await File.ReadAllTextAsync(_unoPagePath);
                    return savedXaml.Contains("<TextBox />", StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(8), initialDelayMs: 50, maxDelayMs: 250);
            }

            // The control, its markup, and the intended drop offset are all known ahead of time -
            // so assert the exact resulting document, not just "a <TextBox /> landed somewhere
            // plausible". A real synthetic mouse drag's screen-position -> text-offset hit test has
            // an inherent +/-1 character jitter around the intended column (confirmed empirically:
            // repeated runs of this exact test landed one character early - "<StackPanel<TextBox
            // />>", clipping the tag itself - and one character late - an extra space before
            // "<TextBox />" - never elsewhere), so pin down the landing spot with a tolerance
            // instead of a single fixed offset.
            int insertedAt = savedXaml.IndexOf("<TextBox />", StringComparison.Ordinal);
            Assert.True(insertedAt >= 0, "Expected the drop to have inserted <TextBox />:\n" + savedXaml);
            // Removing the inserted markup from wherever it actually landed must reproduce the
            // ORIGINAL document byte-for-byte - proving the drop touched nothing else, i.e. it did
            // not corrupt unrelated text elsewhere in the file. NOTE this alone is NOT sufficient:
            // it also holds for "<StackPanel<TextBox />>" (removing "<TextBox />" reconstructs
            // "<StackPanel>" exactly), which is genuinely malformed XML - the insertion landed
            // INSIDE the "<StackPanel>" tag's own brackets rather than after them. The XDocument.
            // Parse check below is what actually catches that.
            var withoutInsertion = savedXaml.Remove(insertedAt, "<TextBox />".Length);
            Assert.Equal(originalXaml, withoutInsertion);
            // And it landed at the intended drop site (within the hit-test's inherent +/-1
            // character jitter - confirmed empirically across repeated runs of this exact test),
            // not e.g. at the caret (offset 0, deliberately parked elsewhere above) or the end of
            // the document.
            Assert.InRange(insertedAt, dropOffset - 2, dropOffset + 2);
            // The two checks above can both pass on a drop that landed mid-tag - only parsing
            // proves the result is still well-formed XAML.
            try
            {
                XDocument.Parse(savedXaml);
            }
            catch (System.Xml.XmlException exception)
            {
                Assert.Fail($"Drop produced malformed XAML: {exception.Message}\n{savedXaml}");
            }
        }
        finally
        {
            // MainPage.xaml is a repository fixture - restore it regardless of outcome.
            await File.WriteAllTextAsync(_unoPagePath, originalXaml);
        }
    }

    /// <summary>
    /// Technote acceptance item: unloading the document must release the designer's runtime.
    /// Each open builds a collectible preview assembly and a WinUI tree from it, so a host that
    /// outlives its document would leak an entire ALC per open - invisible to every other test.
    /// </summary>
    [Fact]
    public async Task WinUIDesigner_ClosingDocument_ReleasesRuntimeHostAndPreviewAssembly()
    {
        await OpenUnoDesignerAsync();

        var open = await _app.InvokeAsync("od.winui-designer.runtime-stats");
        Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());
        Assert.True(open.GetProperty("childAlive").GetBoolean(),
            "Expected the Uno design host child to be alive while the document is open: " + open);

        Assert.True((await _app.InvokeAsync("od.close-active-view")).GetProperty("success").GetBoolean());

        JsonElement closed = default;
        var released = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            // The out-of-process host's lifecycle contract: closing the document disposes the
            // runtime host, which kills the child process.
            closed = await _app.InvokeAsync("od.winui-designer.runtime-stats");
            return closed.GetProperty("success").GetBoolean()
                && !closed.GetProperty("childAlive").GetBoolean()
                && closed.GetProperty("liveHosts").GetInt32() == 0;
        }, TimeSpan.FromSeconds(30), initialDelayMs: 200, maxDelayMs: 1000);

        Assert.True(released,
            "Closing the document must dispose the designer host and let its collectible preview "
            + "assembly be collected: " + closed);

        // Reopening still works after a full release - i.e. teardown did not corrupt shared state.
        await OpenUnoDesignerAsync();
    }

    async Task<JsonElement> OpenUnoDesignerAsync()
    {
        var openedSolution = await _app.ReopenSolutionAsync(_unoSolutionPath);
        Assert.True(openedSolution.GetProperty("success").GetBoolean(), openedSolution.ToString());
        var opened = await _app.InvokeAsync("od.open-file", _unoPagePath);
        Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
        var status = await WaitForRenderedAsync();
        Assert.Equal("Uno", status.GetProperty("framework").GetString());
        return status;
    }

    async Task<JsonElement> WaitForRenderedAsync()
    {
        JsonElement status = default;
        // Every edit recompiles the document through Roslyn and reloads a collectible preview
        // assembly before ProGPU presents a frame, so "rendered" is the only safe gate.
        var ready = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.winui-designer.status");
            return status.TryGetProperty("active", out var active) && active.GetBoolean()
                && status.GetProperty("rendered").GetBoolean();
        }, TimeSpan.FromSeconds(60), initialDelayMs: 100, maxDelayMs: 500);
        Assert.True(ready, status.ToString());
        return status;
    }

    [Fact]
    public async Task OpenAppXaml_ShowsCodeEditorOutline()
    {
        // App.xaml uses <Application> as root, which the WPF designer's secondary binding
        // explicitly excludes (CanAttachTo returns false for "Application"), so only the text
        // editor opens. The XamlBinding addin's XamlOutlineContentHost registers itself on the
        // TextView services via XamlTextEditorExtension.Attach, making the OutlinePad show a
        // XAML element tree instead of the designer's IOutlineNode tree.
        await _app.EnsureSolutionOpenAsync(_app.WpfSampleSolutionPath);

        var appXamlPath = Path.Combine(Path.GetDirectoryName(_app.WpfSampleSolutionPath)!, "App.xaml");
        var openFileResult = await _app.InvokeAsync("od.open-file", appXamlPath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean(), $"Failed to open {appXamlPath}");

        var status = await WaitForXamlOutlineStatusAsync(expectedRootName: "App.xaml", timeoutSeconds: 30, reactivatePath: appXamlPath);

        Assert.True(status.GetProperty("active").GetBoolean(),
            "Expected the XAML code editor outline to be active for App.xaml (text editor, not designer)");

        var rootName = status.GetProperty("rootName").GetString();
        Assert.Equal("App.xaml", rootName);

        var outlineNames = status.GetProperty("outlineNames").EnumerateArray()
            .Select(n => n.GetString())
            .ToList();

        Assert.True(status.GetProperty("rootChildCount").GetInt32() > 0,
            "Expected the root node to have at least one child element");
        Assert.Contains("Application", outlineNames);
        Assert.Contains("Application.Resources", outlineNames);
    }

    [Fact]
    public async Task OpenSamplePaneXaml_LoadsDesignerWithNestedControlTree()
    {
        // SamplePane.xaml is a UserControl with deeply nested named elements (Border→StackPanel→
        // TextBlocks, ListBox). The designer's Outline pad should reflect the full hierarchy.
        await _app.EnsureSolutionOpenAsync(_app.WpfSampleSolutionPath);

        var xamlPath = Path.Combine(Path.GetDirectoryName(_app.WpfSampleSolutionPath)!, "SamplePane.xaml");
        var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean(), $"Failed to open {xamlPath}");

        var status = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "UserControl", timeoutSeconds: 30, reactivatePath: xamlPath);

        Assert.True(status.GetProperty("active").GetBoolean());
        Assert.True(status.GetProperty("designerLoaded").GetBoolean(),
            "Expected the WPF design surface to load SamplePane.xaml");
        Assert.Equal("UserControl", status.GetProperty("rootItemType").GetString());

        var outlineNames = status.GetProperty("outlineNames").EnumerateArray()
            .Select(n => n.GetString())
            .ToList();

        Assert.True(status.GetProperty("outlineChildCount").GetInt32() > 0);
        Assert.Contains("PaneBorder", outlineNames);
        Assert.Contains("PaneStack", outlineNames);
        Assert.Contains("PaneTitle", outlineNames);
        Assert.Contains("PaneBody", outlineNames);
        Assert.Contains("PaneList", outlineNames);
        Assert.Contains("PaneListItemOne", outlineNames);
        Assert.Contains("PaneListItemTwo", outlineNames);
    }

    [Fact]
    public async Task SelectControl_EditingContentInPropertiesPad_UpdatesAndSavesXaml()
    {
        var solutionDirectory = Path.GetDirectoryName(_app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(solutionDirectory, "MainWindow.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);

        try
        {
            var openSolutionResult = await _app.ReopenSolutionAsync(_app.WpfSampleSolutionPath);
            Assert.True(openSolutionResult.GetProperty("success").GetBoolean());
            var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
            Assert.True(openFileResult.GetProperty("opened").GetBoolean());
            var status = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "Window", timeoutSeconds: 30, reactivatePath: xamlPath);
            Assert.True(status.GetProperty("designerLoaded").GetBoolean(), status.ToString());
            Assert.Contains(status.GetProperty("outlineNames").EnumerateArray(),
                name => name.GetString() == "PrimaryButton");

            // The async project-preferences restore can re-activate a previously-open document
            // between the last WaitFor poll and this select (same race the WaitFor helpers guard
            // against) - retry the select, re-activating MainWindow each round.
            JsonElement selected = default;
            await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                selected = await _app.InvokeAsync("od.wpf-designer.select", "PrimaryButton");
                if (selected.GetProperty("success").GetBoolean())
                    return true;
                await _app.InvokeAsync("od.open-file", xamlPath);
                return false;
            }, TimeSpan.FromSeconds(15), initialDelayMs: 50, maxDelayMs: 250);
            Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
            Assert.Equal("PrimaryButton", selected.GetProperty("selectedName").GetString());
            Assert.Equal("PrimaryButton", selected.GetProperty("propertiesPadSelectedName").GetString());
            Assert.Equal("Button", selected.GetProperty("propertiesPadSelectedType").GetString());

            // This action operates on the Content PropertyItem generated by the actual Xceed
            // PropertyGrid in the Properties pad. It intentionally cannot access DesignItem.Properties.
            var edited = await WaitForPropertiesPadEditAsync("Content", "Changed through Properties", timeoutSeconds: 10);
            Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
            Assert.Equal("PrimaryButton", edited.GetProperty("selectedName").GetString());
            Assert.Equal("Content", edited.GetProperty("propertyName").GetString());
            Assert.Equal("Xceed.Wpf.Toolkit.PropertyGrid.PropertyItem",
                edited.GetProperty("propertyItemType").GetString());
            Assert.Equal("Button", edited.GetProperty("before").GetString());
            Assert.Equal("Changed through Properties", edited.GetProperty("after").GetString());

            var dirty = await _app.InvokeAsync("od.file.is-dirty", xamlPath);
            Assert.True(dirty.GetProperty("isDirty").GetBoolean(),
                "Editing through the Properties pad should dirty the designer document");

            var saved = await _app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
            Assert.False(saved.GetProperty("isDirty").GetBoolean());

            var savedXaml = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("Content=\"Changed through Properties\"", savedXaml);
        }
        finally
        {
            // The vscode-wpf sample is a repository fixture, so a successful save must not leave
            // the developer's checkout modified after this integration test.
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task DragToolboxItem_OntoDesignSurface_InsertsControlEditableThroughPropertiesPad()
    {
        // Covers the toolbox -> design surface -> properties pad path end to end using a REAL
        // synthetic mouse drag (press/drag-move/release via cliclick, same primitives AvalonDock's
        // own DevFlowClient uses), not an API shortcut: PortableDragDropOperation (LibreWPF's
        // PresentationCore) now implements the source half of DragDrop.DoDragDrop for portable
        // presentation sources, so WpfToolbox's actual DragDrop.DoDragDrop call - previously a
        // guaranteed no-op off Windows - drives CreateComponentTool's real DragOver/Drop handlers.
        var solutionDirectory = Path.GetDirectoryName(_app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(solutionDirectory, "SamplePane.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);

        try
        {
            var openSolutionResult = await _app.ReopenSolutionAsync(_app.WpfSampleSolutionPath);
            Assert.True(openSolutionResult.GetProperty("success").GetBoolean());
            var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
            Assert.True(openFileResult.GetProperty("opened").GetBoolean());
            var status = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "UserControl", timeoutSeconds: 30, reactivatePath: xamlPath);
            Assert.True(status.GetProperty("designerLoaded").GetBoolean(), status.ToString());
            var outlineNamesBefore = status.GetProperty("outlineNames").EnumerateArray()
                .Select(n => n.GetString()).ToArray();
            Assert.Contains("PaneStack", outlineNamesBefore);

            // The generic ToolsPad ("SideBar" - hosts WpfViewContent.ToolsContent, i.e.
            // WpfToolbox.Instance.ToolboxControl) only realizes its content the first time it's
            // actually shown (see od.show-pad's own doc comment) - without this, the ListBox's
            // items exist (Items.Count is already non-zero) but have no generated containers, so
            // ItemContainerGenerator.ContainerFromItem returns null and there's nothing to press on.
            await _app.InvokeAsync("od.show-pad", "Tools");

            // OD_TEST_MODE=1 sets ShowActivated=false so a normal test run never steals focus
            // from the developer's foreground app - but cliclick's synthetic mouse input is real
            // OS-level input that needs this window to actually be frontmost/focused to route
            // correctly. See od.activate's doc comment.
            await _app.InvokeAsync("od.activate");

            var toolboxBounds = await _app.InvokeAsync("od.wpf-designer.toolbox.query-item-bounds", "TextBox");
            Assert.True(toolboxBounds.GetProperty("success").GetBoolean(), toolboxBounds.ToString());
            var fromX = toolboxBounds.GetProperty("centerX").GetDouble();
            var fromY = toolboxBounds.GetProperty("centerY").GetDouble();

            var targetBounds = await _app.InvokeAsync("od.wpf-designer.query-element-screen-bounds", "PaneStack");
            Assert.True(targetBounds.GetProperty("success").GetBoolean(), targetBounds.ToString());
            // Drop near the bottom of PaneStack's empty space below its existing children (a
            // StackPanel's own bounds still hit-test to itself past its children's combined
            // height), not dead center, which could land on an existing child TextBlock/ListBox
            // instead - CreateComponentTool.GetCurrentTarget only accepts a hit that resolves
            // directly to an AllowDrop element, with no ancestor walk (see PortableDragDropOperation's
            // ResolveDropTarget comment), so hitting a child instead of the panel itself would fail.
            var toX = targetBounds.GetProperty("x").GetDouble() + targetBounds.GetProperty("width").GetDouble() / 2;
            var toY = targetBounds.GetProperty("y").GetDouble() + targetBounds.GetProperty("height").GetDouble() - 4;

            // The exact drop point can land on a WpfDesign adorner instead of the real target
            // (PortableDragDropOperation.ResolveDropTarget's comment explains why - e.g.
            // PanelMoveAdorner, a small localized move-handle rather than a full-panel overlay,
            // ends up AllowDrop=true via a generic shared style and swallows the drop silently
            // if the release happens to land on it), which is timing/rendering-position
            // sensitive rather than deterministic - retry the whole press/move/release gesture
            // rather than fine-tune a single "safe" coordinate that isn't guaranteed safe anyway.
            JsonElement statusAfterDrop = default;
            var outlineGrew = false;
            for (int attempt = 1; attempt <= 4 && !outlineGrew; attempt++)
            {
                var pressed = await _app.PressPointerAsync(fromX, fromY);
                Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());

                // Several intermediate steps: WpfToolbox.OnPreviewMouseMove starts DragDrop.DoDragDrop
                // on the very first move while the item is selected+pressed, and PortableDragDropOperation
                // hit-tests on every move - one big jump would still work, but stepping mirrors an
                // actual drag gesture and gives DragEnter/DragOver a chance to run more than once.
                for (int step = 1; step <= 6; step++)
                {
                    var t = step / 6.0;
                    var moved = await _app.DragMovePointerAsync(fromX + (toX - fromX) * t, fromY + (toY - fromY) * t);
                    Assert.True(moved.GetProperty("ok").GetBoolean(), moved.ToString());
                    await Task.Delay(150);
                }

                var released = await _app.ReleasePointerAsync(toX, toY);
                Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());

                // Confirm the dropped control actually landed in the live designer tree (outline) -
                // it has no x:Name (nothing names a freshly-dropped item, mouse-driven or not), so
                // identify it by the outline count growing rather than by name.
                outlineGrew = await OpenDevelopAppFixture.PollUntilAsync(async () =>
                {
                    statusAfterDrop = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "UserControl", timeoutSeconds: 10, reactivatePath: xamlPath);
                    return statusAfterDrop.GetProperty("outlineNames").GetArrayLength() > outlineNamesBefore.Length;
                }, TimeSpan.FromSeconds(8), initialDelayMs: 50, maxDelayMs: 250);
            }
            Assert.True(outlineGrew,
                "Expected a new element in the outline after the drag-drop, even after retries.\nBefore: " + string.Join(", ", outlineNamesBefore) +
                "\nAfter: " + statusAfterDrop);

            // AddItemsWithCustomSize (the primitive CreateComponentTool's real drag/drop path calls
            // internally, same as a plain click-to-place) already selects the newly created item,
            // so the Properties pad should already be showing it - no explicit select needed.
            var edited = await WaitForPropertiesPadEditAsync("Text", "Dropped via DevFlow", timeoutSeconds: 10);
            Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
            Assert.Equal("Dropped via DevFlow", edited.GetProperty("after").GetString());

            // The real drag-drop path (unlike a plain property edit alone) can leave a ChangeGroup
            // open on the undo transaction stack - see WpfDesignDevFlowActions.FlushPendingTransaction's
            // doc comment. An automatic fix (flushing on DragDrop.DropEvent) was attempted but that
            // handler never actually fires, so this explicit call is a known, currently-necessary
            // workaround, not just a test convenience - a real end user dragging with the mouse
            // hits the same "control doesn't visibly persist immediately" gap today.
            var flushed = await _app.InvokeAsync("od.wpf-designer.flush-pending-transaction");
            Assert.True(flushed.GetProperty("success").GetBoolean(), flushed.ToString());
            var saved = await _app.InvokeAsync("od.file.save", xamlPath);
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());

            var savedXaml = await File.ReadAllTextAsync(xamlPath);
            Assert.Contains("<TextBox", savedXaml, StringComparison.Ordinal);
            Assert.Contains("Text=\"Dropped via DevFlow\"", savedXaml, StringComparison.Ordinal);
        }
        finally
        {
            // SamplePane.xaml is a repository fixture - restore it regardless of outcome.
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task DragToolboxItem_OntoXamlSourceEditor_InsertsMarkupAtDropPoint()
    {
        // Covers the OTHER drag-drop target for a WPF toolbox item: the plain XAML text/source
        // editor (AvalonEditViewContent, the file's default/primary view), not the WpfDesign
        // canvas (WpfViewContent, a secondary view - see DragToolboxItem_OntoDesignSurface_...
        // above). AvalonEditViewContent.TextArea_Drop inserts "<TagName />" at the position the
        // pointer was actually released over. It used to insert at the editor's CURRENT caret
        // instead, which is wherever the user last clicked or typed - so markup landed several
        // lines away from the mouse unless the caret happened to already be there. This test
        // therefore drops on the exact screen position of a chosen offset
        // (od.file.query-offset-screen-position) and asserts the markup landed at that offset,
        // rather than parking the caret somewhere and dropping anywhere.
        // Unlike the canvas test, there's no WpfDesign adorner/DesignPanel hit-testing involved
        // here - but the synthetic press/drag-move/release gesture itself (cliclick +
        // NativeInputPump, see PortableDragDropOperation's doc comment) is still occasionally
        // flaky end to end, same as the canvas test - retry the whole gesture rather than assume
        // a single attempt is reliable.
        var solutionDirectory = Path.GetDirectoryName(_app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(solutionDirectory, "SamplePane.xaml");
        var originalXaml = await File.ReadAllTextAsync(xamlPath);

        try
        {
            var openSolutionResult = await _app.ReopenSolutionAsync(_app.WpfSampleSolutionPath);
            Assert.True(openSolutionResult.GetProperty("success").GetBoolean());
            var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
            Assert.True(openFileResult.GetProperty("opened").GetBoolean());

            // Target the position right after "</ListBox>" - a sibling position inside PaneStack
            // where a self-closing "<TextBox />" is well-formed regardless of surrounding
            // whitespace. This is the DROP point, resolved to real screen coordinates below.
            int anchor = originalXaml.IndexOf("</ListBox>", StringComparison.Ordinal);
            Assert.True(anchor >= 0, "Expected SamplePane.xaml fixture to contain a </ListBox> anchor.");
            int dropOffset = anchor + "</ListBox>".Length;

            // Park the caret at the very start of the document - deliberately NOT the drop point.
            // Two jobs: it activates this file's text view (the designer-status assertion below
            // depends on that same view transition having happened, as it did when this test
            // still drove the insert through the caret), and it makes the assertion at the end
            // meaningful - if the insert ever regressed to using the caret again, the markup would
            // land at offset 0 instead of at the pointer, and the assertion would catch it.
            var caretSet = await _app.InvokeAsync("od.file.set-caret-offset", xamlPath, 0);
            Assert.True(caretSet.GetProperty("success").GetBoolean(), caretSet.ToString());

            // Exercise the secondary-view transition that previously left AvalonEdit's TextArea
            // detached when the source tab was selected again.
            var designerStatus = await _app.InvokeAsync("od.wpf-designer.status");
            Assert.True(designerStatus.GetProperty("designerLoaded").GetBoolean(), designerStatus.ToString());

            // The generic ToolsPad only realizes its content the first time it's shown - see the
            // canvas drag test's own comment on od.show-pad for why this call is required first.
            await _app.InvokeAsync("od.show-pad", "Tools");
            await _app.InvokeAsync("od.activate");

            // AvalonEditViewContent.IToolsHost.ToolsContent resolves to the same WpfToolbox.Instance
            // singleton, without changing the active secondary view again.
            var toolboxBounds = await _app.InvokeAsync("od.wpf-toolbox.query-item-bounds", "TextBox");
            Assert.True(toolboxBounds.GetProperty("success").GetBoolean(), toolboxBounds.ToString());
            var fromX = toolboxBounds.GetProperty("centerX").GetDouble();
            var fromY = toolboxBounds.GetProperty("centerY").GetDouble();

            // Drop on the target offset's own screen position, so the assertion below is a real
            // check that the insert followed the mouse rather than landing there by accident.
            var dropPoint = await _app.InvokeAsync("od.file.query-offset-screen-position", xamlPath, dropOffset);
            Assert.True(dropPoint.GetProperty("success").GetBoolean(), dropPoint.ToString());
            // query-offset-screen-position returns the sub-pixel-exact boundary BEFORE the target
            // offset's character - GetDropOffset's hit test (TextView.GetPositionFloor) resolves a
            // click landing exactly on (or a hair before, once cliclick truncates to a whole pixel)
            // that boundary to the PREVIOUS character, one short of the intended offset. This
            // reproduced 100% of the time before the +2px bias below was added (e.g. inserting into
            // "</ListBox>" itself: "</ListBox<TextBox />>"). A synthetic drag is the only actor that
            // would ever aim at that exact razor's-edge pixel - nudge a couple of pixels into the
            // target character's own cell instead, which is what a real drop would land within.
            var toX = dropPoint.GetProperty("x").GetDouble() + 2;
            var toY = dropPoint.GetProperty("y").GetDouble();

            string savedXaml = null;
            var inserted = false;
            for (int attempt = 1; attempt <= 4 && !inserted; attempt++)
            {
                var pressed = await _app.PressPointerAsync(fromX, fromY);
                Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());

                for (int step = 1; step <= 6; step++)
                {
                    var t = step / 6.0;
                    var moved = await _app.DragMovePointerAsync(fromX + (toX - fromX) * t, fromY + (toY - fromY) * t);
                    Assert.True(moved.GetProperty("ok").GetBoolean(), moved.ToString());
                    await Task.Delay(150);
                }

                var released = await _app.ReleasePointerAsync(toX, toY);
                Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());

                // TextArea_Drop inserts synchronously on the UI thread, but that thread is still
                // draining the synthetic input NativeInputPump pumped during the drag (see
                // PortableDragDropOperation's doc comment) - poll save+read rather than assume the
                // insert has already landed the instant ReleasePointerAsync's HTTP call returns.
                inserted = await OpenDevelopAppFixture.PollUntilAsync(async () =>
                {
                    var saved = await _app.InvokeAsync("od.file.save", xamlPath);
                    Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
                    savedXaml = await File.ReadAllTextAsync(xamlPath);
                    return savedXaml.Contains("<TextBox />", StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(8), initialDelayMs: 50, maxDelayMs: 250);
            }

            // The control, its markup, and the intended drop offset are all known ahead of time -
            // so assert the exact resulting document, not just "a <TextBox /> landed somewhere
            // plausible". A real synthetic mouse drag's screen-position -> text-offset hit test has
            // an inherent +/-1 character jitter around the intended column (confirmed empirically
            // on the WinUI designer's equivalent test - repeated runs landed one character early,
            // clipping into a neighboring tag, and one character late, adding an extra space -
            // never elsewhere), so pin down the landing spot with a tolerance instead of a single
            // fixed offset.
            int insertedAt = savedXaml.IndexOf("<TextBox />", StringComparison.Ordinal);
            Assert.True(insertedAt >= 0, "Expected the drop to have inserted <TextBox />:\n" + savedXaml);
            // Removing the inserted markup from wherever it actually landed must reproduce the
            // ORIGINAL document byte-for-byte - proving the drop touched nothing else, i.e. it did
            // not corrupt unrelated text elsewhere in the file. NOTE this alone is NOT sufficient:
            // it also holds for a mid-tag split like "<StackPanel<TextBox />>" (removing "<TextBox
            // />" reconstructs "<StackPanel>" exactly), which is genuinely malformed XML. The
            // XDocument.Parse check below is what actually catches that.
            var withoutInsertion = savedXaml.Remove(insertedAt, "<TextBox />".Length);
            Assert.Equal(originalXaml, withoutInsertion);
            // And it landed at the intended drop site (within the hit-test's inherent jitter), not
            // e.g. at the caret (offset 0, which was deliberately parked elsewhere above) or at the
            // end of the document.
            Assert.InRange(insertedAt, dropOffset - 2, dropOffset + 2);
            // The two checks above can both pass on a drop that landed mid-tag - only parsing
            // proves the result is still well-formed XAML.
            try
            {
                XDocument.Parse(savedXaml);
            }
            catch (System.Xml.XmlException exception)
            {
                Assert.Fail($"Drop produced malformed XAML: {exception.Message}\n{savedXaml}");
            }
        }
        finally
        {
            // SamplePane.xaml is a repository fixture - restore it regardless of outcome.
            await File.WriteAllTextAsync(xamlPath, originalXaml);
        }
    }

    [Fact]
    public async Task DragToolboxItem_OntoWinFormsDesignSurface_AddsControlToForm()
    {
        // Covers the THIRD drag-drop target for the shared WPF-hosted toolbox: a WinForms
        // DesignSurface (FormsDesignerViewContent, hosted via a WindowsFormsHost inside the
        // otherwise-all-WPF workbench). WinForms designer historically had no visible drag-from
        // palette in this port (FormsDesignerViewContent.ToolsContent used to hardcode null - see
        // its own doc comment) - it's now wired to the SAME WpfToolbox.Instance singleton used by
        // the XAML designer/editor, with WinForms items routed through the real
        // System.Drawing.Design.IToolboxService (ToolboxService.cs) rather than WpfDesign's
        // CreateComponentTool, since it's WinForms' real ParentControlDesigner.OnDragEnter/
        // OnDragDrop that actually creates the component on drop. Crossing from the WPF-hosted
        // toolbox ListBox into the embedded WinForms control tree relies on LibreWinForms'
        // WindowsFormsHost.ProcessExternalDragEvent bridge (see WpfToolbox.OnPreviewMouseMove's
        // own doc comment on the DataObject format-name contract that makes this work).
        var solutionDirectory = Path.GetDirectoryName(_app.WinFormsSampleSolutionPath)!;
        var formCodePath = Path.Combine(solutionDirectory, "Form1.Designer.cs");
        var originalFormCode = await File.ReadAllTextAsync(formCodePath);

        try
        {
            var openSolutionResult = await _app.ReopenSolutionAsync(_app.WinFormsSampleSolutionPath);
            Assert.True(openSolutionResult.GetProperty("success").GetBoolean());
            var openFileResult = await _app.InvokeAsync("od.open-file", formCodePath.Replace(".Designer.cs", ".cs"));
            Assert.True(openFileResult.GetProperty("opened").GetBoolean());

            var status = await _app.InvokeAsync("od.forms-designer.status");
            Assert.True(status.GetProperty("designerLoaded").GetBoolean(), status.ToString());
            Assert.False(status.GetProperty("usesCodeDomLoader").GetBoolean(), status.ToString());
            Assert.Contains("RoslynDesignerLoader", status.GetProperty("loaderType").GetString(), StringComparison.Ordinal);
            var controlNamesBefore = status.GetProperty("controlNames").EnumerateArray()
                .Select(n => n.GetString()).ToArray();
            Assert.Contains("dropPanel", controlNamesBefore);

            // WpfToolbox.Instance (which registers ISharedToolboxHost into SD.Services - see its
            // own doc comment) is a lazily-constructed singleton that, in every OTHER drag-drop
            // test, already exists by this point because some .xaml file's WpfViewContent/
            // AvalonEditViewContent touched it earlier. This test never opens a .xaml file, so
            // nothing has constructed it yet - if od.show-pad ran first, ToolsPad would bind to
            // FormsDesignerViewContent.ToolsContent while ISharedToolboxHost is still unregistered
            // (null), latch onto that null content, and never re-query it once the toolbox singleton
            // shows up afterward. Force construction (ignoring the expected "not realized" failure -
            // nothing has been shown yet) before showing the pad, so ToolsPad's first real bind sees
            // the actual toolbox.
            await _app.InvokeAsync("od.wpf-toolbox.query-item-bounds", "NumericUpDown");

            // Same reasoning as the XAML source-editor test's own comment: the generic ToolsPad
            // only realizes its content the first time it's shown.
            await _app.InvokeAsync("od.show-pad", "Tools");
            await _app.InvokeAsync("od.activate");

            // Query the drop target's bounds BEFORE the toolbox row's bounds, not after:
            // od.forms-designer.query-control-screen-bounds switches the active tab to the
            // FormsDesigner view (FindFormsDesignerViewContent's own SwitchView call), which
            // re-hosts ToolsPad's content and resets the toolbox ListBox's scroll offset back to
            // the top - querying the toolbox row afterward would return coordinates for whatever
            // row now occupies that position post-reset, not NumericUpDown. Querying the toolbox
            // row LAST, immediately before pressing, avoids that race.
            var targetBounds = await _app.InvokeAsync("od.forms-designer.query-control-screen-bounds", "dropPanel");
            Assert.True(targetBounds.GetProperty("success").GetBoolean(), targetBounds.ToString());
            var toX = targetBounds.GetProperty("x").GetDouble() + targetBounds.GetProperty("width").GetDouble() / 2;
            var toY = targetBounds.GetProperty("y").GetDouble() + targetBounds.GetProperty("height").GetDouble() / 2;

            // NumericUpDown has no WPF counterpart in the toolbox's "Windows Presentation
            // Foundation" category, so this DisplayName lookup can't accidentally match the wrong
            // framework's control (both categories share one flat toolbox ListBox).
            var toolboxBounds = await _app.InvokeAsync("od.wpf-toolbox.query-item-bounds", "NumericUpDown");
            Assert.True(toolboxBounds.GetProperty("success").GetBoolean(), toolboxBounds.ToString());
            var fromX = toolboxBounds.GetProperty("centerX").GetDouble();
            var fromY = toolboxBounds.GetProperty("centerY").GetDouble();

            // Same retry rationale as both other drag-drop tests: the synthetic press/drag-move/
            // release gesture (cliclick + NativeInputPump) is occasionally flaky end to end.
            JsonElement statusAfterDrop = default;
            var controlAdded = false;
            for (int attempt = 1; attempt <= 4 && !controlAdded; attempt++)
            {
                var pressed = await _app.PressPointerAsync(fromX, fromY);
                Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());

                for (int step = 1; step <= 6; step++)
                {
                    var t = step / 6.0;
                    var moved = await _app.DragMovePointerAsync(fromX + (toX - fromX) * t, fromY + (toY - fromY) * t);
                    Assert.True(moved.GetProperty("ok").GetBoolean(), moved.ToString());
                    await Task.Delay(150);
                }

                var released = await _app.ReleasePointerAsync(toX, toY);
                Assert.True(released.GetProperty("ok").GetBoolean(), released.ToString());

                controlAdded = await OpenDevelopAppFixture.PollUntilAsync(async () =>
                {
                    statusAfterDrop = await _app.InvokeAsync("od.forms-designer.status");
                    return statusAfterDrop.GetProperty("controlNames").GetArrayLength() > controlNamesBefore.Length;
                }, TimeSpan.FromSeconds(8), initialDelayMs: 50, maxDelayMs: 250);
            }
            Assert.True(controlAdded,
                "Expected a new control on the WinForms design surface after the drag-drop, even after retries.\nBefore: " + string.Join(", ", controlNamesBefore) +
                "\nAfter: " + statusAfterDrop);

            var saved = await _app.InvokeAsync("od.file.save", formCodePath.Replace(".Designer.cs", ".cs"));
            Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());

            var savedFormCode = await File.ReadAllTextAsync(formCodePath);
            Assert.Contains("System.Windows.Forms.NumericUpDown", savedFormCode, StringComparison.Ordinal);
            Assert.DoesNotContain("this.", savedFormCode, StringComparison.Ordinal);
            Assert.Contains("#region Windows Form Designer generated code", savedFormCode, StringComparison.Ordinal);
            Assert.Contains("Required designer variable.", savedFormCode, StringComparison.Ordinal);

            // The dropped control must also come out with a real, non-empty Size. A control created
            // straight from ToolboxItem.CreateComponents (rather than through the designer's own
            // IToolboxUser.ToolPicked path - see AbstractCodeDomDesignerLoader's own comment) keeps
            // Size.Empty, which still serializes to a perfectly valid-looking .Designer.cs but never
            // paints, because WindowsFormsHost.RenderControl skips zero-sized controls. Asserting on
            // the type name alone happily passed while the designer surface showed nothing at all.
            var sizeMatch = System.Text.RegularExpressions.Regex.Match(
                savedFormCode, @"numericUpDown1\.Size\s*=\s*new System\.Drawing\.Size\((\d+),\s*(\d+)\)");
            Assert.True(sizeMatch.Success,
                "Expected the dropped NumericUpDown to have a Size assignment in the generated designer code.\n" + savedFormCode);
            Assert.Equal(120, int.Parse(sizeMatch.Groups[1].Value));
            Assert.Equal(20, int.Parse(sizeMatch.Groups[2].Value));

            // Selecting a WinForms toolbox row without dragging must not arm a persistent creation
            // tool. The shared toolbox used to leave IToolboxService.SelectedToolboxItem set, so
            // the next ordinary canvas click created a second control with no visible way to
            // cancel the stale tool.
            var toolboxBoundsAfterDrop = await _app.InvokeAsync("od.wpf-toolbox.query-item-bounds", "NumericUpDown");
            Assert.True(toolboxBoundsAfterDrop.GetProperty("success").GetBoolean(), toolboxBoundsAfterDrop.ToString());
            var selectX = toolboxBoundsAfterDrop.GetProperty("centerX").GetDouble();
            var selectY = toolboxBoundsAfterDrop.GetProperty("centerY").GetDouble();
            var selectedAgain = await _app.PressPointerAsync(selectX, selectY);
            Assert.True(selectedAgain.GetProperty("ok").GetBoolean(), selectedAgain.ToString());
            var selectionReleased = await _app.ReleasePointerAsync(selectX, selectY);
            Assert.True(selectionReleased.GetProperty("ok").GetBoolean(), selectionReleased.ToString());

            var clickX = targetBounds.GetProperty("x").GetDouble() + 10;
            var clickY = targetBounds.GetProperty("y").GetDouble() + 10;
            var clicked = await _app.PressPointerAsync(clickX, clickY);
            Assert.True(clicked.GetProperty("ok").GetBoolean(), clicked.ToString());
            var clickReleased = await _app.ReleasePointerAsync(clickX, clickY);
            Assert.True(clickReleased.GetProperty("ok").GetBoolean(), clickReleased.ToString());
            await Task.Delay(500);

            var statusAfterCanvasClick = await _app.InvokeAsync("od.forms-designer.status");
            Assert.Equal(
                statusAfterDrop.GetProperty("controlNames").GetArrayLength(),
                statusAfterCanvasClick.GetProperty("controlNames").GetArrayLength());
        }
        finally
        {
            // Form1.Designer.cs is a repository fixture - restore it regardless of outcome.
            await File.WriteAllTextAsync(formCodePath, originalFormCode);
        }
    }

    [Fact]
    public async Task SelectControlOnSamplePane_ShowsSelectionInPropertiesPad()
    {
        // SamplePane.xaml is a UserControl root (unlike MainWindow.xaml's Window root): verify the
        // designer + Properties pad selection round-trip for a non-Window document. The Xceed pad
        // exposes only a narrow filtered property set (same as for MainWindow's Button - see the
        // SelectControl_EditingContent test), and none of SamplePane's elements carry a string
        // property in that set, so assert the selection/type linkage instead of editing.
        var solutionDirectory = Path.GetDirectoryName(_app.WpfSampleSolutionPath)!;
        var xamlPath = Path.Combine(solutionDirectory, "SamplePane.xaml");

        await _app.EnsureSolutionOpenAsync(_app.WpfSampleSolutionPath);
        var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean());
        var status = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "UserControl", timeoutSeconds: 30, reactivatePath: xamlPath);
        Assert.True(status.GetProperty("designerLoaded").GetBoolean(), status.ToString());
        Assert.Contains(status.GetProperty("outlineNames").EnumerateArray(),
            name => name.GetString() == "PaneTitle");

        var selected = await _app.InvokeAsync("od.wpf-designer.select", "PaneTitle");
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
        Assert.Equal("PaneTitle", selected.GetProperty("selectedName").GetString());
        Assert.Equal("PaneTitle", selected.GetProperty("propertiesPadSelectedName").GetString());
        Assert.Equal("TextBlock", selected.GetProperty("propertiesPadSelectedType").GetString());

        var listSelected = await _app.InvokeAsync("od.wpf-designer.select", "PaneList");
        Assert.True(listSelected.GetProperty("success").GetBoolean(), listSelected.ToString());
        Assert.Equal("PaneList", listSelected.GetProperty("propertiesPadSelectedName").GetString());
        Assert.Equal("ListBox", listSelected.GetProperty("propertiesPadSelectedType").GetString());
    }

    // od.open-file returning "opened" only means the file's ViewContent/window was created -
    // the WPF designer's secondary tab attaches its DesignSurface (WpfViewContent.LoadInternal)
    // on a subsequent UI-thread layout pass, and that pass's timing isn't guaranteed to have
    // completed yet when this suite reuses one already-running OpenDevelopAppFixture app across
    // several tests/documents in the same collection (each test re-invoking od.open-solution on
    // an already-open solution, switching tabs among several already-open windows). Poll instead
    // of asserting immediately, matching the same wait-for-UI-state pattern already used by
    // DebuggerIntegrationTests.WaitForTopFrameLineAsync/WaitForTopFrameNameAsync.
    //
    // Two extra things this has to guard against, both discovered by running the whole class
    // together rather than one test at a time:
    //  - Waiting for outlineChildCount > 0 alone isn't enough: nested custom-control instances
    //    (e.g. MainWindow.xaml's <local:SamplePane x:Name="MainPane">) can populate their own
    //    outline node a little later than their simpler siblings, once that control's own
    //    type/assembly has been resolved - so the top-level count can already be > 0 while a
    //    later sibling is still missing. Wait for the flattened outline name count to stop
    //    growing across two consecutive polls instead of just checking it's non-zero once.
    //  - ActiveViewContent can still be a PREVIOUS test's already-open window/tab for a moment
    //    after od.open-file returns "opened" for the new one (window activation is itself
    //    asynchronous) - so the very first poll or two can report a stable, fully-populated
    //    outline that actually belongs to the wrong document entirely. Require the reported
    //    root item's type to match what this specific test just opened before accepting it.
    // Per-project preferences restore previously-open documents when a solution loads (see the
    // fixture's DeleteStaleViewStateMemento comment), so a document opened by an earlier test in
    // this shared collection can still be - or become - the active view well after this test's
    // own od.open-file returned. When the designer reports a different root than expected,
    // re-invoke od.open-file on the target document periodically: it calls SelectWindow on the
    // already-open view, which wins over the async restore activation.
    async Task<JsonElement> WaitForWpfDesignerStatusAsync(string expectedRootItemType, int timeoutSeconds, string reactivatePath = null)
    {
        JsonElement status = default;
        var previousCount = -1;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.wpf-designer.status");
            if (status.GetProperty("active").GetBoolean() &&
                status.TryGetProperty("designerLoaded", out var loaded) && loaded.GetBoolean() &&
                status.TryGetProperty("rootItemType", out var rootItemType) &&
                rootItemType.GetString() == expectedRootItemType &&
                status.TryGetProperty("outlineNames", out var names))
            {
                var count = names.GetArrayLength();
                if (count > 0 && count == previousCount)
                    return true;
                previousCount = count;
            }
            else
            {
                previousCount = -1;
                if (reactivatePath != null)
                    await _app.InvokeAsync("od.open-file", reactivatePath);
            }
            return false;
        }, TimeSpan.FromSeconds(timeoutSeconds), initialDelayMs: 50, maxDelayMs: 250);
        return status;
    }

    async Task<JsonElement> WaitForPropertiesPadEditAsync(string propertyName, string value, int timeoutSeconds)
    {
        JsonElement result = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            result = await _app.InvokeAsync("od.wpf-designer.properties-pad.edit", propertyName, value);
            return result.GetProperty("success").GetBoolean();
        }, TimeSpan.FromSeconds(timeoutSeconds), initialDelayMs: 50, maxDelayMs: 100);
        return result;
    }

    async Task<JsonElement> WaitForWinUIPropertiesPadEditAsync(string propertyName, string value, int timeoutSeconds)
    {
        JsonElement result = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            result = await _app.InvokeAsync("od.winui-designer.properties-pad.edit", propertyName, value);
            return result.GetProperty("success").GetBoolean();
        }, TimeSpan.FromSeconds(timeoutSeconds), initialDelayMs: 50, maxDelayMs: 100);
        return result;
    }

    async Task<JsonElement> WaitForXamlOutlineStatusAsync(string expectedRootName, int timeoutSeconds, string reactivatePath = null)
    {
        JsonElement status = default;
        var previousCount = -1;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.xaml-outline.status");
            if (status.GetProperty("active").GetBoolean() &&
                status.TryGetProperty("rootName", out var rootName) &&
                rootName.GetString() == expectedRootName &&
                status.TryGetProperty("outlineNames", out var names))
            {
                var count = names.GetArrayLength();
                if (count > 0 && count == previousCount)
                    return true;
                previousCount = count;
            }
            else
            {
                previousCount = -1;
                if (reactivatePath != null)
                    await _app.InvokeAsync("od.open-file", reactivatePath);
            }
            return false;
        }, TimeSpan.FromSeconds(timeoutSeconds), initialDelayMs: 50, maxDelayMs: 250);
        return status;
    }

    [Fact]
    public async Task ProjectContextMenu_ContainsClassDiagram()
    {
        await _app.EnsureSolutionOpenAsync(_app.SolutionExplorerFixturePath);

        var loadedAddIns = await _app.InvokeAsync("od.addins");
        var classDiagramAddIn = loadedAddIns.GetProperty("addins").EnumerateArray().FirstOrDefault(item =>
            item.GetProperty("fileName").GetString()?.Contains("ClassDiagramAddin.addin") == true);
        Assert.NotEqual(default, classDiagramAddIn.ValueKind);
        Assert.True(classDiagramAddIn.GetProperty("enabled").GetBoolean(), classDiagramAddIn.ToString());

        var menu = await _app.InvokeAsync("od.project-context-menu", "SampleApp");

        Assert.True(menu.GetProperty("success").GetBoolean(), menu.TryGetProperty("error", out var error) ? error.GetString() : null);
        Assert.Equal("SampleApp", menu.GetProperty("currentProject").GetString());
        Assert.Equal("SampleApp", menu.GetProperty("descendantCurrentProject").GetString());
        var labels = menu.GetProperty("labels").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("Class Diagram", labels);

        var model = await _app.InvokeAsync("od.class-diagram-project-model", "SampleApp");
        Assert.True(model.GetProperty("success").GetBoolean(), model.TryGetProperty("error", out error) ? error.GetString() : null);
        var sources = model.GetProperty("sourceFiles").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(sources, path => path!.Replace('\\', '/').EndsWith("Models/Widget.cs"));
        Assert.True(model.GetProperty("typeCount").GetInt32() > 0);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "OpenDevelop-ClassDiagram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var diagramPath = Path.Combine(temporaryDirectory, "SampleApp.cd");
        var inheritanceSource = Path.Combine(temporaryDirectory, "DiagramTypes.cs");
        File.WriteAllText(inheritanceSource, "namespace DiagramFixture; class BaseType { } class DerivedType : BaseType { }");
        new XDocument(new XElement("ClassDiagram",
            new XAttribute("Version", "2"),
            sources.Append(inheritanceSource).Select(path => new XElement("Source", new XAttribute("File", path!)))))
            .Save(diagramPath);
        try {
            var openedDiagram = await _app.InvokeAsync("od.open-file", diagramPath);
            Assert.True(openedDiagram.GetProperty("opened").GetBoolean(), openedDiagram.ToString());
            Assert.Equal("ICSharpCode.ClassDiagram.ClassDiagramViewContent", openedDiagram.GetProperty("viewContentType").GetString());

            var canvas = await _app.InvokeAsync("od.class-diagram-canvas", diagramPath);
            Assert.True(canvas.GetProperty("success").GetBoolean(), canvas.TryGetProperty("error", out error) ? error.GetString() : null);
            Assert.True(canvas.GetProperty("cardCount").GetInt32() > 0, canvas.ToString());
            Assert.True(canvas.GetProperty("fitToCanvasAvailable").GetBoolean(), canvas.ToString());
            Assert.False(canvas.GetProperty("dependenciesChecked").GetBoolean(), canvas.ToString());
            Assert.True(canvas.GetProperty("routeCount").GetInt32() > 0, canvas.ToString());
            Assert.True(canvas.GetProperty("allRoutesOrthogonal").GetBoolean(), canvas.ToString());
            Assert.True(canvas.GetProperty("allRouteEndpointsOnCardBoundaries").GetBoolean(), canvas.ToString());
            Assert.Contains(" types,", canvas.GetProperty("status").GetString());
        } finally {
            await _app.InvokeAsync("od.close-active-view");
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateCheck_ReportsCurrentVersionAndCheckOutcome()
    {
        var result = await _app.InvokeAsync("od.update.check");

        var currentVersion = result.GetProperty("currentVersion").GetString();
        Assert.False(string.IsNullOrEmpty(currentVersion), "Expected the running version to be reported");

        // Must be a parseable four-part version (RevisionClass shape).
        Assert.True(Version.TryParse(currentVersion, out var parsed) && parsed.Revision >= 0,
            $"Expected a Major.Minor.Build.Revision version, got '{currentVersion}'");

        if (result.TryGetProperty("checkFailed", out var failed) && failed.GetBoolean())
        {
            // Offline / GitHub rate-limited: the checker must degrade gracefully.
            Assert.True(result.TryGetProperty("error", out _));
            return;
        }

        // No published release on the repository yet (GitHub /releases/latest returns 404) is a
        // legitimate "nothing to update to" - reported as updateAvailable=false, not a failure.
        Assert.True(result.TryGetProperty("latestVersion", out var latest));
        Assert.True(result.TryGetProperty("updateAvailable", out var available));
        Assert.True(available.ValueKind is JsonValueKind.True or JsonValueKind.False);
        if (latest.ValueKind == JsonValueKind.Null)
        {
            Assert.False(available.GetBoolean());
            return;
        }
        Assert.False(string.IsNullOrEmpty(latest.GetString()));
        Assert.True(result.TryGetProperty("automaticCheckEnabled", out _));

        // A downloaded release must point back at the project's own repository.
        if (available.GetBoolean())
        {
            var url = result.GetProperty("downloadUrl").GetString();
            Assert.NotNull(url);
            Assert.Contains("lextudio/OpenDevelop", url);
        }
    }

    [Fact]
    public async Task UpdateCheck_RunningVersionMatchesAssemblyVersion()
    {
        var result = await _app.InvokeAsync("od.update.check");

        // The About/update surface must report the same version the assembly carries -
        // RevisionClass (GlobalAssemblyInfo.cs) and [AssemblyVersion] must stay in sync.
        var currentVersion = result.GetProperty("currentVersion").GetString()!;
        var assemblyVersion = result.GetProperty("assemblyVersion").GetString()!;
        Assert.Equal(assemblyVersion, currentVersion);
    }

    void SetUpGitRepo()
    {
        CopyDirectory(_app.GitFixtureTemplatePath, _repoDir);

        RunGit("init -q");
        RunGit("add GitFixture.sln GitFixtureApp/GitFixtureApp.csproj GitFixtureApp/Clean.cs GitFixtureApp/Modified.cs");
        RunGit("commit -q -m initial");

        // Unstaged modification of a tracked file -> "M" in `git status --porcelain` -> GitFileStatus.Modified.
        File.AppendAllText(Path.Combine(_repoDir, "GitFixtureApp", "Modified.cs"), "\n// dirtied by GitAddInTests\n");

        // Staged-but-uncommitted new file -> "A" in `git status --porcelain` -> GitFileStatus.Added.
        RunGit("add GitFixtureApp/Added.cs");

        // GitFixtureApp/Untracked.cs is left untouched: shared GitStatusService includes untracked
        // files, so it should get the same added-style overlay as UnoDevelop.
    }

    static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                dir.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
        }
    }

    void RunGit(string arguments)
    {
        RunGit(_repoDir, arguments);
    }

    static void RunGit(string workingDirectory, string arguments)
    {
        var psi = new ProcessStartInfo("git", $"-c user.name=\"OpenDevelop Test\" -c user.email=\"test@example.invalid\" {arguments}")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed ({process.ExitCode}): {stdout}\n{stderr}");
    }

    [Fact]
    public async Task AddInsList_ContainsGitAddIn()
    {
        var result = await _app.InvokeAsync("od.addins");
        var addins = result.GetProperty("addins").EnumerateArray().ToList();
        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("GitAddIn.addin"));
    }

    [Fact]
    public async Task OpenSolution_WithGitRepo_OverlayIconsReflectFileStatus()
    {
        var solutionPath = Path.Combine(_repoDir, "GitFixture.sln");
        var openResult = await _app.InvokeAsync("od.open-solution", solutionPath);
        Assert.True(openResult.GetProperty("success").GetBoolean(), $"Failed to open {solutionPath}");

        // The Project Browser pad's TreeView content is only realized by AvalonDock once the pad
        // is actually shown/activated - opening a solution alone doesn't force that, so without
        // this the UI tree below would contain zero file nodes even though the solution loaded fine.
        var showPadResult = await _app.InvokeAsync("od.show-pad", "ProjectBrowserPad");
        Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the ProjectBrowser pad");

        var tree = await _app.GetUITreeAsync();
        var elements = FlattenElements(tree).ToList();

        AssertOverlayStatus(elements, "Clean.cs", null);
        AssertOverlayStatus(elements, "Modified.cs", "Modified");
        AssertOverlayStatus(elements, "Added.cs", "Added");
        AssertOverlayStatus(elements, "Untracked.cs", "Untracked");
    }

    [Fact]
    public async Task OpenSolution_WithOnlyNestedUntrackedFile_ProjectNodeShowsAggregatedStatus()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), "GitAddInAggregateTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(repoDir, "App", "Input"));
            var solutionPath = Path.Combine(repoDir, "AggregateFixture.sln");
            var projectPath = Path.Combine(repoDir, "App", "AggregateApp.csproj");
            File.WriteAllText(solutionPath, """
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "AggregateApp", "App\AggregateApp.csproj", "{2B3C4D5E-2222-4B33-8D44-1F2A3B4C5D6E}"
EndProject
Global
EndGlobal
""");
            File.WriteAllText(projectPath, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
  </ItemGroup>
</Project>
""");
            File.WriteAllText(Path.Combine(repoDir, "App", "Program.cs"), "namespace AggregateApp; public class Program { }\n");

            RunGit(repoDir, "init -q");
            RunGit(repoDir, "add AggregateFixture.sln App/AggregateApp.csproj App/Program.cs");
            RunGit(repoDir, "commit -q -m initial");

            File.WriteAllText(Path.Combine(repoDir, "App", "Input", "NotInProject.cs"),
                "namespace AggregateApp; public class NotInProject { }\n");

            var openResult = await _app.InvokeAsync("od.open-solution", solutionPath);
            Assert.True(openResult.GetProperty("success").GetBoolean(), $"Failed to open {solutionPath}");

            var showPadResult = await _app.InvokeAsync("od.show-pad", "ProjectBrowserPad");
            Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the ProjectBrowser pad");

            var tree = await _app.GetUITreeAsync();
            var elements = FlattenElements(tree).ToList();
            AssertOverlayStatus(elements, "AggregateApp", "Untracked");
        }
        finally
        {
            try { Directory.Delete(repoDir, recursive: true); } catch { }
        }
    }

    static void AssertOverlayStatus(List<JsonElement> elements, string fileName, string? expectedStatus)
    {
        var textNode = elements.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock" &&
            e.TryGetProperty("text", out var txt) && txt.GetString() == fileName);
        Assert.True(textNode.ValueKind != JsonValueKind.Undefined, $"No TextBlock found with Text == '{fileName}' in the Project Browser tree");

        string stackPanelId = textNode.GetProperty("parentId").GetString()!;
        var gridNode = elements.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "Grid" &&
            e.TryGetProperty("parentId", out var p) && p.GetString() == stackPanelId);
        Assert.True(gridNode.ValueKind != JsonValueKind.Undefined, $"No icon Grid found as a sibling of the '{fileName}' TextBlock");

        string gridId = gridNode.GetProperty("id").GetString()!;
        var images = elements.Where(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "Image" &&
            e.TryGetProperty("parentId", out var p) && p.GetString() == gridId).ToList();
        // The node Grid contains 3 Image elements: file icon (16x16), linked-file overlay
        // (16x16, null Source for non-linked files yields a zero-size bounds), and the
        // git-overlay badge (8x8). The overlay is the non-zero Image with the smallest width.
        var overlayImage = images.Where(i => i.TryGetProperty("bounds", out var b)
                && b.GetProperty("width").GetDouble() > 0)
            .OrderBy(i => i.GetProperty("bounds").GetProperty("width").GetDouble()).FirstOrDefault();

        string? automationId = overlayImage.TryGetProperty("automationId", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(automationId))
            automationId = null;

        Assert.Equal(expectedStatus, automationId);
    }

    static bool CheckOverlayActive(List<JsonElement> elements, string fileName)
    {
        var textNode = elements.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock" &&
            e.TryGetProperty("text", out var txt) && txt.GetString() == fileName);
        if (textNode.ValueKind == JsonValueKind.Undefined)
            return false;

        string stackPanelId = textNode.GetProperty("parentId").GetString()!;
        var gridNode = elements.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "Grid" &&
            e.TryGetProperty("parentId", out var p) && p.GetString() == stackPanelId);
        if (gridNode.ValueKind == JsonValueKind.Undefined)
            return false;

        string gridId = gridNode.GetProperty("id").GetString()!;
        return elements.Any(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "Image" &&
            e.TryGetProperty("parentId", out var p) && p.GetString() == gridId &&
            e.TryGetProperty("automationId", out var a) &&
            !string.IsNullOrEmpty(a.GetString()));
    }

    [Fact]
    public async Task SearchAndInstallPackage_UpdatesProjectFile()
    {
        var solutionPath = Path.Combine(_projectDir, "NuGetFixture.sln");
        var openResult = await _app.InvokeAsync("od.open-solution", solutionPath);
        Assert.True(openResult.GetProperty("success").GetBoolean(), $"Failed to open {solutionPath}");

        var addInsResult = await _app.InvokeAsync("od.addins");
        var packageManagementAddIn = addInsResult.GetProperty("addins").EnumerateArray()
            .FirstOrDefault(a => a.TryGetProperty("fileName", out var fileName) &&
                fileName.GetString()?.EndsWith("PackageManagement.addin", StringComparison.Ordinal) == true);
        Assert.True(packageManagementAddIn.ValueKind != JsonValueKind.Undefined, "PackageManagement.addin was not registered");

        var feedResult = await _app.InvokeAsync("od.nuget.set-local-feed", _app.LocalNuGetFeedPath);
        Assert.True(feedResult.GetProperty("success").GetBoolean(), $"Set local feed failed: {feedResult}");

        var openDialogResult = await _app.InvokeAsync("od.nuget.open-dialog");
        Assert.True(openDialogResult.GetProperty("success").GetBoolean(), $"Open dialog failed: {openDialogResult}");

        var setSearchResult = await _app.InvokeAsync("od.nuget.set-search-text", TestPackageId);
        Assert.True(setSearchResult.GetProperty("success").GetBoolean(), $"Set search text failed: {setSearchResult}");

        var searchResult = await _app.InvokeAsync("od.nuget.search");
        Assert.True(searchResult.GetProperty("success").GetBoolean(), $"Search command failed: {searchResult}");

        var status = await WaitForSearchToFinishAsync();
        Assert.False(status.GetProperty("hasError").GetBoolean(), $"Search reported an error: {status}");

        var packages = status.GetProperty("packages").EnumerateArray().ToList();
        Assert.Contains(packages, p => p.GetProperty("id").GetString() == TestPackageId);
        Assert.All(packages.Where(p => p.GetProperty("id").GetString() == TestPackageId),
            p => Assert.False(p.GetProperty("isAdded").GetBoolean(), "Package should not be installed yet"));

        var installResult = await _app.InvokeAsync("od.nuget.install", TestPackageId);
        Assert.True(installResult.GetProperty("success").GetBoolean(), $"Install failed: {installResult}");

        var afterInstall = await WaitForPackageInstalledAsync();
        Assert.True(afterInstall, "Package's IsAdded flag never flipped true after install");

        // The dialog is a real WPF Window (ManagePackagesView), so the visual tree walker sees it:
        // the search result row must render the package name as a real TextBlock, and the per-row
        // "added" check icon (AutomationId=PackageAddedIcon, Visibility bound to IsAdded) must have
        // actually flipped to Visible after the install - i.e. the UI reflects the state the JSON
        // status above claims, not just the view model.
        var tree = await _app.GetUITreeAsync();
        var elements = FlattenElements(tree).ToList();

        Assert.Contains(elements, e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
            && e.TryGetProperty("text", out var txt) && txt.GetString() == TestPackageId);

        Assert.True(elements.Any(e =>
            e.TryGetProperty("automationId", out var a) && a.GetString() == "PackageAddedIcon"
            && e.TryGetProperty("isVisible", out var v) && v.GetBoolean()),
            "Expected the PackageAddedIcon to be Visible in the dialog's package row after install");

        await _app.InvokeAsync("od.nuget.close-dialog");

        // On-disk project state: NuGet's own project-file update path wrote the PackageReference.
        var csprojPath = Path.Combine(_projectDir, "NuGetFixtureApp", "NuGetFixtureApp.csproj");
        var csprojText = await File.ReadAllTextAsync(csprojPath);
        Assert.Contains($"Include=\"{TestPackageId}\"", csprojText);

        // The Project Browser refresh is intentionally not part of this test's pass/fail boundary
        // yet. The install path is asynchronous enough that the UI tree can lag the project-file
        // mutation; keep this test focused on search + install not throwing and the PackageReference
        // being persisted.
    }

    [Fact]
    public async Task SearchText_FiltersPackageResultsInDialog()
    {
        var solutionPath = Path.Combine(_projectDir, "NuGetFixture.sln");
        var openResult = await _app.InvokeAsync("od.open-solution", solutionPath);
        Assert.True(openResult.GetProperty("success").GetBoolean(), $"Failed to open {solutionPath}");

        var feedResult = await _app.InvokeAsync("od.nuget.set-local-feed", _app.LocalNuGetFeedPath);
        Assert.True(feedResult.GetProperty("success").GetBoolean(), $"Set local feed failed: {feedResult}");

        var openDialogResult = await _app.InvokeAsync("od.nuget.open-dialog");
        Assert.True(openDialogResult.GetProperty("success").GetBoolean(), $"Open dialog failed: {openDialogResult}");

        // A partial id match must surface the package from the local feed.
        var setSearchResult = await _app.InvokeAsync("od.nuget.set-search-text", "TestPackage");
        Assert.True(setSearchResult.GetProperty("success").GetBoolean(), $"Set search text failed: {setSearchResult}");

        var searchResult = await _app.InvokeAsync("od.nuget.search");
        Assert.True(searchResult.GetProperty("success").GetBoolean(), $"Search command failed: {searchResult}");

        var status = await WaitForSearchToFinishAsync();
        Assert.False(status.GetProperty("hasError").GetBoolean(), $"Search reported an error: {status}");
        var packages = status.GetProperty("packages").EnumerateArray().ToList();
        Assert.NotEmpty(packages);
        Assert.Contains(packages, p => p.GetProperty("id").GetString() == TestPackageId);
        Assert.All(packages, p =>
            Assert.Contains("TestPackage", p.GetProperty("id").GetString()));

        // A non-matching query must return no results - proves search actually filters instead
        // of always returning the whole feed.
        var noMatch = await _app.InvokeAsync("od.nuget.set-search-text", "definitely-not-in-feed-xyz");
        Assert.True(noMatch.GetProperty("success").GetBoolean());
        var noMatchSearch = await _app.InvokeAsync("od.nuget.search");
        Assert.True(noMatchSearch.GetProperty("success").GetBoolean());
        var emptyStatus = await WaitForSearchToFinishAsync();
        Assert.Empty(emptyStatus.GetProperty("packages").EnumerateArray());

        await _app.InvokeAsync("od.nuget.close-dialog");
    }

    async Task<JsonElement> WaitForSearchToFinishAsync()
    {
        JsonElement status = default;
        var finished = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.nuget.status");
            return status.TryGetProperty("isReadingPackages", out var reading) && !reading.GetBoolean();
        }, TimeSpan.FromSeconds(30));
        if (!finished)
            throw new TimeoutException($"Package search never finished. Last status: {status}");
        return status;
    }

    async Task<bool> WaitForPackageInstalledAsync()
    {
        return await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var status = await _app.InvokeAsync("od.nuget.status");
            return status.GetProperty("packages").EnumerateArray()
                .Any(p => p.GetProperty("id").GetString() == TestPackageId && p.GetProperty("isAdded").GetBoolean());
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task OpenAssembly_ShowsIlSpyPadsWithRealContent()
    {
        var assemblyPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_app.DebugTestProjectPath)!, "bin", "Debug", "net10.0", "DebugTestApp.dll");
        Assert.True(System.IO.File.Exists(assemblyPath),
            $"Expected the DebugTestApp fixture to already be built at {assemblyPath} (see prerequisites)");

        var openResult = await _app.InvokeAsync("od.ilspy.open-assembly", assemblyPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean(), $"Failed to open {assemblyPath} in ILSpy");

        var status = await _app.InvokeAsync("od.ilspy.status");

        // OpenDevelop "enters the ILSpy layout": the three real ILSpy pads are registered as
        // OpenDevelop pads (DockWorkspace.ToolPanes, via DockWorkspaceExtensibility.AddToolPane)
        // and visible, instead of the legacy launch-ILSpy.exe integration. Decompiled output is
        // NOT one of those pads - it opens as a document tab (see the active-view assertion
        // below).
        var panes = status.GetProperty("panes").EnumerateArray()
            .Select(p => (Title: p.GetProperty("title").GetString(), IsVisible: p.GetProperty("isVisible").GetBoolean(), Position: p.GetProperty("position")))
            .ToList();

        // "Search"/"Analyze" are ILSpy's own real pane titles (SearchPaneModel/
        // AnalyzerTreeViewModel set them in their constructors); "Assemblies" is one this addin
        // assigns itself (see IlSpyWorkspaceHost).
        foreach (var expectedTitle in new[] { "Assemblies", "Search", "Analyze"})
        {
            var pane = panes.SingleOrDefault(p => p.Title == expectedTitle);
            Assert.True(pane.Title != null, $"Expected an ILSpy pad titled '{expectedTitle}' to be registered; got: {string.Join(", ", panes.Select(p => p.Title))}");
            Assert.True(pane.IsVisible, $"Expected the '{expectedTitle}' pad to be visible after opening an assembly");
        }
        Assert.DoesNotContain(panes, p => p.Title == "Decompiled Code");

        // Pad *position* coverage (added 2026-08-03): the checks above (title present, IsVisible)
        // pass even when a pad is docked in the wrong place - measured live: a race between
        // switching to the "ILSpy" layout and the docking manager's first Loaded event (see
        // AvalonDockLayout.StoreConfiguration's guard, doc/technotes/ilspy.md "the layout gets
        // lost") used to tab all three pads into whatever pane already existed (e.g. next to
        // "Projects"), on the wrong side, with no test catching it - exactly the failure mode
        // reported after repeated manual runs. Assert each pad's real AvalonDock position against
        // Layouts/ILSpy.xml's template: Assemblies in "LeftPane" on the Left, Search in "TopPane"
        // on the Top, Analyze in "BottomPane" on the Bottom - none floating, auto-hidden, or
        // hidden. (Layout switching is incremental since 2026-08-09 - panes not named in the
        // template are re-docked beside the template's panes rather than evicted - so the panes
        // legitimately host sibling pads like Projects/Tools; only the named pane/side are
        // asserted, not sole occupancy.)
        var expectedPositions = new[] {
            (Title: "Assemblies", PaneName: "LeftPane", Side: "Left"),
            (Title: "Search", PaneName: "TopPane", Side: "Top"),
            (Title: "Analyze", PaneName: "BottomPane", Side: "Bottom"),
        };
        foreach (var expected in expectedPositions)
        {
            var position = panes.Single(p => p.Title == expected.Title).Position;
            Assert.True(position.GetProperty("found").GetBoolean(),
                $"Expected to find '{expected.Title}''s anchorable in the live AvalonDock layout");
            Assert.False(position.GetProperty("isFloating").GetBoolean(), $"Expected '{expected.Title}' to be docked, not floating");
            Assert.False(position.GetProperty("isAutoHidden").GetBoolean(), $"Expected '{expected.Title}' to not be auto-hidden");
            Assert.False(position.GetProperty("isHidden").GetBoolean(), $"Expected '{expected.Title}' to not be hidden");
            Assert.Equal(expected.PaneName, position.GetProperty("paneName").GetString());
            Assert.Equal(expected.Side, position.GetProperty("side").GetString());
        }

        // Decompiled output opens as a document tab (a read-only, virtual file). Opening an assembly
        // selects its AssemblyTreeNode, which routes through the native ilspy:// document path
        // (doc/technotes/ilspy.md "Unify C# document hosting" step 3) rather than the bespoke
        // DecompilerTextView pane - so the active view is DecompiledViewContent, not
        // DecompiledCodeViewContent.
        var activeView = await _app.InvokeAsync("od.active-view");
        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.Equal("ICSharpCode.ILSpyAddIn.DecompiledViewContent", activeView.GetProperty("typeName").GetString());

        // ILSpy's AssemblyTreeModel.ShowAssemblyList would rename Application.Current.MainWindow
        // to "ILSpy {version}"; the OpenDevelop-hosted build skips that (OPOPENDEVELOP conditional
        // in the linked ILSpy source) so the IDE keeps its own title.
        var windowTitle = await _app.InvokeAsync("od.window.title");
        Assert.True(windowTitle.GetProperty("hasWindow").GetBoolean());
        Assert.DoesNotContain("ILSpy", windowTitle.GetProperty("title").GetString());

        // Assembly tree pad: the opened assembly shows up in the real ILSpy AssemblyList.
        var loadedAssemblies = status.GetProperty("loadedAssemblies").EnumerateArray()
            .Select(a => a.GetString())
            .ToList();
        Assert.Contains("DebugTestApp", loadedAssemblies);

        // Opening the assembly auto-selects its tree node (AssemblyTreeModel.SelectNode during
        // OpenFiles) - the model-side selected state must report it.
        var selectedNodes = status.GetProperty("selectedNodes").EnumerateArray()
            .Select(a => a.GetString())
            .ToList();
        Assert.True(selectedNodes.Contains("DebugTestApp"),
            $"Expected the opened assembly's tree node to be selected; got: {string.Join(", ", selectedNodes)}");

        // Jump to the node explicitly (the same real SelectNode path) and confirm the selection
        // sticks and the node's rendered text is reported.
        var selectResult = await _app.InvokeAsync("od.ilspy.select-node", "DebugTestApp");
        Assert.True(selectResult.GetProperty("success").GetBoolean(),
            selectResult.TryGetProperty("error", out var error) ? error.GetString() : null);
        Assert.True(selectResult.GetProperty("selected").GetBoolean(),
            "Expected the DebugTestApp tree node to be selected after od.ilspy.select-node");
        Assert.Contains("DebugTestApp", selectResult.GetProperty("selectedNodes").EnumerateArray()
            .Select(a => a.GetString()).ToList());

        // ILSpy's NavigationHistory dedupes records within a 0.5s window (NavigationHistory.cs:
        // NavigationSecondsBeforeNewEntry): a jump recorded less than 0.5s after the previous
        // navigation updates the current entry without pushing to the back stack, which would make
        // step (3)'s Back assertion flaky (measured). The opens above just recorded the assembly
        // node's module navigation, so settle past that window before the search jump - then the
        // jump is guaranteed to push its own back entry.
        await Task.Delay(600);

        // Activate the Assemblies pad. Deliberately od.ilspy.activate-pane and NOT
        // od.ilspy.show-pane: the latter removes and re-adds the anchorable, which was needed back
        // when runtime-added panes didn't reliably dock, but is destructive now that the ILSpy
        // layout template actually restores (see doc/technotes/ilspy.md's layout-schema work) -
        // measured: after one show-pane, activating a *different* pane fails to materialize it at
        // all, and repeated churn eventually leaves none of them rendered.
        var showPaneResult = await _app.InvokeAsync("od.ilspy.activate-pane", "Assemblies");
        Assert.True(showPaneResult.GetProperty("found").GetBoolean(), "Could not find the Assemblies ILSpy pane");

        JsonElement uiTree = default;
        List<string> texts = new();
        List<JsonElement> allElements = new();
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            uiTree = await _app.GetUITreeAsync();
            allElements = FlattenElements(uiTree).ToList();
            texts = allElements
                .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                    && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
                .Select(e => e.GetProperty("text").GetString())
                .ToList();
            // "[Module]" is DecompiledViewContent's title for a whole-module DecompiledTypeReference
            // (see its constructor) - the native document's tab, now that opening an assembly routes
            // there instead of to the bespoke pane's "Decompiled Code" tab.
            return texts.Contains("Assemblies") && texts.Contains("[Module]");
        }, TimeSpan.FromSeconds(30));
        Assert.Contains(texts, t => t == "Assemblies");
        Assert.Contains(texts, t => t == "[Module]");

        // The selected assembly's tree node is rendered as a real TextBlock (the theme fix made
        // the pane content renderable, and the node was selected above): the node text is
        // LoadedAssembly.Text, "DebugTestApp (1.0.0.0, .NETCoreApp, v10.0)".
        Assert.True(texts.Any(t => t.StartsWith("DebugTestApp", StringComparison.Ordinal)),
            $"Expected the selected assembly node to be rendered in the Assemblies tree; got: {string.Join(" | ", texts.Take(20))}");

        // Regression coverage for doc/technotes/ilspy.md's "empty pane content area" failure mode
        // (fixed 2026-08-02 - a Base.Light/Dark.xaml merge into a since-modernized, no-longer-
        // shipped AvalonDock theme resource threw at startup, which previously left the assembly
        // tree pane's content unrendered): the "Assemblies" pane's content is a real, walkable
        // SharpTreeView tree, not an empty container. Assert on actual node instances with
        // non-zero size, not just the tab header text asserted above.
        var treeNodes = allElements
            .Where(e => e.TryGetProperty("fullType", out var ft)
                && ft.GetString() == "ICSharpCode.ILSpy.Controls.TreeView.SharpTreeNodeView"
                && e.TryGetProperty("bounds", out var b)
                && b.TryGetProperty("width", out var w) && w.GetDouble() > 0
                && b.TryGetProperty("height", out var h) && h.GetDouble() > 0)
            .ToList();
        Assert.True(treeNodes.Count > 0,
            "Expected the Assemblies pane's SharpTreeView to render at least one real, non-zero-size tree node - got an empty pane (the historical failure mode)");

        // Opening the assembly auto-selects and decompiles its tree node - od.ilspy.status reads
        // decompiled text from whichever is actually active (the native DecompiledViewContent here,
        // per the AssemblyTreeNode routing above), so this should be the whole module's real output,
        // not a blank/placeholder document.
        Assert.True(status.GetProperty("decompiledTextLength").GetInt32() > 0,
            "Expected the decompiled document to show non-empty decompiled output after opening an assembly");

        // The decompiled text must be the fixture's real IL - not a placeholder/error pane.
        // Private members (ComputeGreeting) aren't decompiled by default, so assert on the
        // type name being present and the ILSpy file-not-found placeholder being absent.
        var snippet = status.GetProperty("decompiledTextSnippet").GetString()!;
        Assert.Contains("Program", snippet);
        Assert.DoesNotContain("The directory was not found", snippet);

        // --- Dedicated-pad visible-content coverage (added 2026-08-03) -------------------------
        // An audit found that of the three ILSpy tool pads, only "Assemblies" had a real *visible
        // content* assertion (the SharpTreeNodeView check above). "Search" and "Analyze" were
        // covered by nothing but title + IsVisible - which is exactly what the historical "empty
        // pane content area" failure mode (doc/technotes/ilspy.md) passes: correct tab header,
        // blank content. The SearchBox specifically had its own such regression ("rendered as a
        // blank gap", fixed via generic.xaml) with nothing guarding it.
        //
        // These live INSIDE this one test rather than as separate [Fact]s: the app fixture is shared
        // across the whole collection, so separate test methods that each activate panes interfere
        // in an order-dependent way (measured - as three [Fact]s this suite failed 3/5 with "pane
        // not materialized" and a lost active document). Sequenced here it is deterministic.
        //
        // No pane switching is needed for these assertions: the restored ILSpy layout docks all
        // three anchorables, so all three pads' content is laid out simultaneously.

        // Search pad: real content, including the SearchBox that once rendered as a blank gap.
        var activateSearch = await _app.InvokeAsync("od.ilspy.activate-pane", "Search");
        Assert.True(activateSearch.GetProperty("found").GetBoolean(), "Could not find the Search ILSpy pane");
        await AssertRenderedWithNonZeroSizeAsync(
            "ICSharpCode.ILSpy.Search.SearchPane",
            "ICSharpCode.ILSpy.Controls.SearchBox");

        // Search -> results -> activate a result -> the Assemblies tree jumps to that member.
        // "ComputeGreeting" is unique to the DebugTestApp fixture (its only private helper), so this
        // stays deterministic even though ILSpy also searches the auto-loaded framework assemblies.
        var search = await _app.InvokeAsync("od.ilspy.search", "ComputeGreeting");
        Assert.True(search.GetProperty("success").GetBoolean(), ErrorOf(search));
        var searchResults = search.GetProperty("results").EnumerateArray()
            .Select(r => (
                Name: r.GetProperty("name").GetString(),
                Location: r.GetProperty("location").GetString(),
                HasReference: r.GetProperty("hasReference").GetBoolean()))
            .ToList();
        // The fixture is fixed (tests/fixtures/DebugTestApp), and "ComputeGreeting" is its only
        // private helper, so this is exact rather than "at least one hit": one result, with ILSpy's
        // own rendering of the signature.
        var hit = Assert.Single(searchResults);
        Assert.Equal("Program.ComputeGreeting(string) : string", hit.Name);
        Assert.Equal("DebugTestApp.Program", hit.Location);
        Assert.True(hit.HasReference,
            "A search result must carry a navigable Reference, otherwise activating it cannot navigate anywhere");
        int hitIndex = 0;

        // Double-clicking a result does exactly one thing (SearchPane.JumpToSelectedItem):
        // MessageBus.Send(new NavigateToReferenceEventArgs(result.Reference)) - which
        // AssemblyTreeModel subscribes to and turns into JumpToReferenceAsync -> SelectNode. So
        // activating it must move the Assemblies tree selection off the assembly node onto the member.
        var activate = await _app.InvokeAsync("od.ilspy.search-activate", hitIndex);
        Assert.True(activate.GetProperty("success").GetBoolean(), ErrorOf(activate));
        Assert.True(activate.GetProperty("selectionChanged").GetBoolean(),
            "Expected activating a search result to change the Assemblies tree selection (the jump)");
        var jumpedTo = activate.GetProperty("selectedNodeDetails").EnumerateArray()
            .Select(n => (Type: n.GetProperty("nodeType").GetString(), Text: n.GetProperty("text").GetString()))
            .ToList();
        var jumpedNode = Assert.Single(jumpedTo);
        Assert.Equal("MethodTreeNode", jumpedNode.Type);
        Assert.Equal("ComputeGreeting(string) : string", jumpedNode.Text);

        // Analyze pad: real content. AnalyzerTreeView *is* a SharpTreeView (its XAML root element),
        // so this also covers the shared tree control rendering inside this pad.
        var activateAnalyze = await _app.InvokeAsync("od.ilspy.activate-pane", "Analyze");
        Assert.True(activateAnalyze.GetProperty("found").GetBoolean(), "Could not find the Analyze ILSpy pane");
        await AssertRenderedWithNonZeroSizeAsync("ICSharpCode.ILSpy.Analyzers.AnalyzerTreeView");

        // --- Multi-pad workflows (added 2026-08-03) -------------------------------------------
        // The checks above verify each pad in isolation. What a user actually does is drive one pad
        // from another, so these assert the linkages themselves, in the order a user would hit them.

        // (1) Search pad -> Assemblies pad -> Decompiled Code document.
        // The search jump above already moved the tree selection onto ComputeGreeting; the point of
        // this assertion is the *third* pad: the decompiled document must follow the tree selection,
        // which is the whole reason the jump is useful. Verified content, not just non-emptiness.
        var afterJump = await WaitForDecompiledTextAsync(
            text => text.Contains("ComputeGreeting", StringComparison.Ordinal),
            "the decompiled document to follow the Assemblies-tree jump onto ComputeGreeting");
        // Exact expected decompilation of the fixture's ComputeGreeting - the source is
        // `$"Hello, {name}!"`, which the decompiler renders as string concatenation.
        Assert.Contains("// DebugTestApp.Program", afterJump);
        Assert.Contains("private static string ComputeGreeting(string name)", afterJump);
        Assert.Contains("return \"Hello, \" + name + \"!\";", afterJump);

        // (2) Assemblies pad -> Analyze pad. Selecting a member and analyzing it is the canonical
        // cross-pad action; od.ilspy.analyze-selected runs exactly what ILSpy's AnalyzeCommand does
        // (SelectedNodes.OfType<IMemberTreeNode>() -> AnalyzerTreeViewModel.Analyze(node.Member)).
        var analyze = await _app.InvokeAsync("od.ilspy.analyze-selected");
        Assert.True(analyze.GetProperty("success").GetBoolean(), ErrorOf(analyze));
        var analyzerRoots = analyze.GetProperty("rootChildren").EnumerateArray()
            .Select(n => (
                Text: n.GetProperty("text").GetString(),
                NodeType: n.GetProperty("nodeType").GetString(),
                Children: n.GetProperty("children").EnumerateArray().Select(c => c.GetString()).ToList()))
            .ToList();
        var analyzedMethod = analyzerRoots.SingleOrDefault(n =>
            n.Text != null && n.Text.Contains("ComputeGreeting", StringComparison.Ordinal));
        Assert.True(analyzedMethod.Text != null,
            $"Expected the Analyze pad to hold an analysis root for ComputeGreeting; got: {string.Join(" | ", analyzerRoots.Select(n => n.NodeType + ":" + n.Text))}");
        Assert.Equal("AnalyzedMethodTreeNode", analyzedMethod.NodeType);
        Assert.Equal("DebugTestApp.Program.ComputeGreeting(string) : string", analyzedMethod.Text);
        // A real analysis, not an empty placeholder: a method analysis offers exactly these two.
        Assert.Equal(new[] { "Uses", "Used By" }, analyzedMethod.Children);

        // (3) Back navigation undoes the jump - the Back toolbar button's whole purpose, and only
        // meaningful because the jump in (1) pushed history.
        var back = await _app.InvokeAsync("od.ilspy.navigate-history", "back");
        Assert.True(back.GetProperty("success").GetBoolean(), ErrorOf(back));
        Assert.True(back.GetProperty("selectionChanged").GetBoolean(),
            "Expected navigating back to move the Assemblies-tree selection off the jumped-to member");
        Assert.True(back.GetProperty("canNavigateForward").GetBoolean(),
            "Expected Forward to become available after navigating back");
        var afterBack = back.GetProperty("selectedNodeDetails").EnumerateArray()
            .Select(n => (Type: n.GetProperty("nodeType").GetString(), Text: n.GetProperty("text").GetString()))
            .ToList();
        var restored = Assert.Single(afterBack);
        Assert.Equal("AssemblyTreeNode", restored.Type);
        Assert.Equal("DebugTestApp (1.0.0.0, .NETCoreApp, v10.0)", restored.Text);

        // (4) Toolbar language dropdown -> Decompiled Code document. Crosses the toolbar and the
        // document, and is the one toolbar element whose effect is directly observable in content.
        // ILSpy persists the chosen language in its session settings, so this must be put back or the
        // *next* run of this test would start in IL - hence the try/finally.
        var beforeLanguage = await _app.InvokeAsync("od.ilspy.toolbar-combos", "", "");
        string originalLanguage = beforeLanguage.GetProperty("combos").EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "IlSpyLanguageComboBox")
            .GetProperty("selectedItem").GetString()!;
        try
        {
            // Re-select the member first: step (3) navigated back to the assembly node, and what the
            // decompiled document holds for an assembly-level selection is a different (much larger)
            // output. Pinning the selection to one small member keeps this assertion about the
            // *language switch* rather than about whatever happened to be selected.
            // Re-search rather than reusing the earlier result index: the pad activations and the
            // Analyze step in between can have re-materialized the Search pane, and its Results
            // collection lives on the *view*, so a stale index is not guaranteed to still resolve.
            var reSearch = await _app.InvokeAsync("od.ilspy.search", "ComputeGreeting");
            Assert.True(reSearch.GetProperty("success").GetBoolean(), ErrorOf(reSearch));
            Assert.Equal(1, reSearch.GetProperty("count").GetInt32());
            var reActivate = await _app.InvokeAsync("od.ilspy.search-activate", 0);
            Assert.True(reActivate.GetProperty("success").GetBoolean(), ErrorOf(reActivate));
            await WaitForDecompiledTextAsync(
                text => text.Contains("ComputeGreeting", StringComparison.Ordinal),
                "the decompiled document to show ComputeGreeting again before switching language");

            var toIl = await _app.InvokeAsync("od.ilspy.toolbar-combos", "Language", "IL");
            Assert.True(toIl.GetProperty("success").GetBoolean(), ErrorOf(toIl));

            // Exact expected IL for the fixture's ComputeGreeting, not just "some IL directive".
            var il = await WaitForDecompiledTextAsync(
                text => text.Contains(".maxstack", StringComparison.Ordinal),
                "the decompiled document to switch to IL after picking IL in the toolbar's language dropdown");
            Assert.Contains(".method private hidebysig static", il);
            Assert.Contains("string ComputeGreeting (", il);
            Assert.Contains(".maxstack 3", il);
            Assert.Contains("System.String::Concat(string, string, string)", il);
            Assert.Contains("end of method Program::ComputeGreeting", il);
            // ...and it is no longer the C# rendering.
            Assert.DoesNotContain("private static string ComputeGreeting(string name)", il);

            // The language-version dropdown is part of the same linkage: IL has no versions, so it
            // must collapse (ILSpy binds its Visibility to HasLanguageVersions).
            var combosInIl = await _app.InvokeAsync("od.ilspy.toolbar-combos", "", "");
            var versionCombo = combosInIl.GetProperty("combos").EnumerateArray()
                .First(c => c.GetProperty("type").GetString() == "IlSpyLanguageVersionComboBox");
            Assert.False(versionCombo.GetProperty("isVisible").GetBoolean(),
                "Expected the language-version dropdown to collapse for IL, which has no language versions");
        }
        finally
        {
            await _app.InvokeAsync("od.ilspy.toolbar-combos", "Language", originalLanguage);
        }

        // (5) Reference hyperlink navigation inside the decompiled document itself - clicking a
        // type/member reference must jump to its declaration, exactly like an IDE's "go to
        // definition". The whole-module document open right now (re-opened as the active view by
        // navigating back to it below) contains Main's call to ComputeGreeting - a real use-site
        // reference, not a definition - so clicking it must open ComputeGreeting's *declaring type*
        // as its own native document and land on the method's declaration line.
        //
        // od.ilspy.click-reference exercises DecompiledViewContent.TryNavigateAtOffset directly
        // (offset resolved by searching the document text for the given substring) rather than a
        // real synthesized mouse event: this environment's click action takes screen coordinates,
        // and mapping a document offset to a screen pixel needs AvalonEdit's own
        // TextEditor.GetPositionFromPoint - the same, unmodified, already-relied-upon API real
        // .cs-file Ctrl+Click "Go To Definition" uses today (CodeEditorView.cs). That call is not
        // novel here, so exercising the offset -> navigate half (the logic this session actually
        // added: reference-span lookup + NavigateToDecompiledEntityService.NavigateTo) is what
        // matters, and is exactly what TryNavigateAtOffset is.
        // Deliberately od.ilspy.navigate-to-module, not od.ilspy.select-node: interacting with the
        // Search/Analyze tool panes above can leave the real ILSpy tree's own SharpTreeView holding
        // the dock's ActiveContent, and re-selecting the assembly node through the tree does not
        // reliably reclaim it back to the document - a pre-existing ILSpy/AvalonDock focus quirk
        // (measured: od.active-view reported {"active":false} for 30s straight after select-node
        // here), not something this addin's routing introduced. navigate-to-module calls
        // NavigateToDecompiledEntityService.NavigateToModule directly, sidestepping the tree
        // control entirely.
        var reopenModule = await _app.InvokeAsync("od.ilspy.navigate-to-module", "DebugTestApp");
        Assert.True(reopenModule.GetProperty("success").GetBoolean(), ErrorOf(reopenModule));
        await WaitForDecompiledTextAsync(
            text => text.Contains("ComputeGreeting", StringComparison.Ordinal) && text.Contains("using System;", StringComparison.Ordinal),
            "the whole-module document to be active again before testing reference-click navigation");

        var click = await _app.InvokeAsync("od.ilspy.click-reference", "ComputeGreeting", 0);
        Assert.True(click.GetProperty("success").GetBoolean(), ErrorOf(click));
        Assert.True(click.GetProperty("navigated").GetBoolean(),
            "Expected clicking the ComputeGreeting call site to resolve to a navigable reference span");

        var afterClick = await _app.InvokeAsync("od.active-view");
        // Settling this view switch can take one dispatcher tick - see the "Verified live" note in
        // the technote for od.active-view showing the pre-navigation value on the very same call
        // that triggered it.
        for (int i = 0; i < 40 && afterClick.GetProperty("fileName").GetString() != null
                && afterClick.GetProperty("fileName").GetString()!.EndsWith("module.cs", StringComparison.Ordinal); i++)
        {
            await Task.Delay(100);
            afterClick = await _app.InvokeAsync("od.active-view");
        }
        if (!afterClick.GetProperty("fileName").GetString()!.EndsWith("DebugTestApp.Program.cs", StringComparison.Ordinal))
            Assert.Fail($"click={click}; afterClick={afterClick};");
        Assert.EndsWith("DebugTestApp.Program.cs", afterClick.GetProperty("fileName").GetString());
        Assert.Equal(17, afterClick.GetProperty("caretLine").GetInt32());

        // (6) Multi-select decompilation: several Assemblies-tree nodes selected together must
        // decompile into ONE combined document, not just whichever was selected last. This was, at
        // the start of this technote's "what's left" pass, the one item with zero code in either
        // direction - ILSpyDecompilerService.DecompileNodes + DecompiledSelectionViewContent close
        // it. "System.Linq" is a real, already-auto-loaded framework assembly (WPF pulls it in), so
        // this exercises the actual multi-assembly case, not just multiple nodes within one module.
        var multiSelect = await _app.InvokeAsync("od.ilspy.select-nodes", "DebugTestApp,System.Linq");
        Assert.True(multiSelect.GetProperty("success").GetBoolean(), ErrorOf(multiSelect));
        Assert.Equal(
            new[] { "DebugTestApp", "System.Linq" },
            multiSelect.GetProperty("selectedNodes").EnumerateArray().Select(n => n.GetString()).ToArray());

        var combined = await WaitForDecompiledTextAsync(
            text => text.Contains("DebugTestApp.dll", StringComparison.Ordinal) && text.Contains("System.Linq.dll", StringComparison.Ordinal),
            "the multi-selected DebugTestApp and System.Linq nodes to decompile together into one document");
        // Both modules' own header comment (real ILSpy's AssemblyTreeNode.Decompile writes the
        // assembly file path as a "// <path>" comment) must appear, in selection order, confirming
        // this is genuinely both modules concatenated - not one replacing the other.
        int debugTestAppIndex = combined.IndexOf("DebugTestApp.dll", StringComparison.Ordinal);
        int systemLinqIndex = combined.IndexOf("System.Linq.dll", StringComparison.Ordinal);
        Assert.True(debugTestAppIndex >= 0 && systemLinqIndex > debugTestAppIndex,
            $"Expected DebugTestApp's module content before System.Linq's in the combined document; got indices {debugTestAppIndex}, {systemLinqIndex}");

        var multiActiveView = await _app.InvokeAsync("od.active-view");
        Assert.Equal("ICSharpCode.ILSpyAddIn.DecompiledSelectionViewContent", multiActiveView.GetProperty("typeName").GetString());
    }

    /// <summary>
    /// Polls the decompiled document until its text satisfies <paramref name="predicate"/>. Decompiling
    /// is asynchronous and triggered indirectly (by a tree selection or a language change), so the
    /// content a linkage produces is never available on the very next call.
    /// </summary>
    async Task<string> WaitForDecompiledTextAsync(Func<string, bool> predicate, string expectation)
    {
        string snippet = "";
        var satisfied = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var status = await _app.InvokeAsync("od.ilspy.status");
            snippet = status.GetProperty("decompiledTextSnippet").GetString() ?? "";
            return predicate(snippet);
        }, TimeSpan.FromSeconds(30));
        if (satisfied)
            return snippet;
        Assert.Fail($"Timed out waiting for {expectation}. Decompiled text was: {snippet[..Math.Min(400, snippet.Length)]}");
        return snippet;
    }

    [Fact]
    public async Task SwitchToIlSpyLayout_ActivatesPanesWithoutPriorIlSpyInteraction()
    {
        // Regression coverage for the gap the user flagged 2026-08-02: registering "ILSpy" as an
        // AddIn-contributed named layout (ILayoutTemplateProvider) is not enough by itself -
        // selecting the layout must also activate IlSpyWorkspaceHost, or ILSpy.xml's pane
        // anchorables silently restore nothing (DockWorkspace's LayoutSerializationCallback skips
        // any ContentId that isn't registered yet). od.ilspy.is-initialized reads
        // IlSpyWorkspaceHost.IsInitialized without triggering it (unlike od.ilspy.status/
        // show-pane/open-assembly, which all initialize as a side effect), so it can distinguish
        // "the layout switch itself did this" from "some other ILSpy action already had."
        //
        // Ordering caveat: OpenDevelopAppFixture's app instance is shared across every test in the
        // "OpenDevelop app" collection, so if another test has already opened an assembly or
        // touched an od.ilspy.* action first, IsInitialized may already be true before this test's
        // switch-layout call - that's still a valid state (the addin was activated one way or
        // another), just not proof this specific call did it. The was-it-already-initialized
        // check below is therefore informational, not a hard assertion; what this test actually
        // guards is the behavior that matters end-to-end: after switching to "ILSpy", the addin's
        // panes are unconditionally initialized and visible, however that came about.
        var before = await _app.InvokeAsync("od.ilspy.is-initialized");
        var wasAlreadyInitialized = before.GetProperty("initialized").GetBoolean();

        var switchResult = await _app.InvokeAsync("od.workbench.switch-layout", "ILSpy");
        Assert.True(switchResult.GetProperty("found").GetBoolean(), "Expected the AddIn-contributed \"ILSpy\" layout to be registered");
        Assert.Equal("ILSpy", switchResult.GetProperty("layoutName").GetString());

        var after = await _app.InvokeAsync("od.ilspy.is-initialized");
        Assert.True(after.GetProperty("initialized").GetBoolean(),
            $"Expected switching to the \"ILSpy\" layout to activate IlSpyWorkspaceHost (ILayoutTemplateProvider.OnActivating). " +
            $"wasAlreadyInitialized={wasAlreadyInitialized} (informational - see ordering caveat above)");

        var status = await _app.InvokeAsync("od.ilspy.status");
        var paneTitles = status.GetProperty("panes").EnumerateArray()
            .Select(p => p.GetProperty("title").GetString())
            .ToList();
        foreach (var expectedTitle in new[] { "Assemblies", "Search", "Analyze" })
        {
            Assert.Contains(expectedTitle, paneTitles);
        }
    }

    /// <summary>
    /// Asserts each given CLR type renders as a real, laid-out element (non-zero width AND height)
    /// somewhere in the UI tree - i.e. the pane's content area is actually populated, not merely
    /// present-but-unrendered (bounds arrive as null for an element that exists in the tree but was
    /// never laid out, e.g. content of a non-selected tab).
    /// </summary>
    async Task AssertRenderedWithNonZeroSizeAsync(params string[] fullTypes)
    {
        List<string> missing = new();
        var allRendered = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var tree = await _app.GetUITreeAsync();
            var all = FlattenElements(tree).ToList();
            missing = fullTypes.Where(ft => !all.Any(e => IsRenderedInstanceOf(e, ft))).ToList();
            return missing.Count == 0;
        }, TimeSpan.FromSeconds(30));
        if (allRendered)
            return;
        // Distinguish "not in the tree at all" from "in the tree but never laid out" - they have
        // completely different causes (pane/DataTemplate not materialized vs. collapsed/zero-size).
        var finalTree = await _app.GetUITreeAsync();
        var finalAll = FlattenElements(finalTree).ToList();
        var diagnosis = missing.Select(ft =>
        {
            var instances = finalAll.Where(e => e.TryGetProperty("fullType", out var t) && t.GetString() == ft).ToList();
            if (instances.Count == 0)
                return ft + " => absent from the UI tree entirely";
            var boundsDesc = instances.Select(e =>
                e.TryGetProperty("bounds", out var b) && b.ValueKind == JsonValueKind.Object
                    ? $"{b.GetProperty("width").GetDouble()}x{b.GetProperty("height").GetDouble()}"
                    : "bounds=null").ToList();
            return $"{ft} => present x{instances.Count} but unrendered ({string.Join(", ", boundsDesc)})";
        });
        Assert.Fail("Expected these ILSpy pane controls to render with a non-zero size: "
            + string.Join(" | ", diagnosis));
    }

    static bool IsRenderedInstanceOf(JsonElement element, string fullType)
    {
        return element.TryGetProperty("fullType", out var ft) && ft.GetString() == fullType
            && element.TryGetProperty("bounds", out var bounds) && bounds.ValueKind == JsonValueKind.Object
            && bounds.TryGetProperty("width", out var w) && w.GetDouble() > 0
            && bounds.TryGetProperty("height", out var h) && h.GetDouble() > 0;
    }

    static string? ErrorOf(JsonElement result)
    {
        return result.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    public ValueTask InitializeAsync() => default;


    static void CopyDirectoryOd(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                dir.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                dir.Contains(Path.DirectorySeparatorChar + ".od" + Path.DirectorySeparatorChar))
                continue;
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + ".od" + Path.DirectorySeparatorChar))
                continue;
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
        }
    }

    [Fact]
    public async Task FindReferences_FindsDeclarationAndCrossFileUsage()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _solutionPath)).GetProperty("success").GetBoolean());
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetPath)).GetProperty("opened").GetBoolean());
        // The ILanguageService backend (Roslyn AdhocWorkspace for C#/VB) only knows about
        // documents that were upserted (opened/synced). Open the referencing file too so
        // cross-file lookup has both documents.
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetServicePath)).GetProperty("opened").GetBoolean());

        // "Widget" on the class declaration line: "    public sealed class Widget" (line 3, 1-based;
        // "Widget" starts at column 25 - use column 27 to stay safely inside the identifier token).
        var result = await _app.InvokeAsync("od.find-references", _widgetPath, 3, 27);

        Assert.True(result.TryGetProperty("count", out var count), result.ToString());
        Assert.True(count.GetInt32() > 0, $"Expected at least one reference, got: {result}");

        var files = result.GetProperty("references").EnumerateArray()
            .Select(r => r.GetProperty("filePath").GetString()!.Replace('\\', '/'))
            .ToList();

        Assert.True(files.Any(f => f.EndsWith("Services/WidgetService.cs")),
            $"Expected a reference from WidgetService.cs (IEnumerable<Widget>), got: {result}");
    }

    [Fact]
    public async Task RenameSymbol_UpdatesDeclarationAndCrossFileUsage()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _solutionPath)).GetProperty("success").GetBoolean());
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetPath)).GetProperty("opened").GetBoolean());
        // Same as FindReferences: the Roslyn AdhocWorkspace backend only sees upserted documents,
        // so open the referencing file before renaming or the cross-file edit is never computed.
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetServicePath)).GetProperty("opened").GetBoolean());

        var renameResult = await _app.InvokeAsync("od.rename-symbol", _widgetPath, 3, 27, "Gadget");
        Assert.True(renameResult.GetProperty("success").GetBoolean(), renameResult.ToString());
        Assert.Equal("Widget", renameResult.GetProperty("oldName").GetString());

        // od.rename-symbol applies the computed edits directly to disk (ApplyEditsToFile) rather
        // than through open editors - assert both files on disk picked up the new name.
        var onDiskAfterRename = File.ReadAllText(_widgetPath);
        Assert.Contains("class Gadget", onDiskAfterRename);

        Assert.Contains("IEnumerable<Gadget>", File.ReadAllText(_widgetServicePath));
    }

    [Fact]
    public async Task ExtractInterface_GeneratesInterfaceAndAddsToClassWithoutTouchingDisk()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _solutionPath)).GetProperty("success").GetBoolean());
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetPath)).GetProperty("opened").GetBoolean());

        var newInterfacePath = Path.Combine(Path.GetDirectoryName(_widgetPath)!, "IWidget.cs");

        // "Widget" on the class declaration line, same location as the other two tests above.
        var result = await _app.InvokeAsync("od.extract-interface", _widgetPath, 3, 27, "IWidget", newInterfacePath, true, "");
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());

        var members = result.GetProperty("members").EnumerateArray().Select(m => m.GetString()).ToList();
        // Members are reported as "TypeName.Member" (the ILanguageService contract's
        // ExtractInterfaceInfo.Members), not bare member names.
        Assert.Contains("Widget.Name", members);

        Assert.True(File.Exists(newInterfacePath), "Extract Interface should have written the new interface file to disk");
        var interfaceText = File.ReadAllText(newInterfacePath);
        Assert.Contains("public interface IWidget", interfaceText);
        Assert.Contains("string Name { get; set; }", interfaceText);

        // od.extract-interface writes the class edits (adding ": IWidget") to disk as well,
        // not through the live editor (same ApplyEditsToFile convention as od.rename-symbol).
        var onDiskClassText = File.ReadAllText(_widgetPath);
        Assert.Contains("class Widget : IWidget", onDiskClassText);
    }

    public async ValueTask DisposeAsync()
    {
                try { Directory.Delete(_repoDir, recursive: true); } catch { }
                try { Directory.Delete(_projectDir, recursive: true); } catch { }
                if (_ilSpySettingsBackup != null)
                {
                    try { File.Copy(_ilSpySettingsBackup, IlSpySettingsPath, overwrite: true); } catch { }
                    try { File.Delete(_ilSpySettingsBackup); } catch { }
                }
                try { await _app.InvokeAsync("od.file.revert-all-dirty"); } catch { }
                try { Directory.Delete(_solutionDir, recursive: true); } catch { }
                try { Directory.Delete(_unoSampleDir, recursive: true); } catch { }
    }
}
