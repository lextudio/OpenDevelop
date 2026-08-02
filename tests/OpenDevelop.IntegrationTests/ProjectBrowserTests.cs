using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class ProjectBrowserTests
{
    readonly OpenDevelopAppFixture app;

    public ProjectBrowserTests(OpenDevelopAppFixture app)
    {
        this.app = app;
    }

    [Fact]
    public async Task WpfCodeBehindIsNestedAndTreeScrollsVertically()
    {
        var opened = await app.InvokeAsync("od.open-solution", app.WpfSampleSolutionPath);
        Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

        var state = await app.InvokeAsync("od.project-browser-state", "sample");
        Assert.True(state.GetProperty("success").GetBoolean(), state.ToString());
        Assert.Equal("Auto", state.GetProperty("verticalScrollBarVisibility").GetString());

        var project = state.GetProperty("project");
        var mainWindow = FindNode(project, "MainWindow.xaml");
        Assert.NotEqual(default, mainWindow.ValueKind);
        Assert.Contains(mainWindow.GetProperty("children").EnumerateArray(), child =>
            child.GetProperty("name").GetString() == "MainWindow.xaml.cs");
    }

    static JsonElement FindNode(JsonElement node, string name)
    {
        if (node.GetProperty("name").GetString() == name)
            return node;
        foreach (var child in node.GetProperty("children").EnumerateArray()) {
            var match = FindNode(child, name);
            if (match.ValueKind != JsonValueKind.Undefined)
                return match;
        }
        return default;
    }
}
