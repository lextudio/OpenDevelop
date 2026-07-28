using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class XmlEditorTests
{
    readonly OpenDevelopAppFixture _app;

    public XmlEditorTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    async Task OpenSolutionAndFile()
    {
        await _app.InvokeAsync("od.open-solution", _app.FixtureSolutionPath);
    }

    [Fact]
    public async Task OpenXmlFile_AttachesXmlTreeView()
    {
        await OpenSolutionAndFile();

        var open = await _app.InvokeAsync("od.open-file", _app.XmlFixtureFilePath);
        Assert.True(open.GetProperty("opened").GetBoolean(), $"Failed to open {_app.XmlFixtureFilePath}");

        var status = await _app.InvokeAsync("od.xml-tree-status");
        Assert.True(status.GetProperty("found").GetBoolean(), status.ToString());
        Assert.Equal("ICSharpCode.XmlEditor.XmlTreeView", status.GetProperty("viewType").GetString());
    }

    [Fact]
    public async Task OpenXmlFile_XmlTreeViewTabTitleIsNotEmpty()
    {
        await OpenSolutionAndFile();

        var open = await _app.InvokeAsync("od.open-file", _app.XmlFixtureFilePath);
        Assert.True(open.GetProperty("opened").GetBoolean());

        var status = await _app.InvokeAsync("od.xml-tree-status");
        Assert.True(status.GetProperty("found").GetBoolean(), status.ToString());
        Assert.False(string.IsNullOrEmpty(status.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task OpenNonXmlFile_DoesNotAttachXmlTreeView()
    {
        await OpenSolutionAndFile();

        var csFile = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_app.FixtureSolutionPath)!, "PassTests.cs");
        var open = await _app.InvokeAsync("od.open-file", csFile);
        Assert.True(open.GetProperty("opened").GetBoolean());

        var status = await _app.InvokeAsync("od.xml-tree-status");
        Assert.False(status.GetProperty("found").GetBoolean());
    }
}
