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

        var status = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "Window", timeoutSeconds: 30);

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

        var status = await WaitForXamlOutlineStatusAsync(expectedRootName: "App.xaml", timeoutSeconds: 30);

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

        var status = await WaitForWpfDesignerStatusAsync(expectedRootItemType: "UserControl", timeoutSeconds: 30);

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

    // od.open-file returning "opened" only means the file's ViewContent/window was created -
    // the WPF designer's secondary tab attaches its DesignSurface (WpfViewContent.LoadInternal)
    // on a subsequent UI-thread layout pass, and that pass's timing isn't guaranteed to have
    // completed yet when this suite reuses one already-running OpenDevelopAppFixture app across
    // several tests/documents in the same collection (each test re-invoking od.open-solution on
    // an already-open solution, switching tabs among several already-open windows). Poll instead
    // of asserting immediately, matching the same wait-for-UI-state pattern already used by
    // DebuggerIntegrationTests.WaitForTopFrameLineAsync/WaitForTopFrameNameAsync.
    //
    // Two extra things this has to guard against, both discovered by running the whole class
    // together rather than one test at a time:
    //  - Waiting for outlineChildCount > 0 alone isn't enough: nested custom-control instances
    //    (e.g. MainWindow.xaml's <local:SamplePane x:Name="MainPane">) can populate their own
    //    outline node a little later than their simpler siblings, once that control's own
    //    type/assembly has been resolved - so the top-level count can already be > 0 while a
    //    later sibling is still missing. Wait for the flattened outline name count to stop
    //    growing across two consecutive polls instead of just checking it's non-zero once.
    //  - ActiveViewContent can still be a PREVIOUS test's already-open window/tab for a moment
    //    after od.open-file returns "opened" for the new one (window activation is itself
    //    asynchronous) - so the very first poll or two can report a stable, fully-populated
    //    outline that actually belongs to the wrong document entirely. Require the reported
    //    root item's type to match what this specific test just opened before accepting it.
    async Task<JsonElement> WaitForWpfDesignerStatusAsync(string expectedRootItemType, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement status = default;
        var previousCount = -1;
        while (DateTime.UtcNow < deadline)
        {
            status = await _app.InvokeAsync("od.wpf-designer.status");
            if (status.GetProperty("active").GetBoolean() &&
                status.TryGetProperty("designerLoaded", out var loaded) && loaded.GetBoolean() &&
                status.TryGetProperty("rootItemType", out var rootItemType) &&
                rootItemType.GetString() == expectedRootItemType &&
                status.TryGetProperty("outlineNames", out var names))
            {
                var count = names.GetArrayLength();
                if (count > 0 && count == previousCount)
                    break;
                previousCount = count;
            }
            else
            {
                previousCount = -1;
            }
            await Task.Delay(250);
        }
        return status;
    }

    async Task<JsonElement> WaitForXamlOutlineStatusAsync(string expectedRootName, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement status = default;
        var previousCount = -1;
        while (DateTime.UtcNow < deadline)
        {
            status = await _app.InvokeAsync("od.xaml-outline.status");
            if (status.GetProperty("active").GetBoolean() &&
                status.TryGetProperty("rootName", out var rootName) &&
                rootName.GetString() == expectedRootName &&
                status.TryGetProperty("outlineNames", out var names))
            {
                var count = names.GetArrayLength();
                if (count > 0 && count == previousCount)
                    break;
                previousCount = count;
            }
            else
            {
                previousCount = -1;
            }
            await Task.Delay(250);
        }
        return status;
    }
}
