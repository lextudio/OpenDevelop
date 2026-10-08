// Regression gates for the fast-mode work (doc/technotes/fast-mode.md). They pin the behaviour
// behind the numbers rather than wall-clock times, which depend on the machine: an idle window
// must not keep the CPU busy (a layout clip compared by reference once kept it at ~90%), and a
// second build of an unchanged solution must skip every project (the fast up-to-date check once
// skipped a third of OpenDevelop.Mvp and a no-op build took ~150 s).

using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class PerformanceGateTests : IAsyncDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _dir;
    readonly string _slnx;

    public PerformanceGateTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "PerformanceGateTests-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.GetDirectoryName(app.SlnxFixturePath)!, _dir);
        _slnx = Path.Combine(_dir, Path.GetFileName(app.SlnxFixturePath));
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task IdleWindow_DoesNotKeepTheCpuBusy()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _slnx)).GetProperty("success").GetBoolean());
        // Wait for this open's own completion milestones, not a fixed delay: on a slow machine the
        // Roslyn push or the project watchers can still be running after any delay picked here, and
        // that is loading, not an idle window. od.open-solution starts a fresh timeline scope, so
        // these marks cannot come from an earlier open.
        await WaitForSolutionOpenToSettleAsync(TimeSpan.FromMinutes(3));
        // The CPU time comes from the app itself: the fixture's process handle is `dotnet run`,
        // which sits idle while the real app (its child) runs.
        var before = (await _app.InvokeAsync("od.process.cpu")).GetProperty("totalProcessorTimeMs").GetDouble();
        var watch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(8));
        var after = (await _app.InvokeAsync("od.process.cpu")).GetProperty("totalProcessorTimeMs").GetDouble();
        var busy = (after - before) / watch.Elapsed.TotalMilliseconds;

        // Measured 3-7% of one core; the regression was ~90%. 40% leaves room for a slow machine.
        Assert.True(busy < 0.40, $"An idle OpenDevelop used {busy:P0} of a core over {watch.Elapsed.TotalSeconds:F1} s.");
    }

    [Fact]
    public async Task SecondBuildOfAnUnchangedSolution_SkipsEveryProject()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _slnx)).GetProperty("success").GetBoolean());
        var first = await _app.InvokeAsync("od.build-solution");
        Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());

        var second = await _app.InvokeAsync("od.build-solution");
        Assert.True(second.GetProperty("success").GetBoolean(), second.ToString());
        var build = (await _app.InvokeAsync("od.perf.timeline")).GetProperty("scopes").GetProperty("build")
            .EnumerateArray().Select(mark => mark.GetProperty("name").GetString()).ToList();

        Assert.Contains("project-up-to-date", build);
        Assert.DoesNotContain("project-built", build);
        Assert.DoesNotContain("built-in-one-process", build);
    }

    static readonly string[] SettledMilestones = { "roslyn-projects-pushed", "project-watchers-started" };

    async Task WaitForSolutionOpenToSettleAsync(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        string[] seen = Array.Empty<string>();
        while (watch.Elapsed < timeout) {
            var timeline = await _app.InvokeAsync("od.perf.timeline");
            if (timeline.GetProperty("scopes").TryGetProperty("solution-open", out var marks)) {
                seen = marks.EnumerateArray().Select(mark => mark.GetProperty("name").GetString() ?? "").ToArray();
                if (SettledMilestones.All(seen.Contains))
                    return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        Assert.Fail($"The solution open did not finish within {timeout.TotalSeconds:F0} s; milestones so far: {string.Join(", ", seen)}.");
    }

    static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj" or ".od")
                continue;
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".od"))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }
    }
}
