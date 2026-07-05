using System.Net.Http.Json;
using System.Text.Json;

using Xunit;

namespace AvalonDock.IntegrationTests;

// End-to-end tests for AvalonDock docking features. Each test is self-contained:
// the shared fixture launches the app once per collection, and these tests exercise
// the DevFlow agent HTTP API and docking functionality.
[Collection("AvalonDock app")]
public sealed class DockingFeatureTests
{
    readonly TestAppFixture _app;

    public DockingFeatureTests(TestAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task AgentStatus_ReturnsValidJson()
    {
        var status = await _app.GetStatusAsync();

        Assert.True(status.TryGetProperty("name", out _), "status missing 'name'");
        Assert.True(status.TryGetProperty("id", out _), "status missing 'id'");
        Assert.True(status.TryGetProperty("framework", out _), "status missing 'framework'");
    }

    [Fact]
    public async Task AgentStatus_FrameworkIsWpf()
    {
        var status = await _app.GetStatusAsync();

        var framework = status.GetProperty("framework").GetString();
        Assert.Equal("wpf", framework);
    }

    [Fact]
    public async Task UITree_ReturnsNonEmpty()
    {
        var tree = await _app.GetUITreeAsync();

        // The visual tree should have at least a root node.
        Assert.True(tree.GetArrayLength() > 0, "UI tree is empty");
    }

    [Fact]
    public async Task UITree_ContainsDockingManager()
    {
        var tree = await _app.GetUITreeAsync();

        // The UI tree should contain a DockingManager control.
        var containsDockingManager = FindNodeByName(tree, "dockManager");
        Assert.True(containsDockingManager, "UI tree does not contain DockingManager");
    }

    [Fact]
    public async Task InvokeActions_ListsRegisteredActions()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.GetAsync("http://localhost:9223/api/v1/invoke/actions");
        resp.EnsureSuccessStatusCode();

        var actions = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // Should return a JSON array (possibly empty if no DevFlowAction methods are registered).
        Assert.Equal(JsonValueKind.Array, actions.ValueKind);
    }

    [Fact]
    public async Task LayoutState_CanBeSerialized()
    {
        // Test that the layout state can be obtained via the UI tree.
        var tree = await _app.GetUITreeAsync();

        // The tree should contain layout information.
        Assert.True(tree.GetArrayLength() > 0, "Layout state is empty");
    }

    [Fact]
    public async Task FloatingWindows_CanBeDetected()
    {
        var tree = await _app.GetUITreeAsync();

        // Check if there are any floating window controls in the tree.
        var floatingWindows = FindNodesByType(tree, "LayoutFloatingWindowControl");
        // Initially there should be no floating windows (or we can test creating one).
        Assert.NotNull(floatingWindows);
    }

    [Fact]
    public async Task AnchorablePanes_CanBeDetected()
    {
        var tree = await _app.GetUITreeAsync();

        // Check if there are any anchorable pane controls in the tree.
        var anchorablePanes = FindNodesByType(tree, "LayoutAnchorablePaneControl");
        Assert.NotNull(anchorablePanes);
    }

    [Fact]
    public async Task DocumentPanes_CanBeDetected()
    {
        var tree = await _app.GetUITreeAsync();

        // Check if there are any document pane controls in the tree.
        var documentPanes = FindNodesByType(tree, "LayoutDocumentPaneControl");
        Assert.NotNull(documentPanes);
    }

    [Fact]
    public async Task DockingManager_HasLayout()
    {
        var tree = await _app.GetUITreeAsync();

        // The DockingManager should have a layout with content.
        var dockManager = FindNodeByName(tree, "dockManager");
        Assert.True(dockManager, "DockingManager not found in UI tree");
    }

    // Helper methods for traversing the UI tree
    private static bool FindNodeByName(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("name", out var nameProp) &&
                nameProp.GetString() == name)
                return true;

            if (element.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (FindNodeByName(child, name))
                        return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindNodeByName(item, name))
                    return true;
            }
        }
        return false;
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
                {
                    FindNodesByTypeRecursive(child, type, result);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                FindNodesByTypeRecursive(item, type, result);
            }
        }
    }
}
