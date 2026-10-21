using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class RazorHighlightingIntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public RazorHighlightingIntegrationTests(OpenDevelopAppFixture app) => _app = app;

    [Fact]
    public async Task Razor_HighlightsMarkupStylesScriptsAndComponentCode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opendevelop-razor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "RazorFixture.csproj");
        var razorPath = Path.Combine(directory, "StatusCard.razor");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(razorPath, "<div class=\"status-card\">@Title</div>\n<style>.status-card { color: red; }</style>\n<script>const title = 'status';</script>\n@code { public string Title { get; set; } = \"OpenDevelop\"; public int Count { get; set; } }\n@functions { public void Render() { Action render = () => { var nested = \"value\"; }; return; } }\n@{ var inlineCode = 1; }\n@(Title)\n<div id=\"after-code\">plain markup</div>\n");
        try
        {
            var opened = await _app.ReopenSolutionAsync(projectPath);
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            var editor = await _app.InvokeAsync("od.open-file", razorPath);
            Assert.True(editor.GetProperty("opened").GetBoolean(), editor.ToString());

            var highlighting = await _app.InvokeAsync("od.active-editor.highlighting");
            Assert.True(highlighting.GetProperty("success").GetBoolean(), highlighting.ToString());
            Assert.Equal("ASP.NET Core Razor", highlighting.GetProperty("definition").GetString());
            AssertExactColor(highlighting, "<div class=\"status-card\">", "HtmlTag");
            AssertExactColor(highlighting, "color", "CssProperty");
            AssertExactColor(highlighting, "red", "CssValue");
            AssertExactColor(highlighting, "const", "JavaScriptKeyWords");
            AssertPrefixColor(highlighting, "@code", "Directive");
            AssertPrefixColor(highlighting, "@functions", "Directive");
            AssertExactColor(highlighting, "public", "Keyword");
            AssertExactColor(highlighting, "string", "Type");
            AssertExactColor(highlighting, "int", "Type");
            AssertExactColor(highlighting, "void", "Type");
            AssertExactColor(highlighting, "return", "Keyword");
            AssertExactColor(highlighting, "<div id=\"after-code\">", "HtmlTag");
        }
        finally
        {
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    static void AssertExactColor(JsonElement highlighting, string text, string color)
    {
        Assert.Contains(highlighting.GetProperty("sections").EnumerateArray(), section =>
            section.GetProperty("text").GetString() == text
            && section.GetProperty("color").GetString() == color);
    }

    static void AssertPrefixColor(JsonElement highlighting, string text, string color)
    {
        Assert.Contains(highlighting.GetProperty("sections").EnumerateArray(), section =>
            section.GetProperty("text").GetString()!.StartsWith(text, StringComparison.Ordinal)
            && section.GetProperty("color").GetString() == color);
    }
}
