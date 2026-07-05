using System.Net.Http.Json;
using System.Text.Json;

using Xunit;

namespace AvalonDock.IntegrationTests;

/// <summary>
/// Integration tests for AvalonDock drag/drop, float/dock, hide/show,
/// layout serialization, and theme switching via the DevFlow agent.
/// </summary>
[Collection("AvalonDock app")]
public sealed class DragDropFeatureTests
{
    readonly TestAppFixture _app;

    public DragDropFeatureTests(TestAppFixture app)
    {
        _app = app;
    }

    // ── Agent capability verification ────────────────────────────────────

    [Fact]
    public async Task Agent_DragCapability_IsEnabled()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.GetAsync("http://localhost:9223/api/v1/agent/status");
        resp.EnsureSuccessStatusCode();
        var status = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // The capabilities object should include drag=true.
        Assert.True(status.TryGetProperty("capabilities", out var caps),
            "status missing 'capabilities'");
        Assert.True(caps.TryGetProperty("drag", out var drag) && drag.GetBoolean(),
            "drag capability is not enabled");
    }

    // ── Float / Dock operations ──────────────────────────────────────────

    [Fact]
    public async Task Float_And_Dock_Anchorable()
    {
        // Query initial layout — toolWindow1 should be visible and not floating.
        var layout = await QueryLayoutAsync();
        var tool1 = FindAnchorable(layout, "toolWindow1");
        Assert.NotNull(tool1);
        Assert.True(tool1.Value.GetProperty("isVisible").GetBoolean(), "toolWindow1 should be visible initially");
        Assert.False(tool1.Value.GetProperty("isFloat").GetBoolean(), "toolWindow1 should not be floating initially");

        // Float it.
        var floatResult = await _app.InvokeAsync("avd.float", "toolWindow1");
        Assert.Equal("Floated 'toolWindow1'", floatResult.GetString());

        // Verify it's now floating.
        layout = await QueryLayoutAsync();
        tool1 = FindAnchorable(layout, "toolWindow1");
        Assert.NotNull(tool1);
        Assert.True(tool1.Value.GetProperty("isFloat").GetBoolean(), "toolWindow1 should be floating after float");

        // Dock it back.
        var dockResult = await _app.InvokeAsync("avd.dock", "toolWindow1");
        Assert.Equal("Docked 'toolWindow1'", dockResult.GetString());

        // Verify it's no longer floating.
        layout = await QueryLayoutAsync();
        tool1 = FindAnchorable(layout, "toolWindow1");
        Assert.NotNull(tool1);
        Assert.False(tool1.Value.GetProperty("isFloat").GetBoolean(), "toolWindow1 should not be floating after dock");
    }

    [Fact]
    public async Task Float_UnknownAnchorable_ReturnsError()
    {
        var result = await _app.InvokeAsync("avd.float", "nonexistent-id");
        Assert.Contains("not found", result.GetString());
    }

    [Fact]
    public async Task Dock_NonFloatingAnchorable_ReturnsError()
    {
        var result = await _app.InvokeAsync("avd.dock", "toolWindow1");
        Assert.Contains("not floating", result.GetString());
    }

    // ── Hide / Show operations ───────────────────────────────────────────

    [Fact]
    public async Task Hide_And_Show_Anchorable()
    {
        // Hide toolWindow2.
        var hideResult = await _app.InvokeAsync("avd.hide", "toolWindow2");
        Assert.Equal("Hidden 'toolWindow2'", hideResult.GetString());

        // Verify hidden in layout.
        var layout = await QueryLayoutAsync();
        var tool2 = FindAnchorable(layout, "toolWindow2");
        Assert.NotNull(tool2);
        Assert.False(tool2.Value.GetProperty("isVisible").GetBoolean(), "toolWindow2 should be hidden");
        Assert.True(tool2.Value.GetProperty("isHidden").GetBoolean(), "toolWindow2 should be marked hidden");

        // Show it again.
        var showResult = await _app.InvokeAsync("avd.show", "toolWindow2");
        Assert.Equal("Shown 'toolWindow2'", showResult.GetString());

        // Verify visible again.
        layout = await QueryLayoutAsync();
        tool2 = FindAnchorable(layout, "toolWindow2");
        Assert.NotNull(tool2);
        Assert.True(tool2.Value.GetProperty("isVisible").GetBoolean(), "toolWindow2 should be visible after show");
    }

    [Fact]
    public async Task Show_NonHiddenAnchorable_ReturnsError()
    {
        var result = await _app.InvokeAsync("avd.show", "toolWindow1");
        Assert.Contains("not hidden", result.GetString());
    }

    // ── Add / Close documents ────────────────────────────────────────────

    [Fact]
    public async Task AddDocuments_ThenQuery()
    {
        // Count initial documents.
        var layoutBefore = await QueryLayoutAsync();
        var docsBefore = layoutBefore.GetProperty("documents").GetArrayLength();

        // Add two documents.
        var result = await _app.InvokeAsync("avd.add-documents");
        var raw = result.GetString();
        Assert.NotNull(raw);
        Assert.Contains("Added documents", raw);

        // Count after — should have 2 more.
        var layoutAfter = await QueryLayoutAsync();
        var docsAfter = layoutAfter.GetProperty("documents").GetArrayLength();
        Assert.Equal(docsBefore + 2, docsAfter);
    }

    [Fact]
    public async Task AddAnchorable_ThenQuery()
    {
        var layoutBefore = await QueryLayoutAsync();
        var anchBefore = layoutBefore.GetProperty("anchorables").GetArrayLength();

        var result = await _app.InvokeAsync("avd.add-anchorable", "Test Anchorable");
        var raw = result.GetString();
        Assert.NotNull(raw);
        Assert.Contains("Added anchorable", raw);

        var layoutAfter = await QueryLayoutAsync();
        var anchAfter = layoutAfter.GetProperty("anchorables").GetArrayLength();
        Assert.Equal(anchBefore + 1, anchAfter);
    }

    // ── Layout serialization / restore ───────────────────────────────────

    [Fact]
    public async Task Serialize_And_Restore_Layout()
    {
        // Serialize current layout.
        var xml = await _app.InvokeAsync("avd.layout.serialize");
        var xmlStr = xml.GetString();
        Assert.NotNull(xmlStr);
        Assert.Contains("LayoutRoot", xmlStr);
        Assert.Contains("DockingManager", xmlStr);

        // Add a document to change state.
        await _app.InvokeAsync("avd.add-documents");
        var layoutAfterAdd = await QueryLayoutAsync();
        var docsAfterAdd = layoutAfterAdd.GetProperty("documents").GetArrayLength();

        // Restore the original layout.
        var restoreResult = await _app.InvokeAsync("avd.layout.restore", xmlStr);
        Assert.Equal("Layout restored", restoreResult.GetString());

        // Verify layout is back to original document count.
        var layoutRestored = await QueryLayoutAsync();
        var docsRestored = layoutRestored.GetProperty("documents").GetArrayLength();
        Assert.True(docsRestored < docsAfterAdd,
            $"Expected fewer docs after restore ({docsRestored} < {docsAfterAdd})");
    }

    // ── Theme switching ──────────────────────────────────────────────────

    [Theory]
    [InlineData("ArcDark")]
    [InlineData("ArcLight")]
    [InlineData("VS2013Blue")]
    [InlineData("VS2013Dark")]
    [InlineData("VS2013Light")]
    [InlineData("Metro")]
    [InlineData("Aero")]
    public async Task SwitchTheme_Verifies(string themeTag)
    {
        var result = await _app.InvokeAsync("avd.switch-theme", themeTag);
        Assert.Equal($"Switched to '{themeTag}'", result.GetString());
    }

    [Fact]
    public async Task SwitchTheme_Unknown_ReturnsError()
    {
        var result = await _app.InvokeAsync("avd.switch-theme", "NonexistentTheme");
        Assert.Contains("Unknown theme", result.GetString());
    }

    // ── New floating window ──────────────────────────────────────────────

    [Fact]
    public async Task NewFloatingWindow_CreatesFloatingAnchorable()
    {
        var layoutBefore = await QueryLayoutAsync();
        var floatsBefore = layoutBefore.GetProperty("floatingWindows").GetArrayLength();

        var result = await _app.InvokeAsync("avd.new-floating", "My Floating Window");
        var raw = result.GetString();
        Assert.NotNull(raw);
        Assert.Contains("Created floating", raw);

        var layoutAfter = await QueryLayoutAsync();
        var floatsAfter = layoutAfter.GetProperty("floatingWindows").GetArrayLength();
        Assert.True(floatsAfter > floatsBefore,
            $"Expected more floating windows ({floatsAfter} > {floatsBefore})");
    }

    // ── OS-level drag (Windows only) ────────────────────────────────────

    [Fact]
    public async Task Drag_GlobalCoordinates_Succeeds()
    {
        if (!OperatingSystem.IsWindows())
            return; // skip on non-Windows

        // Create a floating window first so we have something to drag.
        var floatResult = await _app.InvokeAsync("avd.new-floating", "Drag Test Window");
        Assert.Contains("Created floating", floatResult.GetString());

        // Query layout to get floating window info.
        var layout = await QueryLayoutAsync();
        var floats = layout.GetProperty("floatingWindows");
        Assert.True(floats.GetArrayLength() > 0, "No floating windows found");

        // Use the DevFlow drag API with global coordinates.
        // Drag from (400, 300) to (500, 400) — a 100px diagonal drag.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var dragBody = new
        {
            fromX = 400.0,
            fromY = 300.0,
            toX = 500.0,
            toY = 400.0,
            steps = 12,
            global = true
        };
        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(dragBody),
            System.Text.Encoding.UTF8,
            "application/json");
        using var resp = await http.PostAsync("http://localhost:9223/api/v1/ui/actions/drag", content);

        // Should succeed (200) or return 501 if drag not supported.
        // On Windows with the new WpfAgentService override, it should return 200.
        if (resp.IsSuccessStatusCode)
        {
            var dragResult = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(dragResult.TryGetProperty("ok", out var ok), "drag response missing 'ok'");
            Assert.True(ok.GetBoolean(), "drag returned ok=false");
        }
        else
        {
            // 501 = drag not supported (e.g. running on macOS agent)
            Assert.Equal(System.Net.HttpStatusCode.NotImplemented, resp.StatusCode);
        }
    }

    // ── UI tree structural tests ─────────────────────────────────────────

    [Fact]
    public async Task UITree_ContainsExpectedAnchorablePanes()
    {
        var tree = await _app.GetUITreeAsync();

        // Should contain anchorable pane controls.
        var panes = FindNodesByType(tree, "LayoutAnchorablePaneControl");
        Assert.NotNull(panes);
        Assert.True(panes!.Count > 0, "No LayoutAnchorablePaneControl found in UI tree");
    }

    [Fact]
    public async Task UITree_ContainsDocumentPanes()
    {
        var tree = await _app.GetUITreeAsync();

        var panes = FindNodesByType(tree, "LayoutDocumentPaneControl");
        Assert.NotNull(panes);
        Assert.True(panes!.Count > 0, "No LayoutDocumentPaneControl found in UI tree");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<JsonElement> QueryLayoutAsync()
    {
        var raw = await _app.InvokeAsync("avd.query.layout");
        return JsonDocument.Parse(raw.GetString()!).RootElement;
    }

    private static JsonElement? FindAnchorable(JsonElement layout, string contentId)
    {
        if (!layout.TryGetProperty("anchorables", out var arr)) return null;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.TryGetProperty("contentId", out var cid) &&
                cid.GetString() == contentId)
                return item;
        }
        return null;
    }

    private static List<JsonElement>? FindNodesByType(JsonElement element, string type)
    {
        var result = new List<JsonElement>();
        FindNodesByTypeRecursive(element, type, result);
        return result;
    }

    private static void FindNodesByTypeRecursive(JsonElement element, string type, List<JsonElement> result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var typeProp) &&
                typeProp.GetString()?.Contains(type) == true)
                result.Add(element);

            if (element.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                    FindNodesByTypeRecursive(child, type, result);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                FindNodesByTypeRecursive(item, type, result);
        }
    }
}
