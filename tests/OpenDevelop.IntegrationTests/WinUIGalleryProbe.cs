using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// TEMPORARY triage probe: opens the page named by OD_PROBE_PAGE (absolute path) with the Microsoft
/// WinUI child and dumps the resulting status + child log to %TEMP%\od-probe-dump.txt. Gated on
/// OD_PROBE_PAGE being set. Delete when the corpus triage is done.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class WinUIGalleryProbe
{
    readonly OpenDevelopAppFixture _app;

    public WinUIGalleryProbe(OpenDevelopAppFixture app) => _app = app;

    [Fact]
    public async Task ProbePage()
    {
        var probe = Environment.GetEnvironmentVariable("OD_PROBE_PAGE");
        if (string.IsNullOrEmpty(probe))
        {
            Assert.Skip("Set OD_PROBE_PAGE to an absolute .xaml path.");
            return;
        }
        if (!string.Equals(Environment.GetEnvironmentVariable("OD_WINUI_RUNTIME"), "microsoft", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Set OD_WINUI_RUNTIME=microsoft.");
            return;
        }
        var root = _app.WinUIGalleryRoot;
        if (root is null) { Assert.Skip("No WinUI-Gallery checkout."); return; }

        await _app.EnsureSolutionOpenAsync(Path.Combine(root, "WinUIGallery.slnx"));
        await _app.InvokeAsync("od.solution.set-configuration", "Debug-Unpackaged", "ARM64");

        // Fresh host so no prior document masks the outcome.
        var current = await _app.InvokeAsync("od.winui-designer.status");
        if (current.TryGetProperty("active", out var active) && active.GetBoolean())
        {
            await _app.InvokeAsync("od.close-all-document-views");
            await OpenDevelopAppFixture.PollUntilAsync(async () =>
            {
                var s = await _app.InvokeAsync("od.winui-designer.status");
                return !(s.TryGetProperty("active", out var a) && a.GetBoolean());
            }, TimeSpan.FromSeconds(30), initialDelayMs: 200, maxDelayMs: 1000);
        }

        await _app.InvokeAsync("od.open-file", probe);

        JsonElement status = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.winui-designer.status");
            if (!status.TryGetProperty("active", out var isActive) || !isActive.GetBoolean())
                return false;
            var text = status.TryGetProperty("status", out var t) ? t.GetString() ?? "" : "";
            return (status.TryGetProperty("rendered", out var r) && r.GetBoolean())
                || text.Contains("Cannot preview", StringComparison.OrdinalIgnoreCase)
                || text.Contains("exited while rendering", StringComparison.OrdinalIgnoreCase)
                || text.Contains("crashed", StringComparison.OrdinalIgnoreCase)
                || text.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || text.Contains("error", StringComparison.OrdinalIgnoreCase);
        }, TimeSpan.FromSeconds(75), initialDelayMs: 250, maxDelayMs: 1000);

        var child = await _app.InvokeAsync("od.winui-designer.child-log");
        var diag = await _app.InvokeAsync("od.winui-designer.diagnostics");
        // Size-stability sample: jitter shows up as different size strings across reads.
        var sizes = new System.Collections.Generic.SortedSet<string>();
        for (var i = 0; i < 6; i++)
        {
            var st = await _app.InvokeAsync("od.winui-designer.status");
            sizes.Add(st.TryGetProperty("status", out var sv) ? sv.GetString() ?? "" : "");
            await Task.Delay(700);
        }
        var stability = string.Join(" | ", sizes);
        var geometry = await _app.InvokeAsync("od.winui-designer.surface-geometry");
        var timing = await _app.InvokeAsync("od.winui-designer.render-timing");
        var profile = await _app.InvokeAsync("od.winui-designer.frame-profile");
        var pngPath = Path.Combine(Path.GetTempPath(), "od-probe-frame.png");
        var export = await _app.InvokeAsync("od.winui-designer.export-png", pngPath);
        var bounds = new System.Text.StringBuilder();
        if (status.TryGetProperty("elementNames", out var names) && names.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var n in names.EnumerateArray().Take(8))
            {
                var b = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", n.GetString());
                bounds.Append(n.GetString()).Append(" => ").Append(b.ToString()).Append('\n');
            }
        }
        // The icon-bar action reads the ACTIVE view's text editor; switch away from the designer
        // so the XAML source editor is active.
        try { await _app.InvokeAsync("od.winui-designer.switch-to-source"); } catch { }
        var iconBar = await _app.InvokeAsync("od.editor.icon-bar-bookmarks");
        var outPath = Path.Combine(Path.GetTempPath(), "od-probe-dump.txt");
        await File.WriteAllTextAsync(outPath, "STATUS:\n" + status + "\n\nSTABILITY:\n" + stability + "\n\nGEOMETRY:\n" + geometry + "\n\nTIMING:\n" + timing + "\n\nPROFILE:\n" + profile + "\n\nBOUNDS:\n" + bounds + "\n\nDIAG:\n" + diag + "\n\nICONBAR:\n" + iconBar + "\n\nCHILDLOG:\n" + child);
        Assert.True(false, "dumped to " + outPath);
    }
}
