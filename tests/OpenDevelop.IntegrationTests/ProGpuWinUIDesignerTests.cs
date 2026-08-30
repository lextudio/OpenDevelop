using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Covers the ProGPU in-process WinUI runtime host (WinUIXamlDesigner.ProGPUHost) — the fallback
/// backend when the out-of-process Uno child isn't deployed, and the one that predates it. Unlike
/// the Uno backend (covered directly by <c>WinUIXamlDesigner.UnoHost.Tests</c>'
/// <c>UnoDesignHostRpcTests</c>, a headless RPC-level suite with no WPF dependency), ProGPU is a
/// WPF-hosted, in-process compiled-WinUI pipeline (Roslyn compile → collectible preview assembly
/// → live WinUI visual tree rendered via <c>CompositionTarget.Rendering</c>) — a standalone test
/// executable for it could not get a portable (non-Microsoft.WindowsDesktop.App) runtimeconfig on
/// this platform the way the real OpenDevelop.exe does (see designer-common.md's convergence
/// notes), so these tests instead drive the real running app over DevFlow, the same way every
/// other WPF-hosted designer in this suite (WpfDesigner, the WinForms designer, and the Uno
/// backend's own AddInTests.cs coverage) is tested.
///
/// RegisterDevFlowActionsCommand.RuntimeSelectionVariable (OD_WINUI_RUNTIME) picks
/// which backend the app registers at startup. These tests only mean anything when the whole
/// `dotnet test` invocation is run with OD_WINUI_RUNTIME=progpu — they skip otherwise, rather
/// than silently exercising whatever the default happened to be (which is the Uno child today,
/// already covered elsewhere). Run them with:
///   OD_WINUI_RUNTIME=progpu dotnet test tests/OpenDevelop.IntegrationTests --filter-query "/*/*/ProGpuWinUIDesignerTests/*"
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class ProGpuWinUIDesignerTests : IDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _sampleDir;
    readonly string _solutionPath;
    readonly string _pagePath;
    readonly bool _skip;

    public ProGpuWinUIDesignerTests(OpenDevelopAppFixture app)
    {
        _app = app;
        // This project is DevFlow/HTTP-only and has no reference to the WinUIXamlDesigner addin
        // assembly, so the variable name is duplicated as a literal rather than referencing
        // RegisterDevFlowActionsCommand.RuntimeSelectionVariable directly - keep this in sync with
        // that constant if it's ever renamed.
        _skip = !string.Equals(
            Environment.GetEnvironmentVariable("OD_WINUI_RUNTIME"),
            "progpu", StringComparison.OrdinalIgnoreCase);
        if (_skip)
            return;

        // A private working copy, same reasoning as AddInTests' own Uno/NuGet/Git fixtures: these
        // tests edit the page (toolbox insert, property edit), and must never mutate the repo's
        // tracked sample.
        _sampleDir = Path.Combine(Path.GetTempPath(), "ProGpuWinUIDesignerTests-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.GetDirectoryName(app.ProGpuWinUISampleSolutionPath)!, _sampleDir);
        _solutionPath = Path.Combine(_sampleDir, Path.GetFileName(app.ProGpuWinUISampleSolutionPath));
        _pagePath = Path.Combine(_sampleDir, "MainPage.xaml");
    }

    public void Dispose()
    {
        if (_skip)
            return;
        try { Directory.Delete(_sampleDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task OpenXamlFile_RendersThroughProGpuPipeline()
    {
        if (_skip) {
            Assert.Skip("Set OD_WINUI_RUNTIME=progpu to run the ProGPU WinUI designer suite (default runtime order otherwise prefers the Uno child).");
            return;
        }

        var status = await OpenDesignerAsync();
        Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
        Assert.True(status.GetProperty("rendered").GetBoolean(), status.ToString());

        // The frame really came from ProGPU's compiled pipeline, not a fallback/blank surface -
        // a non-trivial pixel sample is the same proof AddInTests' own Uno-backend test
        // (OpenUnoXamlFile_UsesWinUIXamlDesignerInsteadOfWpfDesigner) uses for the sibling backend.
        var sample = await _app.InvokeAsync("od.winui-designer.render-sample");
        Assert.True(sample.GetProperty("success").GetBoolean(), sample.ToString());
        var text = sample.GetProperty("sample").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task EditProperty_ThroughPropertiesPad_RoundTripsToXamlSource()
    {
        if (_skip) {
            Assert.Skip("Set OD_WINUI_RUNTIME=progpu to run the ProGPU WinUI designer suite.");
            return;
        }

        await OpenDesignerAsync();

        // od.winui-designer.select + properties-pad.edit is a real shared-PropertyItem edit path
        // (not a direct XAML-buffer poke), same call shape AddInTests uses for the Uno backend -
        // this exercises ProGPU's own DesignItem-equivalent property write-back.
        var selected = await _app.InvokeAsync("od.winui-designer.select", "TitleText");
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());

        var edited = await _app.InvokeAsync("od.winui-designer.properties-pad.edit", "Text", "Edited by ProGPU test");
        Assert.True(edited.GetProperty("success").GetBoolean(), edited.ToString());

        var saved = await _app.InvokeAsync("od.file.save", _pagePath);
        Assert.True(saved.GetProperty("success").GetBoolean(), saved.ToString());
        Assert.Contains("Edited by ProGPU test", File.ReadAllText(_pagePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloseDocument_ReleasesThePreviewAssembly()
    {
        if (_skip) {
            Assert.Skip("Set OD_WINUI_RUNTIME=progpu to run the ProGPU WinUI designer suite.");
            return;
        }

        // ProGpuRuntimeHostBootstrap.LiveHostCount/LastPreviewRootAlive exist specifically for
        // this technote acceptance item (wpf-designer.md-adjacent doc for the WinUI designer:
        // "unloading a document releases its runtime") but aren't wired to any DevFlow action, so
        // this can only assert the document-level, DevFlow-visible half of that contract: closing
        // the file leaves no WinUI designer active. The collectible-ALC-release half stays a
        // manual/diagnostic-only check (see ProGpuRuntimeHostBootstrap's own doc comment) until a
        // DevFlow action exposes it.
        await OpenDesignerAsync();
        var closed = await _app.InvokeAsync("od.close-active-view");
        Assert.True(closed.GetProperty("success").GetBoolean(), closed.ToString());

        var status = await _app.InvokeAsync("od.winui-designer.status");
        Assert.False(status.TryGetProperty("active", out var active) && active.GetBoolean(), status.ToString());
    }

    async Task<System.Text.Json.JsonElement> OpenDesignerAsync()
    {
        var openedSolution = await _app.ReopenSolutionAsync(_solutionPath);
        Assert.True(openedSolution.GetProperty("success").GetBoolean(), openedSolution.ToString());
        var opened = await _app.InvokeAsync("od.open-file", _pagePath);
        Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());

        System.Text.Json.JsonElement status = default;
        var rendered = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            status = await _app.InvokeAsync("od.winui-designer.status");
            return status.TryGetProperty("active", out var active) && active.GetBoolean()
                && status.TryGetProperty("rendered", out var isRendered) && isRendered.GetBoolean();
        }, TimeSpan.FromSeconds(30));
        Assert.True(rendered, "WinUI designer never reported rendered: " + status);
        return status;
    }

    static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".vs")
                continue;
            CopyDirectory(dir, Path.Combine(destDir, name));
        }
    }
}
