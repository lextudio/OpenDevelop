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

    // MinimalMSBuildEngine (a real `dotnet build` child process, per BuildTests.cs) parses its own
    // stdout/stderr for standard MSBuild diagnostic lines via a regex (DiagnosticLine in
    // MinimalMSBuildEngine.cs), reporting one BuildError per match with real file/line/column - and
    // only falls back to a single generic "Build failed (exit code non-zero)" entry if nothing
    // matched. That regex used to only handle the classic 2-number "(line,column):" shape; this
    // repo's SDK/Roslyn version instead emits a 4-number span shape - "(line,col,endLine,endCol):" -
    // for CS1002 and similar diagnostics, which silently fell through the old regex, always hitting
    // the generic fallback. Fixed by making the trailing ",endLine,endColumn" group optional. This
    // test locks in the real per-line diagnostics now reaching both od.build-solution's own
    // BuildResults.Errors and the Error List pad (TaskService, a separate code path - see
    // UIBuildFeedbackSink.ReportError).
    [Fact]
    public async Task ErrorList_OnBuildFailure_CapturesRealPerLineCompileErrors()
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

            // od.build-solution's own diagnostics now have real per-line detail, not just the one
            // generic summary.
            var buildDiagnostics = buildResult.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(buildDiagnostics, d =>
                (d.GetProperty("fileName").GetString() ?? "").Replace('\\', '/').EndsWith("ScratchBroken.cs")
                && d.GetProperty("line").GetInt32() == 3
                && d.GetProperty("errorCode").GetString() == "CS1002");

            // ...and so does the separately-populated Error List pad.
            var errorList = await _app.InvokeAsync("od.error-list");
            var tasks = errorList.GetProperty("tasks").EnumerateArray().ToList();
            var brokenFileTasks = tasks.Where(t =>
                (t.GetProperty("file").GetString() ?? "").Replace('\\', '/').EndsWith("ScratchBroken.cs")).ToList();

            Assert.NotEmpty(brokenFileTasks);
            Assert.Contains(brokenFileTasks, t => t.GetProperty("type").GetString() == "Error");
            Assert.Contains(brokenFileTasks, t => t.GetProperty("line").GetInt32() == 3);
            Assert.Contains(brokenFileTasks, t => !string.IsNullOrWhiteSpace(t.GetProperty("description").GetString()));
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
