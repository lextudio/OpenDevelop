using System.Text;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// TEMPORARY triage probe: opens the page named by OD_WPF_PROBE_PAGE (relative to the WPFGallery
/// project folder) with the Microsoft WPF child and dumps status/child-log/bounds plus a PNG of the
/// rendered frame to %TEMP%. Gated on OD_WPF_PROBE_PAGE being set. Delete when triage is done.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class WpfGalleryProbe
{
    readonly OpenDevelopAppFixture _app;

    public WpfGalleryProbe(OpenDevelopAppFixture app) => _app = app;

    [Fact]
    public async Task ProbePage()
    {
        var probe = Environment.GetEnvironmentVariable("OD_WPF_PROBE_PAGE");
        if (string.IsNullOrEmpty(probe))
        {
            Assert.Skip("Set OD_WPF_PROBE_PAGE to a WPFGallery page path.");
            return;
        }
        var root = _app.WpfGalleryRoot;
        if (root is null) { Assert.Skip("No WPF-Samples checkout."); return; }

        await _app.EnsureSolutionOpenAsync(Path.Combine(root, "WPFGallery.sln"));
        var pagePath = Path.Combine(root, probe.Replace('/', Path.DirectorySeparatorChar));
        await _app.InvokeAsync("od.open-file", pagePath);

        var outPath = Path.Combine(Path.GetTempPath(), "od-wpf-probe-dump.txt");
        var pngPath = Path.Combine(Path.GetTempPath(), "od-wpf-probe-frame.png");
        var sb = new StringBuilder();
        try
        {
            JsonElement status = default;
            await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                status = await _app.InvokeAsync("od.wpf-designer.status");
                return status.TryGetProperty("designerLoaded", out var loaded) && loaded.GetBoolean();
            }, TimeSpan.FromSeconds(45), initialDelayMs: 150, maxDelayMs: 500);

            await Task.Delay(800);
            status = await _app.InvokeAsync("od.wpf-designer.status");
            sb.Append("STATUS:\n").Append(status).Append("\n\n");

            sb.Append("CHILDLOG:\n").Append(await _app.InvokeAsync("od.wpf-designer.child-log")).Append("\n\n");

            var export = await _app.InvokeAsync("od.wpf-designer.export-frame", pngPath);
            sb.Append("EXPORT:\n").Append(export).Append("\n\n");

            var bounds = new StringBuilder();
            if (status.TryGetProperty("outlineNames", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in array.EnumerateArray())
                {
                    var name = n.GetString();
                    if (string.IsNullOrEmpty(name)) continue;
                    var b = await _app.InvokeAsync("od.wpf-designer.query-element-screen-bounds", name);
                    bounds.Append(name).Append(" => ").Append(b.ToString()).Append('\n');
                }
            }
            sb.Append("BOUNDS:\n").Append(bounds);
        }
        finally
        {
            await File.WriteAllTextAsync(outPath, sb.ToString());
        }
        Assert.Fail("dumped to " + outPath + " and " + pngPath);
    }
}
