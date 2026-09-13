using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Drives the WPF design surface (<c>od.wpf-designer.*</c>) against the real WPFGallery
/// application from the official microsoft/WPF-Samples repo - the WPF counterpart of
/// <see cref="WinUIGalleryDesignerTests"/>, which does the same thing for the WinUI-Gallery corpus
/// against the Microsoft WinUI design host.
///
/// The Gallery's control pages (<c>Views/BasicInput/ButtonPage.xaml</c> etc.) are plain <c>Page</c>
/// documents, each wrapping its sample controls in the app's own <c>controls:ControlExample</c> -
/// a corpus far larger and more hostile than the tiny <c>WpfSample</c>/<c>MicrosoftWpfSample</c>
/// fixtures the rest of this suite mostly exercises, so it is a useful regression net for the WPF
/// designer's document loading, outline building, and selection against unfamiliar real-world XAML.
///
/// Backend: <c>WpfViewContent.LoadInternal</c> picks the design-host backend per document via
/// <c>XamlFrameworkDetector.Detect(...)</c>, NOT via the <c>OD_WPF_RUNTIME</c> env var (that only
/// changes the *default* used when detection is inconclusive) - it looks at what the owning
/// project actually references. WPFGallery is a plain SDK-style <c>net10.0-windows</c>/
/// <c>UseWPF=true</c> project with no LibreWPF (librewpf.transport) reference, so every page in it
/// is correctly routed to the real "Microsoft WPF" design host, not "LibreWPF" - measured directly
/// running this suite; do not assume "LibreWPF" is the WPF designer's universal default the way it
/// is for the other WPF fixtures (<c>WpfSample</c>), which are deliberately built against the
/// LibreWPF transport package instead.
///
/// Optional and gated: skips unless a WPF-Samples checkout is found (<c>OD_WPF_GALLERY_ROOT</c>
/// pointing directly at the "Sample Applications/WPFGallery" folder, or a sibling "WPF-Samples"
/// directory at any ancestor of the test binary - see
/// <see cref="OpenDevelopAppFixture.WpfGalleryRoot"/>), or unless the Microsoft WPF design host is
/// unavailable (Windows-only, same gate as <c>AddInTests.MicrosoftDesignerBackendsAvailable</c>).
///
/// The Gallery must be built once (<c>OD_WPF_GALLERY_BUILD=1</c> builds it through DevFlow before
/// the first page opens, same opt-in as WinUIGalleryDesignerTests's own build gate) - measured
/// directly: against an UNBUILT checkout, the design host cannot resolve the app's own
/// <c>controls:ControlExample</c> custom control, and every page's outline silently stops at
/// <c>Page &gt; ContentPagePane &gt; ScrollViewer &gt; StackPanel</c> with NONE of the actual sample
/// controls inside it - <c>designerLoaded</c> still reports <c>true</c> and the root name is still
/// present, so a check that only looks at those (as an earlier version of this suite did) passes
/// while missing the entire point of the corpus. Each row below therefore also names one control
/// TYPE that only appears in the outline once the real page content resolves, and the assertion
/// requires it.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
[Trait("DesignerBackend", "Microsoft")]
public sealed class WpfGalleryDesignerTests
{
    const string ExpectedBackend = "WPF";

    readonly OpenDevelopAppFixture _app;
    readonly string? _galleryRoot;
    readonly bool _skip;
    readonly string _skipReason = "";

    public WpfGalleryDesignerTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _galleryRoot = app.WpfGalleryRoot;

        if (!OperatingSystem.IsWindows())
        {
            _skip = true;
            _skipReason = "The Microsoft WPF design host is Windows-only.";
        }
        else if (_galleryRoot is null)
        {
            _skip = true;
            _skipReason = "No WPF-Samples checkout found; set OD_WPF_GALLERY_ROOT to the "
                + "\"Sample Applications/WPFGallery\" folder of a microsoft/WPF-Samples checkout.";
        }
    }

    // Each row: page (relative to the Gallery's WPFGallery project folder), the x:Name of its
    // outer content root (every page in this corpus carries the same one), and one control TYPE
    // name that only shows up in the outline once the page's real sample content has resolved -
    // see the class doc's "unbuilt checkout" pitfall for why the root name alone isn't enough.
    public static TheoryData<string, string, string> RenderPages => new() {
        { "Views/BasicInput/ButtonPage.xaml", "ContentPagePane", "Button" },
        { "Views/BasicInput/CheckBoxPage.xaml", "ContentPagePane", "CheckBox" },
        { "Views/BasicInput/ComboBoxPage.xaml", "ContentPagePane", "ComboBoxItem" },
        { "Views/BasicInput/RadioButtonPage.xaml", "ContentPagePane", "RadioButton" },
        { "Views/BasicInput/SliderPage.xaml", "ContentPagePane", "Slider" },
        { "Views/Layout/BorderPage.xaml", "ContentPagePane", "Border" },
        { "Views/Layout/ExpanderPage.xaml", "ContentPagePane", "Expander" },
        { "Views/Layout/GridSplitterPage.xaml", "ContentPagePane", "GridSplitter" },
        { "Views/Layout/GroupBoxPage.xaml", "ContentPagePane", "GroupBox" },
        { "Views/Collections/ListBoxPage.xaml", "ContentPagePane", "ListBoxItem" },
        // GridView is a non-visual ListView.View property value, not an outline node - Label is.
        { "Views/Collections/ListViewPage.xaml", "ContentPagePane", "Label" },
        { "Views/Collections/TreeViewPage.xaml", "ContentPagePane", "TreeViewItem" },
        // The page's DataGrid carries x:Name="SampleDataGrid", so the outline reports that name
        // instead of the type "DataGrid" (outline prefers a real x:Name when one is set).
        { "Views/Collections/DataGridPage.xaml", "ContentPagePane", "SampleDataGrid" },
        { "Views/DateAndTime/CalendarPage.xaml", "ContentPagePane", "Calendar" },
        { "Views/DateAndTime/DatePickerPage.xaml", "ContentPagePane", "DatePicker" },
        { "Views/Media/CanvasPage.xaml", "ContentPagePane", "Canvas" },
        { "Views/Media/ImagePage.xaml", "ContentPagePane", "Image" },
        { "Views/Navigation/TabControlPage.xaml", "ContentPagePane", "TabItem" },
        // MenuPage.xaml is deliberately NOT here - see MenuPage_OutlineStopsAtTheFirstControlExample
        // below for why.
    };

    [Theory]
    [MemberData(nameof(RenderPages))]
    public async Task GalleryPage_LoadsWithOutlineAndRealContent(string relativePage, string rootName, string leafTypeName)
    {
        if (_skip) { Assert.Skip(_skipReason); return; }

        // Wait for the leaf too, not just the root - some pages (e.g. Menu's nested MenuItems)
        // resolve their deeper content a little later than their simpler siblings, and waiting on
        // outline-count stabilization alone can declare "ready" one poll too early.
        var status = await OpenDesignerAsync(relativePage, rootName, leafTypeName);

        Assert.Equal(ExpectedBackend, status.GetProperty("backend").GetString());
        Assert.Equal("Page", status.GetProperty("rootItemType").GetString());
        Assert.True(
            status.TryGetProperty("designerLoaded", out var loaded) && loaded.GetBoolean(),
            relativePage + ": designer did not load. status=" + status);

        var names = ReadOutlineNames(status);
        Assert.True(names.Contains(rootName),
            relativePage + ": '" + rootName + "' missing from outlineNames=[" + string.Join(",", names) + "]");
        // The real proof of content, not just document/root loading - see the class doc comment.
        // A missing leaf almost always means the WPFGallery project itself was never built (its
        // controls:ControlExample custom control can't be resolved without a compiled assembly),
        // not a WPF-designer regression - build it first (dotnet build the WPFGallery.csproj, or
        // set OD_WPF_GALLERY_BUILD=1) and re-run.
        Assert.True(names.Contains(leafTypeName),
            relativePage + ": '" + leafTypeName + "' missing from outlineNames=[" + string.Join(",", names)
                + "] - is the WPFGallery project built? (custom controls fail to resolve otherwise)");

        var selected = await _app.InvokeAsync("od.wpf-designer.select", rootName);
        Assert.True(
            selected.TryGetProperty("success", out var ok) && ok.GetBoolean(),
            relativePage + ": could not select '" + rootName + "': " + selected);
        Assert.Equal(rootName, selected.GetProperty("selectedName").GetString());
    }

    /// <summary>
    /// MenuPage.xaml's outline reliably stops at <c>Page &gt; ContentPagePane &gt; PageHeader &gt;
    /// ScrollViewer &gt; Grid &gt; ControlExample</c> and never descends into the
    /// <c>ControlExample</c>'s own <c>Menu</c>/<c>MenuItem</c> content - confirmed stable (not a slow
    /// resolve - the exact same outline held steady for 30s of polling) and not accompanied by any
    /// document-level error beyond the usual GPU-unavailable rendering diagnostic (see
    /// wpf-designer.md's "Bounded portable frame rendering"). The page's first
    /// <c>controls:ControlExample</c> wraps a <c>&lt;Style TargetType="MenuItem"&gt;</c> with an
    /// <c>EventSetter Event="Click" Handler="MenuItem_Click"</c> - the likely culprit, since no other
    /// corpus page combines a Style resource with an EventSetter referencing a code-behind handler.
    /// Recorded here as a known WPF-designer limitation on this specific page shape rather than
    /// asserted against (unlike <c>RenderPages</c>, a fixed expectation here would either mask a
    /// regression that makes it worse or start failing the moment someone fixes it) - this fact
    /// exists so a future fix shows up as this test starting to need updating, not as a silent
    /// change nobody notices.
    /// </summary>
    [Fact]
    public async Task MenuPage_OutlineStopsAtTheFirstControlExample()
    {
        if (_skip) { Assert.Skip(_skipReason); return; }

        await EnsureGalleryOpenAsync();
        var pagePath = GalleryPagePath("Views/Navigation/MenuPage.xaml");
        var opened = await _app.InvokeAsync("od.open-file", pagePath);
        Assert.True(opened.TryGetProperty("opened", out var isOpen) && isOpen.GetBoolean(), opened.ToString());

        JsonElement status = default;
        var loaded = await OpenDevelopAppFixture.PollUntilAsync(async () => {
            status = await _app.InvokeAsync("od.wpf-designer.status");
            return status.TryGetProperty("active", out var active) && active.GetBoolean()
                && status.TryGetProperty("designerLoaded", out var dl) && dl.GetBoolean()
                && ReadOutlineNames(status).Contains("ContentPagePane");
        }, TimeSpan.FromSeconds(30), initialDelayMs: 100, maxDelayMs: 500);
        Assert.True(loaded, "MenuPage.xaml never loaded. last status=" + status);

        var names = ReadOutlineNames(status);
        Assert.True(names.Contains("ControlExample"), "outlineNames=[" + string.Join(",", names) + "]");
        Assert.False(names.Contains("MenuItem"),
            "MenuPage.xaml's outline now contains 'MenuItem' - the known limitation this fact "
                + "documents may be fixed; move MenuPage.xaml back into RenderPages with leaf "
                + "\"MenuItem\" instead of leaving this assertion stale.");
    }

    [Fact]
    public async Task Gallery_OpensAndIsRoutedToTheMicrosoftWpfBackend()
    {
        if (_skip) { Assert.Skip(_skipReason); return; }

        // The same page the corpus theories start from, so a routing regression surfaces once here.
        var status = await OpenDesignerAsync("Views/BasicInput/ButtonPage.xaml", "ContentPagePane", "Button");
        Assert.Equal(ExpectedBackend, status.GetProperty("backend").GetString());
        Assert.Equal("Page", status.GetProperty("rootItemType").GetString());

        // The owning project must be the Gallery app itself - the designer's project-dependency
        // context (and therefore its compiled assembly/resources) is derived from it.
        var pagePath = GalleryPagePath("Views/BasicInput/ButtonPage.xaml");
        var dependency = await _app.InvokeAsync("od.project.dependency-context", pagePath);
        Assert.True(dependency.TryGetProperty("projectFound", out var found) && found.GetBoolean(), dependency.ToString());
        Assert.Equal("WPFGallery", dependency.GetProperty("projectName").GetString());
    }

    static bool _builtOnce;

    async Task EnsureGalleryOpenAsync()
    {
        await _app.EnsureSolutionOpenAsync(Path.Combine(_galleryRoot!, "WPFGallery.sln"));

        if (!_builtOnce
            && string.Equals(Environment.GetEnvironmentVariable("OD_WPF_GALLERY_BUILD"), "1", StringComparison.Ordinal))
        {
            _builtOnce = true;
            var build = await _app.InvokeAsync("od.build-solution");
            Assert.True(
                string.Equals(build.GetProperty("result").GetString(), "Success", StringComparison.Ordinal),
                await _app.DescribeBuildAsync(build));
        }
    }

    async Task<JsonElement> OpenDesignerAsync(string relativePage, string rootName, string? leafTypeName = null)
    {
        await EnsureGalleryOpenAsync();

        var pagePath = GalleryPagePath(relativePage);
        Assert.True(File.Exists(pagePath), "Gallery page not found: " + pagePath);

        var opened = await _app.InvokeAsync("od.open-file", pagePath);
        Assert.True(opened.TryGetProperty("opened", out var isOpen) && isOpen.GetBoolean(), opened.ToString());

        JsonElement status = default;
        var previousCount = -1;
        var ready = await OpenDevelopAppFixture.PollUntilAsync(async () => {
            status = await _app.InvokeAsync("od.wpf-designer.status");
            if (!(status.TryGetProperty("active", out var active) && active.GetBoolean())
                || !(status.TryGetProperty("designerLoaded", out var loaded) && loaded.GetBoolean()))
            {
                previousCount = -1;
                // A previously-open document can still be active for a moment after od.open-file
                // returns - see the identical pattern/rationale in AddInTests's
                // WaitForWpfDesignerStatusAsync. Re-invoking od.open-file wins over that restore.
                await _app.InvokeAsync("od.open-file", pagePath);
                return false;
            }
            var names = ReadOutlineNames(status);
            if (!names.Contains(rootName) || (leafTypeName != null && !names.Contains(leafTypeName)))
            {
                previousCount = -1;
                return false;
            }
            // Wait for the outline to stop growing across two consecutive polls, same rationale as
            // WaitForWpfDesignerStatusAsync: nested custom-control instances can populate a little
            // later than their simpler siblings.
            var count = status.GetProperty("outlineNames").GetArrayLength();
            if (count == previousCount)
                return true;
            previousCount = count;
            return false;
        }, TimeSpan.FromSeconds(60), initialDelayMs: 100, maxDelayMs: 500);

        Assert.True(ready, relativePage + ": WPF designer never finished loading. last status=" + status);
        return status;
    }

    string GalleryPagePath(string relativePage)
        => Path.Combine(_galleryRoot!, relativePage.Replace('/', Path.DirectorySeparatorChar));

    static HashSet<string> ReadOutlineNames(JsonElement status)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (status.TryGetProperty("outlineNames", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var item in array.EnumerateArray())
                if (item.GetString() is { Length: > 0 } name)
                    names.Add(name);
        return names;
    }
}
