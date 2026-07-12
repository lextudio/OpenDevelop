using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of the WPF designer AddIn work done this session (toolbox grouping, the
// Xceed-based Property Pad, the restored Outline pad wiring): open the vscode-wpf sample app's
// solution, open MainWindow.xaml, and confirm the designer surface actually loaded the XAML root
// (not a WpfDocumentError fallback), the toolbox shows grouped controls, and the Outline pad's
// element tree contains the sample's named controls. Drives the app via the od.* DevFlow actions
// (OpenDevelopDevFlowActions.cs / WpfDesignDevFlowActions.cs) since there's no native UI
// automation pipeline for the WPF-embedded DevFlow agent.
[Collection("OpenDevelop app")]
public sealed class WpfDesignerTests
{
    readonly OpenDevelopAppFixture _app;

    public WpfDesignerTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task OpenXamlFile_LoadsDesignerWithToolboxAndOutline()
    {
        var openSolutionResult = await _app.InvokeAsync("od.open-solution", _app.WpfSampleSolutionPath);
        Assert.True(openSolutionResult.GetProperty("success").GetBoolean(),
            $"OpenSolutionOrProject returned false for {_app.WpfSampleSolutionPath}");

        var xamlPath = Path.Combine(Path.GetDirectoryName(_app.WpfSampleSolutionPath)!, "MainWindow.xaml");
        var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean(), $"Failed to open {xamlPath}");

        var status = await _app.InvokeAsync("od.wpf-designer.status");

        Assert.True(status.GetProperty("active").GetBoolean());
        Assert.True(status.GetProperty("designerLoaded").GetBoolean(),
            "Expected the WPF design surface to load the XAML root (not fall back to WpfDocumentError)");
        Assert.Equal("Window", status.GetProperty("rootItemType").GetString());

        // Toolbox: the popular-controls group plus grouped controls populate WpfToolbox.Instance.
        Assert.True(status.GetProperty("toolboxItemCount").GetInt32() > 0,
            "Expected the toolbox to list at least the popular WPF controls");
        Assert.True(status.GetProperty("toolboxGroupCount").GetInt32() > 0,
            "Expected the toolbox to show at least one control group");

        // Outline pad: the flattened element tree should include MainWindow.xaml's named controls.
        var outlineNames = status.GetProperty("outlineNames").EnumerateArray()
            .Select(n => n.GetString())
            .ToList();

        Assert.True(status.GetProperty("outlineChildCount").GetInt32() > 0,
            "Expected the Outline pad's root node to have at least one child");
        Assert.Contains("PrimaryButton", outlineNames);
        Assert.Contains("MainPane", outlineNames);
    }

    [Fact]
    public async Task OpenAppXaml_ShowsCodeEditorOutline()
    {
        // App.xaml uses <Application> as root, which the WPF designer's secondary binding
        // explicitly excludes (CanAttachTo returns false for "Application"), so only the text
        // editor opens. The XamlBinding addin's XamlOutlineContentHost registers itself on the
        // TextView services via XamlTextEditorExtension.Attach, making the OutlinePad show a
        // XAML element tree instead of the designer's IOutlineNode tree.
        var openSolutionResult = await _app.InvokeAsync("od.open-solution", _app.WpfSampleSolutionPath);
        Assert.True(openSolutionResult.GetProperty("success").GetBoolean());

        var appXamlPath = Path.Combine(Path.GetDirectoryName(_app.WpfSampleSolutionPath)!, "App.xaml");
        var openFileResult = await _app.InvokeAsync("od.open-file", appXamlPath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean(), $"Failed to open {appXamlPath}");

        var status = await _app.InvokeAsync("od.xaml-outline.status");

        Assert.True(status.GetProperty("active").GetBoolean(),
            "Expected the XAML code editor outline to be active for App.xaml (text editor, not designer)");

        var rootName = status.GetProperty("rootName").GetString();
        Assert.Equal("App.xaml", rootName);

        var outlineNames = status.GetProperty("outlineNames").EnumerateArray()
            .Select(n => n.GetString())
            .ToList();

        Assert.True(status.GetProperty("rootChildCount").GetInt32() > 0,
            "Expected the root node to have at least one child element");
        Assert.Contains("Application", outlineNames);
        Assert.Contains("Application.Resources", outlineNames);
    }

    [Fact]
    public async Task OpenSamplePaneXaml_LoadsDesignerWithNestedControlTree()
    {
        // SamplePane.xaml is a UserControl with deeply nested named elements (Border→StackPanel→
        // TextBlocks, ListBox). The designer's Outline pad should reflect the full hierarchy.
        var openSolutionResult = await _app.InvokeAsync("od.open-solution", _app.WpfSampleSolutionPath);
        Assert.True(openSolutionResult.GetProperty("success").GetBoolean());

        var xamlPath = Path.Combine(Path.GetDirectoryName(_app.WpfSampleSolutionPath)!, "SamplePane.xaml");
        var openFileResult = await _app.InvokeAsync("od.open-file", xamlPath);
        Assert.True(openFileResult.GetProperty("opened").GetBoolean(), $"Failed to open {xamlPath}");

        var status = await _app.InvokeAsync("od.wpf-designer.status");

        Assert.True(status.GetProperty("active").GetBoolean());
        Assert.True(status.GetProperty("designerLoaded").GetBoolean(),
            "Expected the WPF design surface to load SamplePane.xaml");
        Assert.Equal("UserControl", status.GetProperty("rootItemType").GetString());

        var outlineNames = status.GetProperty("outlineNames").EnumerateArray()
            .Select(n => n.GetString())
            .ToList();

        Assert.True(status.GetProperty("outlineChildCount").GetInt32() > 0);
        Assert.Contains("PaneBorder", outlineNames);
        Assert.Contains("PaneStack", outlineNames);
        Assert.Contains("PaneTitle", outlineNames);
        Assert.Contains("PaneBody", outlineNames);
        Assert.Contains("PaneList", outlineNames);
        Assert.Contains("PaneListItemOne", outlineNames);
        Assert.Contains("PaneListItemTwo", outlineNames);
    }
}
