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

    [Fact]
    public async Task ErrorList_CapturesRealCompileErrorWithFileAndLine()
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

            var errorList = await _app.InvokeAsync("od.error-list");
            Assert.True(errorList.GetProperty("errorCount").GetInt32() > 0);

            var tasks = errorList.GetProperty("tasks").EnumerateArray().ToList();
            var brokenFileTasks = tasks.Where(t =>
                (t.GetProperty("file").GetString() ?? "").Replace('\\', '/').EndsWith("ScratchBroken.cs")).ToList();

            Assert.True(brokenFileTasks.Count > 0, "No task matched ScratchBroken.cs. Full error-list: " + errorList.GetRawText());
            Assert.Contains(brokenFileTasks, t => t.GetProperty("type").GetString() == "Error");
            Assert.Contains(brokenFileTasks, t => t.GetProperty("line").GetInt32() > 0);
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
