using System.Net.Http.Json;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end tests for the OpenDevelop DevFlow agent. Each test is self-contained:
// the shared fixture launches the app once per collection, and these tests exercise
// the agent HTTP API and basic UI interaction.
[Collection("OpenDevelop app")]
public sealed class DevFlowAgentTests
{
    readonly OpenDevelopAppFixture _app;

    public DevFlowAgentTests(OpenDevelopAppFixture app)
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
        Assert.True(tree.GetProperty("elements").GetArrayLength() > 0, "UI tree is empty");
    }

    [Fact]
    public async Task InvokeActions_ListsRegisteredActions()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.GetAsync($"{_app.DevFlowBaseUrl}/api/v1/invoke/actions");
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<JsonElement>();

        // The endpoint wraps the list in {"actions": [...]}, not a bare array.
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("actions").ValueKind);
    }
}
