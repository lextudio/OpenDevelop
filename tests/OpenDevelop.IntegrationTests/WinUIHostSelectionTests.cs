using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Which WinUI design host a Microsoft WinUI project gets, each in an IDE of its own (own config directory, own environment), because the
/// host is chosen once at start-up from <c>OD_WINUI_RUNTIME</c>, the stored Tools &gt; Options value and the deployed files.
///
/// There is no fallback from the Surface host to another host: when it cannot be used the designer reports why and renders nothing. The
/// Microsoft host is used only when it is selected. Windows only (both hosts are).
/// </summary>
[Collection("94 WinUI host selection (isolated IDE)")]
public sealed class WinUIHostSelectionTests
{
    const string SurfaceMarker = "WinUI Surface host";
    const string MicrosoftMarker = "WinUI design host";

    static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Directory.Build.props")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }

    static string CopySample(string work)
    {
        // The whole sample, built output included: the Microsoft host adopts the app's runtimeconfig/deps (the Surface path provisions its own).
        var source = Path.Combine(RepositoryRoot(), "tests", "fixtures", "WinUIDesignerFixture", "Microsoft");
        var dest = Path.Combine(work, "sample");
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
        return dest;
    }

    /// <summary>A private config directory, optionally holding a stored Tools &gt; Options value for the WinUI runtime.</summary>
    static string ConfigDirectory(string work, string? storedRuntime)
    {
        var config = Directory.CreateDirectory(Path.Combine(work, "config")).FullName;
        if (storedRuntime is not null)
            File.WriteAllText(Path.Combine(config, "OpenDevelopProperties.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Properties>\n  <Property key=\"WinUIXamlDesigner.Runtime\">" + storedRuntime + "</Property>\n</Properties>");
        return config;
    }

    /// <summary>Opens the sample's page. The design view attaches only once project evaluation has settled, so the page is reopened until it does.</summary>
    static async Task<JsonElement> OpenPageAsync(IsolatedIde ide, string sampleDir, Func<JsonElement, bool> done, TimeSpan timeout)
    {
        var solution = await ide.InvokeAsync("od.open-solution", Path.Combine(sampleDir, "WinUISample.slnx"));
        Assert.True(solution.GetProperty("success").GetBoolean(), solution.ToString());
        var page = Path.Combine(sampleDir, "MainPage.xaml");
        JsonElement status = default;
        var deadline = DateTime.UtcNow + timeout;
        var lastOpen = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            status = await ide.InvokeAsync("od.winui-designer.status");
            if (status.TryGetProperty("active", out var active) && active.GetBoolean() && done(status))
                return status;
            if (DateTime.UtcNow - lastOpen > TimeSpan.FromSeconds(12) && !(status.TryGetProperty("active", out var attached) && attached.GetBoolean()))
            {
                await ide.InvokeAsync("od.close-all-document-views");
                await ide.InvokeAsync("od.open-file", page);
                lastOpen = DateTime.UtcNow;
            }
            await Task.Delay(1000);
        }
        Assert.Fail("The designer did not reach the expected state in " + timeout + ": " + status);
        return status;
    }

    static string Text(JsonElement status) => status.GetProperty("status").GetString() ?? "";

    [Fact]
    public async Task SurfaceHostFilesMissing_IsReported_AndNothingIsRenderedThroughAnotherHost()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The WinUI hosts are Windows-only.");
        var work = Path.Combine(Path.GetTempPath(), "WinUIHostSelection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var sample = CopySample(work);
            var emptyPayload = Directory.CreateDirectory(Path.Combine(work, "no-surface-host")).FullName;
            using var ide = await IsolatedIde.StartAsync(ConfigDirectory(work, null), new Dictionary<string, string?>
            {
                ["OD_WINUI_RUNTIME"] = null,                 // the default: the Surface host
                ["OD_SURFACE_HOST_ROOT"] = emptyPayload,     // a Surface payload that is not deployed
            });

            // The Microsoft host is deployed (the other cells need it), so a fallback would render through it.
            var status = await OpenPageAsync(ide, sample, s => Text(s).Contains("not installed", StringComparison.Ordinal), TimeSpan.FromMinutes(3));

            Assert.Contains("Surface host's files", Text(status));
            Assert.False(status.GetProperty("rendered").GetBoolean(), status.ToString());
            // Nothing arrives late either: wait long enough for a delayed fallback to show up.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(1000);
                var later = await ide.InvokeAsync("od.winui-designer.status");
                Assert.False(later.GetProperty("rendered").GetBoolean(), "Rendered after the Surface host was reported missing: " + later);
                Assert.DoesNotContain(MicrosoftMarker, Text(later));
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* a still-running IDE may hold a file; the temp dir is disposable */ }
        }
    }

    /// <summary>
    /// Tools &gt; Options &gt; Experimental Features, driven through <c>od.winui-designer.options</c>: the page is built from the Options
    /// tree and loaded like the dialog does, a choice is selected on its combo box (what a click sets) and saved like accepting the
    /// dialog. The stored value is what the next start resolves (<c>WinUIRuntimeSelection.Resolve</c>, covered by unit tests).
    /// Not covered: the written file and the actual restart (the IDE is killed, not closed), and pixels (the dialog itself is modal).
    /// </summary>
    [Fact]
    public async Task OptionsPage_OffersBothHosts_AndSavingAChoiceChangesTheStoredRuntime()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The WinUI hosts are Windows-only.");
        var work = Path.Combine(Path.GetTempPath(), "WinUIHostSelection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var ide = await IsolatedIde.StartAsync(ConfigDirectory(work, null), new Dictionary<string, string?> { ["OD_WINUI_RUNTIME"] = null });

            var shown = await ide.InvokeAsync("od.winui-designer.options");
            Assert.True(shown.GetProperty("success").GetBoolean(), shown.ToString());
            Assert.Equal("Experimental Features", shown.GetProperty("label").GetString());
            Assert.Equal("Experimental Features", shown.GetProperty("path").EnumerateArray().Last().GetString());
            var choices = shown.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("value").GetString()).ToList();
            Assert.Equal(new[] { "surface", "microsoft" }, choices);
            Assert.Equal("surface", shown.GetProperty("selected").GetString());
            Assert.Equal("surface", shown.GetProperty("stored").GetString());
            // The text must not promise the fallback that no longer exists, and must say what happens instead.
            var text = string.Join(" ", shown.GetProperty("texts").EnumerateArray().Select(t => t.GetString()));
            Assert.DoesNotContain("falls back", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never switches to the Microsoft host by itself", text, StringComparison.Ordinal);

            // Selecting without accepting changes nothing that is stored.
            var selected = await ide.InvokeAsync("od.winui-designer.options", "microsoft");
            Assert.Equal("microsoft", selected.GetProperty("selected").GetString());
            Assert.Equal("surface", selected.GetProperty("stored").GetString());

            // Accepting stores it, and that is what the next start resolves.
            var saved = await ide.InvokeAsync("od.winui-designer.options", "microsoft", true);
            Assert.Equal("microsoft", saved.GetProperty("stored").GetString());
            Assert.Equal("microsoft", saved.GetProperty("resolvedOnNextStart").GetString());
            var reopened = await ide.InvokeAsync("od.winui-designer.options");
            Assert.Equal("microsoft", reopened.GetProperty("selectedBefore").GetString());

            var back = await ide.InvokeAsync("od.winui-designer.options", "surface", true);
            Assert.Equal("surface", back.GetProperty("stored").GetString());
            Assert.Equal("surface", back.GetProperty("resolvedOnNextStart").GetString());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* a still-running IDE may hold a file; the temp dir is disposable */ }
        }
    }

    /// <summary>The page cannot show a stored <c>uno</c> (it offers surface and microsoft), so it displays the default; accepting the
    /// dialog without changing the choice must not overwrite it.</summary>
    [Fact]
    public async Task OptionsPage_AcceptingWithoutAChange_LeavesAStoredUnoChoiceAlone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The WinUI hosts are Windows-only.");
        var work = Path.Combine(Path.GetTempPath(), "WinUIHostSelection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var ide = await IsolatedIde.StartAsync(ConfigDirectory(work, "uno"), new Dictionary<string, string?> { ["OD_WINUI_RUNTIME"] = null });

            var shown = await ide.InvokeAsync("od.winui-designer.options");
            Assert.Equal("uno", shown.GetProperty("stored").GetString());
            Assert.Equal("surface", shown.GetProperty("selected").GetString());   // shown as the default

            var accepted = await ide.InvokeAsync("od.winui-designer.options", null!, true);
            Assert.True(accepted.GetProperty("success").GetBoolean(), accepted.ToString());
            Assert.Equal("uno", accepted.GetProperty("stored").GetString());
            Assert.Equal("uno", accepted.GetProperty("resolvedOnNextStart").GetString());

            // Choosing something does replace it.
            var changed = await ide.InvokeAsync("od.winui-designer.options", "microsoft", true);
            Assert.Equal("microsoft", changed.GetProperty("stored").GetString());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* a still-running IDE may hold a file; the temp dir is disposable */ }
        }
    }

    [Fact]
    public async Task StoredMicrosoftChoice_SelectsTheMicrosoftHost_NotTheSurface()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The WinUI hosts are Windows-only.");
        var work = Path.Combine(Path.GetTempPath(), "WinUIHostSelection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var sample = CopySample(work);
            using var ide = await IsolatedIde.StartAsync(ConfigDirectory(work, "microsoft"), new Dictionary<string, string?>
            {
                ["OD_WINUI_RUNTIME"] = null,                 // only the stored Tools > Options value decides
            });

            var status = await OpenPageAsync(ide, sample, s => s.GetProperty("rendered").GetBoolean(), TimeSpan.FromMinutes(4));

            Assert.Contains(MicrosoftMarker, Text(status));
            Assert.DoesNotContain(SurfaceMarker, Text(status));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* a still-running IDE may hold a file; the temp dir is disposable */ }
        }
    }
}
