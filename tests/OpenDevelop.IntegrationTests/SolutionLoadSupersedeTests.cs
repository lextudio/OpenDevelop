// A solution is built off the UI thread and published on it (IProjectService.OpenSolutionOrProjectAsync,
// doc/technotes/fast-mode.md). A newer open or a close must win over a load still in flight, including one
// whose build has already finished and only waits to be published: cancellation cannot reach that one, so
// it was published anyway - a closed solution reappeared, or the older of two opens replaced the newer.
// od.test.solution-publish-delay holds a finished build back long enough to act inside that window.

using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class SolutionLoadSupersedeTests : IAsyncDisposable
{
    static readonly TimeSpan PublishDelay = TimeSpan.FromSeconds(4);
    readonly OpenDevelopAppFixture _app;

    public SolutionLoadSupersedeTests(OpenDevelopAppFixture app) => _app = app;

    public async ValueTask DisposeAsync()
    {
        try { await _app.InvokeAsync("od.test.solution-publish-delay", 0); } catch { }
    }

    [Fact]
    public async Task CloseWhileALoadWaitsToBePublished_KeepsTheSolutionClosed()
    {
        var solution = _app.SolutionExplorerFixturePath;
        Assert.True((await _app.InvokeAsync("od.close-solution")).GetProperty("success").GetBoolean());
        await _app.InvokeAsync("od.test.solution-publish-delay", (int)PublishDelay.TotalMilliseconds);

        Assert.True((await _app.InvokeAsync("od.open-solution-begin", solution)).GetProperty("success").GetBoolean());
        await WaitForAsync(status => status.Loading == solution, "the load to start");
        // Long enough for this small solution's build to finish; it now waits out the publish delay.
        await Task.Delay(PublishDelay / 2);
        Assert.True((await _app.InvokeAsync("od.close-solution")).GetProperty("success").GetBoolean());

        // Past the delay: the finished build would have been published by now if it were not invalidated.
        await Task.Delay(PublishDelay + TimeSpan.FromSeconds(2));
        var status = await StatusAsync();
        Assert.Null(status.Path);
        Assert.Null(status.Loading);
    }

    [Fact]
    public async Task ANewerOpenWhileAnOlderLoadWaitsToBePublished_EndsWithTheNewerSolution()
    {
        var older = _app.SolutionExplorerFixturePath;
        var newer = _app.WpfHotReloadFixturePath;
        Assert.True((await _app.InvokeAsync("od.close-solution")).GetProperty("success").GetBoolean());
        await _app.InvokeAsync("od.test.solution-publish-delay", (int)PublishDelay.TotalMilliseconds);

        Assert.True((await _app.InvokeAsync("od.open-solution-begin", older)).GetProperty("success").GetBoolean());
        await WaitForAsync(status => status.Loading == older, "the older load to start");
        await Task.Delay(PublishDelay / 2);
        // The newer load waits twice as long, so the older finished build reaches the UI thread first, while the
        // newer one is still pending: that older one must be discarded, not published (and the newer then disposed).
        await _app.InvokeAsync("od.test.solution-publish-delay", (int)(2 * PublishDelay).TotalMilliseconds);
        Assert.True((await _app.InvokeAsync("od.open-solution-begin", newer)).GetProperty("success").GetBoolean());

        // Past the older load's delay, before the newer one's: nothing may be published yet.
        await Task.Delay(PublishDelay);
        Assert.Null((await StatusAsync()).Path);
        await WaitForAsync(status => status.Path == newer && status.Loading == null, "the newer solution to be published");
        var final = await StatusAsync();
        Assert.Equal(newer, final.Path);
        Assert.Null(final.Loading);
    }

    async Task<(string? Path, string? Loading)> StatusAsync()
    {
        var status = await _app.InvokeAsync("od.solution.status");
        static string? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return (Read(status, "path"), Read(status, "loading"));
    }

    async Task WaitForAsync(Func<(string? Path, string? Loading), bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        (string? Path, string? Loading) status = default;
        while (watch.Elapsed < TimeSpan.FromSeconds(60)) {
            status = await StatusAsync();
            if (condition(status))
                return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}; last status: path={status.Path}, loading={status.Loading}");
    }
}
