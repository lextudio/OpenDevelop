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
    internal const string SurfaceStatusMarker = "WinUI Surface host";
    // public: xUnit resolves SkipUnless by name and only finds a public static member.
    public static bool SurfaceAvailable => OperatingSystem.IsWindows();

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

        // A document that renders must not leave runtime-graph complaints that do not apply to the Surface child in the designer's
        // output pad: the Uno 7 requirement belongs to Uno projects, and the Surface child never adopts the app's runtime graph.
        var pad = await _app.InvokeAsync("od.output-text", "WinUI Designer");
        var padText = pad.TryGetProperty("text", out var padValue) ? padValue.GetString() ?? "" : "";
        foreach (var misleading in new[] { "Uno designer requires", "cannot run this configuration's runtime graph", "pins .NET" })
            Assert.DoesNotContain(misleading, padText, StringComparison.Ordinal);

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
        // surface-geometry has no "success" member (its contract is available/frame/selection/handle/element): a rendered
        // frame must have a size before anything is selected, and the selection must hug the element afterwards.
        var frameOnly = await _app.InvokeAsync("od.winui-designer.surface-geometry");
        Assert.True(frameOnly.GetProperty("available").GetBoolean(), frameOnly.ToString());
        Assert.True(frameOnly.GetProperty("frame").GetProperty("width").GetDouble() > 0
            && frameOnly.GetProperty("frame").GetProperty("height").GetDouble() > 0, frameOnly.ToString());
        var selected = await _app.InvokeAsync("od.winui-designer.select", "PrimaryButton");
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());
        OpenDevelopAppFixture.AssertSurfaceGeometryConsistent(await _app.InvokeAsync("od.winui-designer.surface-geometry"), "Surface host");
        var bounds = await _app.InvokeAsync("od.winui-designer.describe-element", "PrimaryButton");
        Assert.True(bounds.GetProperty("success").GetBoolean(), bounds.ToString());
        Assert.False(string.IsNullOrWhiteSpace(bounds.GetProperty("description").GetString()));
    }

    /// <summary>
    /// The Toolbox-to-design-surface path with a REAL OS mouse drag (press, stepped move, release), as the Uno and Microsoft cells
    /// have it: the pad's own drag, the shared surface's drop handling over the Surface frame, and resolving the drop point to the
    /// container aimed at. The Surface has no document model, so the insertion must arrive as a source edit in the parent.
    /// </summary>
    [Fact(Skip = "The WinUI Surface host is Windows-only.", SkipUnless = nameof(SurfaceAvailable))]
    public async Task SurfaceHost_DragToolboxItemOntoDesignSurface_InsertsIntoDroppedContainer()
    {
        var status = await OpenSurfaceDesignerAsync();
        var namesBefore = status.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()).ToList();

        // The shared ToolsPad realizes its rows only once it has been shown.
        await _app.InvokeAsync("od.show-pad", "Tools");
        await _app.InvokeAsync("od.activate");

        var toolboxBounds = await _app.InvokeAsync("od.winui-designer.toolbox.query-item-bounds", "TextBlock");
        Assert.True(toolboxBounds.GetProperty("success").GetBoolean(), toolboxBounds.ToString());
        var target = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", "PrimaryButton");
        Assert.True(target.GetProperty("success").GetBoolean(), target.ToString());

        // Aim at the part of the button that is inside the visible design viewport: a small IDE window clips it (see the click test in
        // AddInTests), and a drop outside the viewport never reaches the surface.
        var geometry = await _app.InvokeAsync("od.winui-designer.surface-geometry");
        double toX = target.GetProperty("centerX").GetDouble(), toY = target.GetProperty("centerY").GetDouble();
        if (geometry.TryGetProperty("viewport", out var viewport) && viewport.GetProperty("width").GetDouble() > 0)
        {
            double left = Math.Max(target.GetProperty("x").GetDouble(), viewport.GetProperty("x").GetDouble());
            double top = Math.Max(target.GetProperty("y").GetDouble(), viewport.GetProperty("y").GetDouble());
            double right = Math.Min(target.GetProperty("x").GetDouble() + target.GetProperty("width").GetDouble(),
                viewport.GetProperty("x").GetDouble() + viewport.GetProperty("width").GetDouble());
            double bottom = Math.Min(target.GetProperty("y").GetDouble() + target.GetProperty("height").GetDouble(),
                viewport.GetProperty("y").GetDouble() + viewport.GetProperty("height").GetDouble());
            Assert.True(right > left && bottom > top, "PrimaryButton is entirely outside the visible design viewport: " + target + " vs " + viewport);
            toX = (left + right) / 2;
            toY = (top + bottom) / 2;
        }
        double fromX = toolboxBounds.GetProperty("centerX").GetDouble(), fromY = toolboxBounds.GetProperty("centerY").GetDouble();

        var reset = await _app.InvokeAsync("od.pointer-events", true);
        Assert.True(reset.GetProperty("success").GetBoolean(), reset.ToString());
        var pressed = await _app.PressPointerAsync(fromX, fromY);
        Assert.True(pressed.GetProperty("ok").GetBoolean(), pressed.ToString());
        var delivery = await _app.InvokeAsync("od.pointer-events", false);
        Assert.True(delivery.GetProperty("count").GetInt32() > 0, "The toolbox press did not reach the main window: " + delivery);
        AddInTests.AssertPointerDeliveryMatchesScreenPoint(delivery, fromX, fromY);
        await _app.DragPointerInStepsAsync(fromX, fromY, toX, toY, steps: 8);

        JsonElement after = default;
        var inserted = await OpenDevelopAppFixture.PollUntilAsync(async () =>
        {
            after = await _app.InvokeAsync("od.winui-designer.status");
            return after.GetProperty("elementNames").GetArrayLength() > namesBefore.Count;
        }, TimeSpan.FromSeconds(30), initialDelayMs: 100, maxDelayMs: 500);
        Assert.True(inserted, "A real toolbox drag should have added an element: " + after + "\npointer events: " + delivery);
        Assert.Contains(SurfaceStatusMarker, after.GetProperty("status").GetString() ?? "", StringComparison.Ordinal);

        var newName = after.GetProperty("elementNames").EnumerateArray().Select(n => n.GetString()).Except(namesBefore).Single();
        Assert.StartsWith("TextBlock", newName);

        // A genuine, well-formed source edit that landed beside PrimaryButton (the drop target's container).
        await _app.InvokeAsync("od.file.save-all");
        var onDisk = await File.ReadAllTextAsync(_pagePath);
        System.Xml.Linq.XDocument document = null;
        try { document = System.Xml.Linq.XDocument.Parse(onDisk); }
        catch (System.Xml.XmlException exception) { Assert.Fail($"Drop produced malformed XAML: {exception.Message}\n{onDisk}"); }
        System.Xml.Linq.XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        var created = document.Descendants().Where(e => (string)e.Attribute(xNs + "Name") == newName).ToList();
        Assert.True(created.Count == 1, $"Expected exactly one element named '{newName}', found {created.Count}:\n{onDisk}");
        Assert.Equal("TextBlock", created[0].Name.LocalName);
        var primaryButton = document.Descendants().Single(e => (string)e.Attribute(xNs + "Name") == "PrimaryButton");
        Assert.Equal(primaryButton.Parent, created[0].Parent);
    }
}
