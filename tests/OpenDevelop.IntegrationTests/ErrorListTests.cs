using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// Coverage for the Error List pad, which is a genuinely separate code path from od.build-solution's
// own BuildResults object (see od.error-list's Description in OpenDevelopDevFlowActions.cs):
// UIBuildFeedbackSink.ReportError -> TaskService.Add is dispatched via InvokeAsyncAndForget, and
// TaskService's task list is a static/global collection that only the Build *menu command*
// (BuildCommands.cs' BeforeBuild()) clears before building - od.build-solution's direct
// SD.BuildService.BuildAsync call does not. BuildTests.cs already covers BuildResults; this covers
// what the pad itself actually accumulates.
//
// Uses a scratch broken .cs file dropped into the SolutionExplorerFixture's SampleApp project to
// produce a real compile error, and deletes it (restoring the fixture) in a finally block.
[Collection("OpenDevelop app")]
public sealed class ErrorListTests
{
    readonly OpenDevelopAppFixture _app;

    public ErrorListTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    string SampleAppDirectory => Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp");

    [Fact]
    public async Task ErrorList_IsEmptyAfterCleanBuild()
    {
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
        await _app.InvokeAsync("od.error-list.clear");
        await _app.InvokeAsync("od.build-solution");

        var errorList = await _app.InvokeAsync("od.error-list");
        Assert.Equal(0, errorList.GetProperty("errorCount").GetInt32());
    }

    // Documents an actual gap this test uncovered: MinimalMSBuildEngine (a real `dotnet build`
    // child process, per BuildTests.cs) detects build failure only via a non-zero exit code - it
    // does not parse the process's own console output into individual per-file/per-line compiler
    // diagnostics. So BOTH od.build-solution's own BuildResults.Errors *and* the Error List pad
    // (fed separately via UIBuildFeedbackSink.ReportError -> TaskService.Add) end up with exactly
    // one generic synthetic entry - "Build failed (exit code non-zero); see build output for
    // details.", pointing at the .csproj with Line=-1/0 - regardless of how many or which real
    // compile errors caused the failure. The individual compiler errors are only visible as raw
    // text via od.output-text (see BuildTests.cs' OutputPadCapturesRealBuildLog), not structured.
    [Fact]
    public async Task ErrorList_OnBuildFailure_ShowsGenericSummaryTaskNotPerLineDiagnostics()
    {
        var brokenFilePath = Path.Combine(SampleAppDirectory, "ScratchBroken.cs");
        try
        {
            File.WriteAllText(brokenFilePath,
                "namespace SampleApp {\n" +
                "    class ScratchBroken {\n" +
                "        void Method() { this is not valid csharp syntax at all }\n" +
                "    }\n" +
                "}\n");

            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");

            var buildResult = await _app.InvokeAsync("od.build-solution");
            Assert.False(buildResult.GetProperty("errorCount").GetInt32() == 0, "Expected the broken scratch file to produce build errors");
            // Even od.build-solution's own diagnostics are just the one generic summary, not
            // per-line detail - the actual compile errors only ever reach od.output-text as text.
            Assert.Single(buildResult.GetProperty("diagnostics").EnumerateArray());

            var errorList = await _app.InvokeAsync("od.error-list");
            Assert.Equal(1, errorList.GetProperty("errorCount").GetInt32());

            var task = errorList.GetProperty("tasks").EnumerateArray().Single();
            Assert.Equal("Error", task.GetProperty("type").GetString());
            Assert.EndsWith("SampleApp.csproj", task.GetProperty("file").GetString()!.Replace('\\', '/'));
            Assert.Contains("Build failed", task.GetProperty("description").GetString());
        }
        finally
        {
            TryDelete(brokenFilePath);
            // Leave the app's own error-list state clean for whichever test runs next in this
            // shared app instance, rather than letting od.build-solution's non-clearing behavior
            // leak this test's induced error into later tests.
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");
            await _app.InvokeAsync("od.build-solution");
        }
    }

    [Fact]
    public async Task ErrorList_WithoutExplicitClear_StaleEntriesSurviveANewCleanBuild()
    {
        // Documents a real characteristic (not a test bug): od.build-solution calls
        // SD.BuildService.BuildAsync directly, bypassing the Build menu command's
        // TaskService.ClearExceptCommentTasks() - so unlike using the actual Build menu/toolbar
        // button, driving builds through this API can leave a previous build's errors in the
        // Error List pad even after a subsequent build of now-fixed code succeeds.
        var brokenFilePath = Path.Combine(SampleAppDirectory, "ScratchStale.cs");
        try
        {
            File.WriteAllText(brokenFilePath, "this is not valid csharp syntax at all");

            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");
            await _app.InvokeAsync("od.build-solution");

            var afterBrokenBuild = await _app.InvokeAsync("od.error-list");
            Assert.True(afterBrokenBuild.GetProperty("errorCount").GetInt32() > 0);

            // Fix the code and build again, WITHOUT calling od.error-list.clear first.
            TryDelete(brokenFilePath);
            var secondBuildResult = await _app.InvokeAsync("od.build-solution");
            Assert.Equal(0, secondBuildResult.GetProperty("errorCount").GetInt32());

            var afterFixedBuild = await _app.InvokeAsync("od.error-list");
            Assert.True(afterFixedBuild.GetProperty("errorCount").GetInt32() > 0,
                "Expected the stale error from the earlier broken build to still be present, since od.build-solution never clears the Error List pad on its own");
        }
        finally
        {
            TryDelete(brokenFilePath);
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.error-list.clear");
            await _app.InvokeAsync("od.build-solution");
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
