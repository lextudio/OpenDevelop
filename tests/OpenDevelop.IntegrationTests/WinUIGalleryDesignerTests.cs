using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Drives the MICROSOFT WinUI design host (<c>WinUIXamlDesigner.MicrosoftHost</c>) against the real
/// WinUI-Gallery application - a corpus far larger and more hostile than the tiny
/// <c>src/Samples/WinUISample</c> fixture the <c>WinUIOnly_*</c> tests in <c>AddInTests</c> use.
///
/// The Gallery is a Windows App SDK app whose pages are all wrapped in the app's own
/// <c>controls:ControlExample</c> and whose controls are declared with <c>x:Bind</c>; seeing them
/// render requires the host to serve the app's compiled resources (.pri + loose .xbf) and to apply
/// the document repairs documented in <c>doc/technotes/winui-designer.md</c>
/// ("Real WinUI-Gallery preview: seven stacked causes"). These tests are the regression net for
/// that work.
///
/// Optional and gated:
///   * skips unless <c>OD_WINUI_RUNTIME=microsoft</c> (the Gallery must run on the real Windows App
///     SDK runtime, not Uno/ProGPU), and
///   * skips unless a WinUI-Gallery checkout is found (<c>OD_WINUI_GALLERY_ROOT</c>, or a sibling
///     <c>WinUI-Gallery</c> directory at any ancestor of the test binary).
///
/// The Gallery must be built once for the configuration/platform under test; the defaults match the
/// checkout used during the original investigation (<c>Debug-Unpackaged</c> / <c>ARM64</c> on arm64,
/// <c>x64</c> otherwise). Set <c>OD_WINUI_GALLERY_BUILD=1</c> to build it through DevFlow first.
/// Override via <c>OD_WINUI_GALLERY_CONFIGURATION</c> / <c>OD_WINUI_GALLERY_PLATFORM</c>.
///
/// Assertion philosophy, straight from the investigation: <c>rendered: true</c> alone is NOT proof
/// anything was drawn - a page can report success at 0x0. Every "renders" case therefore also
/// asserts a non-zero rendered size, and every "fails" case asserts a real diagnostic AND that the
/// child is still alive (a crash is a different, worse failure mode).
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
[Trait("DesignerBackend", "Microsoft")]
public sealed class WinUIGalleryDesignerTests
{
    const string SolutionFileName = "WinUIGallery.slnx";
    const string RuntimeVariable = "OD_WINUI_RUNTIME";

    readonly OpenDevelopAppFixture _app;
    readonly string? _galleryRoot;
    readonly string _configuration;
    readonly string _platform;
    readonly bool _skip;
    readonly string _skipReason = "";

    public WinUIGalleryDesignerTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _galleryRoot = app.WinUIGalleryRoot;
        _configuration = Environment.GetEnvironmentVariable("OD_WINUI_GALLERY_CONFIGURATION") ?? "Debug-Unpackaged";
        _platform = Environment.GetEnvironmentVariable("OD_WINUI_GALLERY_PLATFORM")
            ?? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "x64");

        if (!string.Equals(Environment.GetEnvironmentVariable(RuntimeVariable), "microsoft", StringComparison.OrdinalIgnoreCase))
        {
            _skip = true;
            _skipReason = "Set OD_WINUI_RUNTIME=microsoft to run the WinUI-Gallery corpus against the Microsoft WinUI design host.";
        }
        else if (_galleryRoot is null)
        {
            _skip = true;
            _skipReason = "No WinUI-Gallery checkout found; set OD_WINUI_GALLERY_ROOT to a directory containing " + SolutionFileName + ".";
        }
    }

    // Each row: page (relative to the Gallery root), comma-separated x:Names that must be present in
    // the loaded document, and an optional x:Name whose rendered on-screen bounds must be non-zero
    // (a live-tree proof that goes beyond the document's own namescope).
    public static TheoryData<string, string, string> RenderPages => new() {
        { "WinUIGallery/Samples/Button/ButtonPage.xaml", "Button1,Button2", "Button1" },
        { "WinUIGallery/Samples/CheckBox/CheckBoxPage.xaml", "OptionsAllCheckBox,TwoStateOutput", "OptionsAllCheckBox" },
        { "WinUIGallery/Samples/ComboBox/ComboBoxPage.xaml", "Combo1,Combo2,Combo3", "Combo1" },
        { "WinUIGallery/Samples/Slider/SliderPage.xaml", "Slider1,Slider2", "Slider1" },
        { "WinUIGallery/Samples/ToggleSwitch/ToggleSwitchPage.xaml", "ToggleSwitch2", "ToggleSwitch2" },
        { "WinUIGallery/Samples/ProgressBar/ProgressBarPage.xaml", "ProgressBar2", "ProgressBar2" },
        { "WinUIGallery/Samples/RadioButton/RadioButtonPage.xaml", "BackgroundRadioButtons,BorderRadioButtons", "BackgroundRadioButtons" },
        { "WinUIGallery/Samples/Expander/ExpanderPage.xaml", "Expander1", "Expander1" },
        { "WinUIGallery/Samples/TreeView/TreeViewPage.xaml", "sampleTreeView", "sampleTreeView" },
        { "WinUIGallery/Samples/AppBarButton/AppBarButtonPage.xaml", "Button1,Button2", "Button1" },
        // These two have no named inner control (the sample control is the ControlExample's direct
        // content and carries no x:Name), so they assert the document loaded and drew, with no
        // per-element probe.
        { "WinUIGallery/Samples/TextBox/TextBoxPage.xaml", "Example1,Example2,Example3,Example4", "" },
        { "WinUIGallery/Samples/Pivot/PivotPage.xaml", "Example1", "" },
        // AutoSuggestBox's framework default template hosts a Popup that natively crashes the design
        // host; the Microsoft host now rewrites these elements with a Popup-free design-time
        // template (FrameworkDefaultResources.ApplyDesignTimeControlTemplates), so they render.
        { "WinUIGallery/Samples/AutoSuggestBox/AutoSuggestBoxPage.xaml", "Control1,Control2", "Control1" },
        { "WinUIGallery/Samples/NavigationView/NavigationViewPage.xaml", "nvSample,contentFrame", "contentFrame" },
        // Fixed by the Windows App SDK 2.4.0 upgrade (SplitMenuFlyoutItem / SystemBackdropElement
        // do not exist before 2.x).
        { "WinUIGallery/Samples/MenuFlyout/MenuFlyoutPage.xaml", "Example1,Control1", "" },
        { "WinUIGallery/Samples/SystemBackdropElement/SystemBackdropElementPage.xaml", "Example1,DynamicBackdropHost", "" },
        // Fixed by the accent-palette work (FrameworkDefaultResources.ApplyAccentPalette): the page
        // resolves AccentAcrylicBackgroundFillColorDefaultBrush, whose TintColor used to fail.
        { "WinUIGallery/Samples/XamlStyles/XamlStylesPage.xaml", "", "" },
        // Fixed by InjectDesignData dropping design-time (d:/mc:) markup by namespace: the surviving
        // d:Source used to make the combined document unparseable and surface as a bogus x:Class error.
        { "WinUIGallery/Samples/SemanticZoom/SemanticZoomPage.xaml", "Example1,cvsGroups", "" },
        // Fixed by ConditionalXmlnsStripper dropping conditional attributes (two mutually-exclusive
        // conditions collapsed to the same attribute name and failed the parse).
        { "WinUIGallery/Samples/CustomXamlConditionals/CustomXamlConditionalsPage.xaml", "", "" },
        // Fixed by the RefreshContainer design-time template (its default template presented nothing
        // offscreen, so the page measured 0x0).
        { "WinUIGallery/Samples/PullToRefresh/PullToRefreshPage.xaml", "Example1,rc,lv", "lv" },
        // Fixed by renaming an empty Frame (no Source) to a Border: bare Frames made the whole
        // offscreen render return 0x0 even though the page lays out.
        { "WinUIGallery/Samples/ConnectedAnimation/ConnectedAnimationPage.xaml", "CollectionContentFrame,CardFrame", "CollectionContentFrame" },
    };

    // Root element is the app's abstract ItemsPageBase; XamlReader constructs the root's own type
    // and ignores x:Class, so no runtime parser can create it. DesignHost reports this in the
    // user-visible StatusText ("Cannot preview this page: its root element 'ItemsPageBase' is an
    // abstract class..."), NOT in documentError (which is reserved for source-model errors).
    public static TheoryData<string, string, string> AbstractRootPages => new() {
        { "WinUIGallery/Samples/ListView/ListViewPage.xaml", "BaseExample", "abstract class" },
        { "WinUIGallery/Samples/GridView/GridViewPage.xaml", "BasicGridView", "abstract class" },
    };

    // A framework control whose default template natively crashed the design host while rendering
    // (AutoSuggestBox's Popup). Fixed host-side by a design-time template, so NavigationView and
    // AutoSuggestBox now live in RenderPages; no crash cases remain.

    [Theory]
    [MemberData(nameof(RenderPages))]
    public async Task GalleryPage_RendersWithRealContent(string relativePage, string expectedNames, string probeName)
    {
        if (_skip) { Assert.Skip(_skipReason); return; }

        var signature = SplitNames(expectedNames).FirstOrDefault() ?? string.Empty;
        var status = await OpenDesignerAsync(relativePage, signature, DesignerExpectation.Rendered);

        Assert.Equal("WinUI", status.GetProperty("framework").GetString());
        Assert.Equal("WinUI", status.GetProperty("backend").GetString());
        Assert.True(
            status.TryGetProperty("rendered", out var rendered) && rendered.GetBoolean(),
            relativePage + ": not rendered. status=" + status);

        // rendered:true is not enough - a page can report success at 0x0.
        var size = ParseRenderedSize(GetStatusText(status));
        Assert.True(
            size is { Width: > 0, Height: > 0 },
            relativePage + ": rendered but with a zero/invalid size. status=" + status);

        var names = ReadElementNames(status);
        foreach (var name in SplitNames(expectedNames))
            Assert.True(names.Contains(name), relativePage + ": element '" + name + "' missing from elementNames=[" + string.Join(",", names) + "]");

        if (!string.IsNullOrEmpty(probeName))
        {
            var bounds = await _app.InvokeAsync("od.winui-designer.query-element-screen-bounds", probeName);
            Assert.True(
                bounds.TryGetProperty("success", out var ok) && ok.GetBoolean(),
                relativePage + ": rendered element '" + probeName + "' not resolvable: " + bounds);
            Assert.True(bounds.GetProperty("width").GetDouble() > 0 && bounds.GetProperty("height").GetDouble() > 0,
                relativePage + ": rendered element '" + probeName + "' has zero bounds: " + bounds);
        }

        await AssertDesignHostChildAliveAsync(relativePage);
    }

    [Theory]
    [MemberData(nameof(AbstractRootPages))]
    public async Task GalleryPage_AbstractRootFailsWithAnExplanation(string relativePage, string signatureName, string expectedDiagnostic)
    {
        if (_skip) { Assert.Skip(_skipReason); return; }

        var status = await OpenDesignerAsync(relativePage, signatureName, DesignerExpectation.Diagnostic);

        Assert.False(IsRendered(status), relativePage + ": abstract-root page unexpectedly rendered. status=" + status);
        Assert.Contains(expectedDiagnostic, GetStatusText(status), StringComparison.OrdinalIgnoreCase);

        await AssertDesignHostChildAliveAsync(relativePage);
    }

    [Fact]
    public async Task Gallery_OpensAndIsRoutedToTheMicrosoftWinUIBackend()
    {
        if (_skip) { Assert.Skip(_skipReason); return; }

        await EnsureGalleryOpenAsync();

        // The same page the corpus theories start from, so a routing regression surfaces once here.
        var status = await OpenDesignerAsync("WinUIGallery/Samples/Button/ButtonPage.xaml", "Button1", DesignerExpectation.Rendered);
        Assert.Equal("WinUI", status.GetProperty("framework").GetString());
        Assert.Equal("WinUI", status.GetProperty("backend").GetString());

        // The owning project must be the Gallery app itself - the host's project-dependency context
        // (and therefore the app's compiled resources) is derived from it.
        var pagePath = GalleryPagePath("WinUIGallery/Samples/Button/ButtonPage.xaml");
        var dependency = await _app.InvokeAsync("od.project.dependency-context", pagePath);
        Assert.True(dependency.TryGetProperty("projectFound", out var found) && found.GetBoolean(), dependency.ToString());
        Assert.Equal("WinUIGallery", dependency.GetProperty("projectName").GetString());
        Assert.True(dependency.GetProperty("outputAssemblyExists").GetBoolean(), dependency.ToString());
    }

    async Task EnsureGalleryOpenAsync()
    {
        await _app.EnsureSolutionOpenAsync(Path.Combine(_galleryRoot!, SolutionFileName));

        // The Microsoft host reads the owning project's dependency context once, when the document
        // opens, so the active configuration/platform must be correct before the first page opens.
        var configured = await _app.InvokeAsync("od.solution.set-configuration", _configuration, _platform);
        Assert.True(configured.TryGetProperty("success", out var ok) && ok.GetBoolean(), configured.ToString());

        if (string.Equals(Environment.GetEnvironmentVariable("OD_WINUI_GALLERY_BUILD"), "1", StringComparison.Ordinal))
        {
            var build = await _app.InvokeAsync("od.build-solution");
            Assert.True(
                string.Equals(build.GetProperty("result").GetString(), "Success", StringComparison.Ordinal),
                await _app.DescribeBuildAsync(build));
        }
    }

    enum DesignerExpectation
    {
        // The page must render real content.
        Rendered,
        // The page must not render, and the host must report why (in StatusText for a load failure
        // such as an abstract root, or via a crash message / render diagnostics).
        Diagnostic,
    }

    async Task<JsonElement> OpenDesignerAsync(string relativePage, string signatureName, DesignerExpectation expectation)
    {
        await EnsureGalleryOpenAsync();
        await ResetDesignHostAsync();

        var pagePath = GalleryPagePath(relativePage);
        Assert.True(File.Exists(pagePath), "Gallery page not found: " + pagePath);

        var opened = await _app.InvokeAsync("od.open-file", pagePath);
        Assert.True(opened.TryGetProperty("opened", out var isOpen) && isOpen.GetBoolean(), opened.ToString());

        JsonElement status = default;
        var ready = await OpenDevelopAppFixture.PollUntilAsync(async () => {
            status = await _app.InvokeAsync("od.winui-designer.status");
            if (!status.TryGetProperty("active", out var active) || !active.GetBoolean())
                return false;
            // Wait until THIS document is the active designer - a previously-open page would
            // otherwise satisfy the rendered/diagnostic condition first.
            if (!string.IsNullOrEmpty(signatureName) && !ReadElementNames(status).Contains(signatureName))
                return false;
            if (expectation == DesignerExpectation.Rendered)
                return IsRendered(status);
            // A load failure sets rendered=false and puts the explanation in StatusText
            // (documentError is reserved for source-model errors). Wait until a terminal failure
            // message appears - not a transient "Starting…"/"Rendering…" text.
            if (IsRendered(status))
                return false;
            return IsFailureStatus(GetStatusText(status));
        }, TimeSpan.FromSeconds(150), initialDelayMs: 250, maxDelayMs: 1000);

        Assert.True(ready, relativePage + ": designer never reached the expected state. last status=" + status);
        return status;
    }

    // Each corpus page is opened in a FRESH design host. The out-of-process child does not survive
    // being handed one Gallery document after another (the second and later opens crash it natively
    // mid-render); the standalone GalleryProbe sidesteps this the same way, by starting a new client
    // per file. Closing every document releases the child (see
    // WinUIOnly_ClosingDocument_ReleasesRuntimeHostAndPreviewAssembly), so the next open gets a clean
    // host and repeats the ~30s theme build rather than crashing.
    async Task ResetDesignHostAsync()
    {
        var status = await _app.InvokeAsync("od.winui-designer.status");
        if (!(status.TryGetProperty("active", out var active) && active.GetBoolean()))
            return;

        await _app.InvokeAsync("od.close-all-document-views");

        var closed = await OpenDevelopAppFixture.PollUntilAsync(async () => {
            var current = await _app.InvokeAsync("od.winui-designer.status");
            return !(current.TryGetProperty("active", out var isActive) && isActive.GetBoolean());
        }, TimeSpan.FromSeconds(30), initialDelayMs: 200, maxDelayMs: 1000);
        Assert.True(closed, "WinUI designer did not close before opening the next Gallery page.");

        var released = await OpenDevelopAppFixture.PollUntilAsync(async () => {
            var stats = await _app.InvokeAsync("od.winui-designer.runtime-stats");
            return !(stats.TryGetProperty("childAlive", out var alive) && alive.GetBoolean());
        }, TimeSpan.FromSeconds(30), initialDelayMs: 200, maxDelayMs: 1000);
        Assert.True(released, "Design host child was not released after closing all documents.");
    }

    string GalleryPagePath(string relativePage)
        => Path.Combine(_galleryRoot!, relativePage.Replace('/', Path.DirectorySeparatorChar));

    async Task AssertDesignHostChildAliveAsync(string relativePage)
    {
        var stats = await _app.InvokeAsync("od.winui-designer.runtime-stats");
        Assert.True(
            stats.TryGetProperty("childAlive", out var alive) && alive.GetBoolean(),
            relativePage + ": design host child is not alive: " + stats);
    }

    static bool IsRendered(JsonElement status)
        => status.TryGetProperty("rendered", out var rendered) && rendered.GetBoolean();

    // A terminal design-host failure message: an uninstantiable root, a crash during rendering, or
    // the host's bounded give-up after repeated crashes.
    static bool IsFailureStatus(string statusText)
        => statusText.Contains("Cannot preview", StringComparison.OrdinalIgnoreCase)
            || statusText.Contains("exited while rendering", StringComparison.OrdinalIgnoreCase)
            || statusText.Contains("crashed", StringComparison.OrdinalIgnoreCase);

    static string GetStatusText(JsonElement status)
        => status.TryGetProperty("status", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? ""
            : "";

    static HashSet<string> ReadElementNames(JsonElement status)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (status.TryGetProperty("elementNames", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (item.GetString() is { Length: > 0 } name)
                    names.Add(name);
        return names;
    }

    static (int Width, int Height)? ParseRenderedSize(string? statusText)
    {
        if (string.IsNullOrEmpty(statusText))
            return null;
        // "Rendered by WinUI design host (800×600 @ 1x)." - the multiplication sign is a real character
        // in the payload; accept x/X too so the same parser works against either host.
        var match = Regex.Match(statusText, @"(\d+)\s*[×xX]\s*(\d+)");
        if (!match.Success)
            return null;
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    static IEnumerable<string> SplitNames(string names)
        => names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
