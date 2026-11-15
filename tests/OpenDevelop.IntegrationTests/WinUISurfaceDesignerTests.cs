using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The WinUI Surface host (<c>OD_WINUI_RUNTIME=surface</c>) driven through the IDE, the same journeys the Microsoft
/// cell has (render, source edit refresh, Properties pad edit with undo/redo, selection). The Surface host reports
/// backend <c>WinUI</c> exactly like the Microsoft host, so every test also requires the status text to name the
/// Surface host: a silent fall back to the Microsoft host must fail here, not pass.
///
/// Windows only (the Surface and its provisioner are Windows-only) and needs the Windows App Runtime. The first
/// open provisions a matched host (about a minute cold, 10-20 s warm), hence the long render wait. Run it with
/// <c>dotnet msbuild tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj -t:RunRuntimeCell -p:RuntimeCell=SurfaceWinUI</c>.
/// Written on macOS without being run: the first Windows run is the first real verification (see
/// doc/technotes/winui-designer-plan.md, "Surface host: what is left to verify").
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
[Trait(RuntimeCell.TraitName, RuntimeCell.SurfaceWinUI)]
public sealed class WinUISurfaceDesignerTests : IDisposable
{
    const string SurfaceStatusMarker = "WinUI Surface host";
    static bool SurfaceAvailable => OperatingSystem.IsWindows();

    readonly OpenDevelopAppFixture _app;
    readonly string _sampleDir;
    readonly string _solutionPath;
    readonly string _pagePath;

    public WinUISurfaceDesignerTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _sampleDir = Path.Combine(Path.GetTempPath(), "WinUISurfaceTests-" + Guid.NewGuid().ToString("N"));
        // Source only: the Surface provisioner builds the project itself, so no bin/obj copy (unlike the Microsoft cell).
        if (SurfaceAvailable)
            CopySource(Path.GetDirectoryName(app.WinUISampleSolutionPath)!, _sampleDir);
        _solutionPath = Path.Combine(_sampleDir, Path.GetFileName(app.WinUISampleSolutionPath));
        _pagePath = Path.Combine(_sampleDir, "MainPage.xaml");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sampleDir)) Directory.Delete(_sampleDir, recursive: true); }
        catch (IOException) { /* a still-running designer may hold the folder; the temp dir is disposable */ }
    }

    static void CopySource(string source, string dest)
    {
        bool Skip(string path) => path.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".od");
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories).Where(d => !Skip(d)))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => !Skip(f)))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }

    async Task<JsonElement> OpenSurfaceDesignerAsync()
    {
        var solution = await _app.ReopenSolutionAsync(_solutionPath);
        Assert.True(solution.GetProperty("success").GetBoolean(), solution.ToString());
        var opened = await _app.InvokeAsync("od.open-file", _pagePath);
        Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
        return await WaitForSurfaceRenderedAsync(TimeSpan.FromMinutes(4));
    }

    async Task<JsonElement> WaitForSurfaceRenderedAsync(TimeSpan timeout)
    {
        JsonElement status = default;
        var ready = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.winui-designer.status");
            return status.TryGetProperty("active", out var active) && active.GetBoolean()
                && status.GetProperty("rendered").GetBoolean();
        }, timeout, initialDelayMs: 200, maxDelayMs: 1000);
        if (!ready)
        {
            var details = new System.Text.StringBuilder(status.ToString());
            foreach (var category in new[] { "WinUI Designer", "IDE" })
            {
                try
                {
                    var output = await _app.InvokeAsync("od.output-text", category);
                    details.Append($"\n--- output channel '{category}':\n")
                        .Append(output.TryGetProperty("text", out var text) ? text.GetString() : output.ToString());
                }
                catch (Exception e) { details.Append($"\n--- output channel '{category}' unreadable: {e.GetType().Name}: {e.Message}"); }
            }
            Assert.Fail(details.ToString());
        }
        Assert.Equal("WinUI", status.GetProperty("framework").GetString());
        RuntimeCell.AssertWinUIBackend(status, RuntimeCell.SurfaceWinUI);
        Assert.True(status.GetProperty("status").GetString()!.Contains(SurfaceStatusMarker, StringComparison.Ordinal),
            "Rendered, but not by the Surface host (a fallback to another host would show here): " + status);
        return status;
    }

    [Fact(Skip = "The WinUI Surface host is Windows-only.", SkipUnless = nameof(SurfaceAvailable))]
    public async Task SurfaceHost_RendersWinUIPage_WithOutlineAndNoDocumentError()
    {
        var status = await OpenSurfaceDesignerAsync();
        Assert.Null(status.GetProperty("documentError").GetString());

        Assert.True((await _app.InvokeAsync("od.show-pad", "ICSharpCode.SharpDevelop.Gui.OutlinePad")).GetProperty("found").GetBoolean());
        var outline = await _app.InvokeAsync("od.outline-pad.content");
        Assert.True(outline.GetProperty("available").GetBoolean(), outline.ToString());
        var names = outline.GetProperty("names").EnumerateArray().Select(n => n.GetString()).ToArray();
        Assert.Contains("RootStack", names);
        Assert.Contains("TitleText", names);
        Assert.Contains("PrimaryButton", names);
    }

    [Fact(Skip = "The WinUI Surface host is Windows-only.", SkipUnless = nameof(SurfaceAvailable))]
    public async Task SurfaceHost_SourceEditOutsideDesigner_RefreshesDesignSurface()
    {
        await OpenSurfaceDesignerAsync();
        Assert.True((await _app.InvokeAsync("od.winui-designer.switch-to-source")).GetProperty("success").GetBoolean());
        var edit = await _app.InvokeAsync("od.search.replace", "Hello WinUI", "Edited In Source", "solution");
        Assert.True(edit.GetProperty("success").GetBoolean(), edit.ToString());

        var status = await WaitForSurfaceRenderedAsync(TimeSpan.FromSeconds(90));
        Assert.Null(status.GetProperty("documentError").GetString());

        await _app.InvokeAsync("od.file.save-all");
        Assert.Contains("Edited In Source", await File.ReadAllTextAsync(_pagePath));
    }

    [Fact(Skip = "The WinUI Surface host is Windows-only.", SkipUnless = nameof(SurfaceAvailable))]
    public async Task SurfaceHost_PropertiesPadEdit_UpdatesSource_AndUndoRedoRestoreIt()
    {
        await OpenSurfaceDesignerAsync();
        var selected = await _app.InvokeAsync("od.winui-designer.select", "PrimaryButton");
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());

        JsonElement edited = default;
        await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            edited = await _app.InvokeAsync("od.winui-designer.properties-pad.edit", "Content", "Changed through Properties");
            return edited.GetProperty("success").GetBoolean();
        }, TimeSpan.FromSeconds(20), initialDelayMs: 50, maxDelayMs: 200);
        Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());
        Assert.Equal("PrimaryButton", edited.GetProperty("selectedName").GetString());
        Assert.Equal("Hello WinUI", edited.GetProperty("before").GetString());
        Assert.Equal("Changed through Properties", edited.GetProperty("after").GetString());

        var status = await WaitForSurfaceRenderedAsync(TimeSpan.FromSeconds(90));
        Assert.True(status.GetProperty("canUndo").GetBoolean(), status.ToString());
        Assert.True((await _app.InvokeAsync("od.file.is-dirty", _pagePath)).GetProperty("isDirty").GetBoolean());

        Assert.True((await _app.InvokeAsync("od.winui-designer.undo")).GetProperty("success").GetBoolean());
        status = await WaitForSurfaceRenderedAsync(TimeSpan.FromSeconds(90));
        Assert.False(status.GetProperty("canUndo").GetBoolean(), status.ToString());
        Assert.True(status.GetProperty("canRedo").GetBoolean(), status.ToString());

        Assert.True((await _app.InvokeAsync("od.winui-designer.redo")).GetProperty("success").GetBoolean());
        await WaitForSurfaceRenderedAsync(TimeSpan.FromSeconds(90));
        Assert.True((await _app.InvokeAsync("od.file.save", _pagePath)).GetProperty("success").GetBoolean());
        Assert.Contains("Content=\"Changed through Properties\"", await File.ReadAllTextAsync(_pagePath), StringComparison.Ordinal);
    }

    [Fact(Skip = "The WinUI Surface host is Windows-only.", SkipUnless = nameof(SurfaceAvailable))]
    public async Task SurfaceHost_SelectElement_ReportsGeometryAndBounds()
    {
        await OpenSurfaceDesignerAsync();
        var geometry = await _app.InvokeAsync("od.winui-designer.surface-geometry");
        Assert.True(geometry.GetProperty("success").GetBoolean(), geometry.ToString());
        var selected = await _app.InvokeAsync("od.winui-designer.select", "PrimaryButton");
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
        var bounds = await _app.InvokeAsync("od.winui-designer.describe-element", "PrimaryButton");
        Assert.True(bounds.GetProperty("success").GetBoolean(), bounds.ToString());
        Assert.False(string.IsNullOrWhiteSpace(bounds.GetProperty("description").GetString()));
    }
}
