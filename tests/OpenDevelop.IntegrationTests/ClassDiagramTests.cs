using Xunit;
using System.Xml.Linq;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class ClassDiagramTests
{
    readonly OpenDevelopAppFixture _app;

    public ClassDiagramTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task ProjectContextMenu_ContainsClassDiagram()
    {
        var opened = await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
        Assert.True(opened.GetProperty("success").GetBoolean());

        var loadedAddIns = await _app.InvokeAsync("od.addins");
        var classDiagramAddIn = loadedAddIns.GetProperty("addins").EnumerateArray().FirstOrDefault(item =>
            item.GetProperty("fileName").GetString()?.Contains("ClassDiagramAddin.addin") == true);
        Assert.NotEqual(default, classDiagramAddIn.ValueKind);
        Assert.True(classDiagramAddIn.GetProperty("enabled").GetBoolean(), classDiagramAddIn.ToString());

        var menu = await _app.InvokeAsync("od.project-context-menu", "SampleApp");

        Assert.True(menu.GetProperty("success").GetBoolean(), menu.TryGetProperty("error", out var error) ? error.GetString() : null);
        Assert.Equal("SampleApp", menu.GetProperty("currentProject").GetString());
        Assert.Equal("SampleApp", menu.GetProperty("descendantCurrentProject").GetString());
        var labels = menu.GetProperty("labels").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("Class Diagram", labels);

        var model = await _app.InvokeAsync("od.class-diagram-project-model", "SampleApp");
        Assert.True(model.GetProperty("success").GetBoolean(), model.TryGetProperty("error", out error) ? error.GetString() : null);
        var sources = model.GetProperty("sourceFiles").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains(sources, path => path!.Replace('\\', '/').EndsWith("Models/Widget.cs"));
        Assert.True(model.GetProperty("typeCount").GetInt32() > 0);

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "OpenDevelop-ClassDiagram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var diagramPath = Path.Combine(temporaryDirectory, "SampleApp.cd");
        var inheritanceSource = Path.Combine(temporaryDirectory, "DiagramTypes.cs");
        File.WriteAllText(inheritanceSource, "namespace DiagramFixture; class BaseType { } class DerivedType : BaseType { }");
        new XDocument(new XElement("ClassDiagram",
            new XAttribute("Version", "2"),
            sources.Append(inheritanceSource).Select(path => new XElement("Source", new XAttribute("File", path!)))))
            .Save(diagramPath);
        try {
            var openedDiagram = await _app.InvokeAsync("od.open-file", diagramPath);
            Assert.True(openedDiagram.GetProperty("opened").GetBoolean(), openedDiagram.ToString());
            Assert.Equal("ICSharpCode.ClassDiagram.ClassDiagramViewContent", openedDiagram.GetProperty("viewContentType").GetString());

            var canvas = await _app.InvokeAsync("od.class-diagram-canvas", diagramPath);
            Assert.True(canvas.GetProperty("success").GetBoolean(), canvas.TryGetProperty("error", out error) ? error.GetString() : null);
            Assert.True(canvas.GetProperty("cardCount").GetInt32() > 0, canvas.ToString());
            Assert.True(canvas.GetProperty("fitToCanvasAvailable").GetBoolean(), canvas.ToString());
            Assert.False(canvas.GetProperty("dependenciesChecked").GetBoolean(), canvas.ToString());
            Assert.True(canvas.GetProperty("routeCount").GetInt32() > 0, canvas.ToString());
            Assert.True(canvas.GetProperty("allRoutesOrthogonal").GetBoolean(), canvas.ToString());
            Assert.True(canvas.GetProperty("allRouteEndpointsOnCardBoundaries").GetBoolean(), canvas.ToString());
            Assert.Contains(" types,", canvas.GetProperty("status").GetString());
        } finally {
            await _app.InvokeAsync("od.close-active-view");
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }
}
