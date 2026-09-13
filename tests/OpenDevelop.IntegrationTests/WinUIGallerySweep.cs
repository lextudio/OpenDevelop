using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// TEMPORARY full-corpus sweep: opens every WinUI-Gallery catalog page with the Microsoft WinUI
/// child (fresh host per page) and appends one TSV row per page to %TEMP%\od-gallery-sweep.tsv,
/// classifying RENDER / EMPTY / ABSTRACT / CRASH / OTHER. Gated on OD_WINUI_GALLERY_SWEEP=1.
/// Delete after the corpus is triaged and the stable cases are promoted into
/// WinUIGalleryDesignerTests.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class WinUIGallerySweep
{
    readonly OpenDevelopAppFixture _app;

    public WinUIGallerySweep(OpenDevelopAppFixture app) => _app = app;

    [Fact]
    public async Task SweepAllCatalogPages()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("OD_WINUI_GALLERY_SWEEP"), "1", StringComparison.Ordinal))
        {
            Assert.Skip("Set OD_WINUI_GALLERY_SWEEP=1 to run the full WinUI-Gallery sweep.");
            return;
        }
        if (!string.Equals(Environment.GetEnvironmentVariable("OD_WINUI_RUNTIME"), "microsoft", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Set OD_WINUI_RUNTIME=microsoft.");
            return;
        }
        var root = _app.WinUIGalleryRoot;
        if (root is null)
        {
            Assert.Skip("No WinUI-Gallery checkout.");
            return;
        }

        await _app.EnsureSolutionOpenAsync(Path.Combine(root, "WinUIGallery.slnx"));
        await _app.InvokeAsync("od.solution.set-configuration", "Debug-Unpackaged", "ARM64");

        var samples = Path.Combine(root, "WinUIGallery", "Samples");
        var pages = Directory.GetFiles(samples, "*Page.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var limit = int.TryParse(Environment.GetEnvironmentVariable("OD_SWEEP_LIMIT"), out var l) ? l : int.MaxValue;
        if (limit < pages.Count) pages = pages.Take(limit).ToList();

        var report = Path.Combine(Path.GetTempPath(), "od-gallery-sweep.tsv");
        File.WriteAllText(report, "page\toutcome\tsize\tdetail\n");
        Console.WriteLine($"SWEEP: {pages.Count} pages -> {report}");

        foreach (var page in pages)
        {
            var rel = Path.GetRelativePath(root, page);
            string outcome, size = "", detail = "";
            try
            {
                await ResetDesignHostAsync();

                var opened = await _app.InvokeAsync("od.open-file", page);
                if (!(opened.TryGetProperty("opened", out var isOpen) && isOpen.GetBoolean()))
                {
                    outcome = "OPENFAIL";
                    detail = opened.ToString();
                }
                else
                {
                    JsonElement status = default;
                    var done = await OpenDevelopAppFixture.PollUntilAsync(async () =>
                    {
                        status = await _app.InvokeAsync("od.winui-designer.status");
                        if (!status.TryGetProperty("active", out var active) || !active.GetBoolean())
                            return false;
                        return IsRendered(status) || IsFailureStatus(GetStatusText(status));
                    }, TimeSpan.FromSeconds(75), initialDelayMs: 250, maxDelayMs: 1000);

                    if (!done)
                    {
                        outcome = "TIMEOUT";
                        detail = GetStatusText(status);
                    }
                    else if (IsRendered(status))
                    {
                        var parsed = ParseRenderedSize(GetStatusText(status));
                        size = parsed is { } s ? $"{s.Width}x{s.Height}" : "";
                        outcome = parsed is { Width: > 0, Height: > 0 } ? "RENDER" : "EMPTY";
                        detail = GetStatusText(status);
                    }
                    else
                    {
                        var text = GetStatusText(status);
                        outcome = text.Contains("abstract class", StringComparison.OrdinalIgnoreCase) ? "ABSTRACT"
                            : text.Contains("crashed", StringComparison.OrdinalIgnoreCase)
                                || text.Contains("exited while rendering", StringComparison.OrdinalIgnoreCase) ? "CRASH"
                            : "FAIL";
                        detail = text;
                    }
                }
            }
            catch (Exception e)
            {
                outcome = "EXCEPTION";
                detail = e.GetBaseException().Message;
            }

            detail = Regex.Replace(detail ?? "", @"\s+", " ");
            if (detail.Length > 300) detail = detail[..300];
            File.AppendAllText(report, $"{rel}\t{outcome}\t{size}\t{detail}\n");
            Console.WriteLine($"SWEEP: {outcome,-9} {size,-10} {rel}");
        }

        var counts = File.ReadAllLines(report).Skip(1)
            .Select(line => line.Split('\t')[1])
            .GroupBy(o => o).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine("SWEEP SUMMARY: " + string.Join(", ", counts));
    }

    async Task ResetDesignHostAsync()
    {
        var status = await _app.InvokeAsync("od.winui-designer.status");
        if (!(status.TryGetProperty("active", out var active) && active.GetBoolean()))
            return;
        await _app.InvokeAsync("od.close-all-document-views");
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var current = await _app.InvokeAsync("od.winui-designer.status");
            return !(current.TryGetProperty("active", out var isActive) && isActive.GetBoolean());
        }, TimeSpan.FromSeconds(30), initialDelayMs: 200, maxDelayMs: 1000);
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            var stats = await _app.InvokeAsync("od.winui-designer.runtime-stats");
            return !(stats.TryGetProperty("childAlive", out var alive) && alive.GetBoolean());
        }, TimeSpan.FromSeconds(30), initialDelayMs: 200, maxDelayMs: 1000);
    }

    static bool IsRendered(JsonElement status)
        => status.TryGetProperty("rendered", out var rendered) && rendered.GetBoolean();

    static bool IsFailureStatus(string text)
        => text.Contains("Cannot preview", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exited while rendering", StringComparison.OrdinalIgnoreCase)
            || text.Contains("crashed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not found", StringComparison.OrdinalIgnoreCase);

    static string GetStatusText(JsonElement status)
        => status.TryGetProperty("status", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";

    static (int Width, int Height)? ParseRenderedSize(string? statusText)
    {
        if (string.IsNullOrEmpty(statusText)) return null;
        var match = Regex.Match(statusText, @"(\d+)\s*[×xX]\s*(\d+)");
        return match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : null;
    }
}
