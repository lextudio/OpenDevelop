using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of the hosted-ILSpy-panes work (doc/technotes/ilspy.md): opening an
// assembly via the real ILSpy AssemblyTreeModel should register and show the four ILSpy pads
// (Assemblies/Search/Analyzer/Decompiled Code) as real OpenDevelop pads (DockWorkspace.ToolPanes),
// with the assembly tree and decompiled-code view showing real content - not the legacy
// launch-ILSpy.exe/DisplayBinding integration. Drives the app via the od.ilspy.* DevFlow actions
// (IlSpyDevFlowActions.cs) since there's no native file-dialog automation for the WPF-embedded
// DevFlow agent (od.ilspy.open-assembly bypasses the OpenFileDialog the real menu command shows).
//
// The test isolates ILSpy's own persistent assembly list (~/Library/Application Support/
// ICSharpCode/ILSpy.xml on macOS): the hosted ILSpy restores the user's previously-opened
// assemblies on startup, and a restored entry whose path no longer exists makes the decompile
// view render "The directory was not found" instead of the opened assembly - so the fixture
// backs the file up, removes it for the duration of the test, and restores it afterwards (same
// determinism argument as the fixture's DeleteStaleViewStateMemento).
//
// Prerequisites:
//   1. Build OpenDevelop in Debug:
//        dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
//   2. Build the fixture assembly this test opens in ILSpy:
//        dotnet build tests/fixtures/DebugTestApp/DebugTestApp.csproj -c Debug
[Collection("OpenDevelop app")]
public sealed class IlSpyAddInTests : IDisposable
{
    static readonly string IlSpySettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ICSharpCode", "ILSpy.xml");
    readonly string _ilSpySettingsBackup;

    readonly OpenDevelopAppFixture _app;

    public IlSpyAddInTests(OpenDevelopAppFixture app)
    {
        _app = app;
        if (File.Exists(IlSpySettingsPath))
        {
            _ilSpySettingsBackup = Path.Combine(Path.GetTempPath(), "ILSpy.xml." + Guid.NewGuid().ToString("N"));
            File.Copy(IlSpySettingsPath, _ilSpySettingsBackup);
            File.Delete(IlSpySettingsPath);
        }
    }

    public void Dispose()
    {
        if (_ilSpySettingsBackup != null)
        {
            try { File.Copy(_ilSpySettingsBackup, IlSpySettingsPath, overwrite: true); } catch { }
            try { File.Delete(_ilSpySettingsBackup); } catch { }
        }
    }

    [Fact]
    public async Task OpenAssembly_ShowsIlSpyPadsWithRealContent()
    {
        var assemblyPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(_app.DebugTestProjectPath)!, "bin", "Debug", "net10.0", "DebugTestApp.dll");
        Assert.True(System.IO.File.Exists(assemblyPath),
            $"Expected the DebugTestApp fixture to already be built at {assemblyPath} (see prerequisites)");

        var openResult = await _app.InvokeAsync("od.ilspy.open-assembly", assemblyPath);
        Assert.True(openResult.GetProperty("opened").GetBoolean(), $"Failed to open {assemblyPath} in ILSpy");

        var status = await _app.InvokeAsync("od.ilspy.status");

        // OpenDevelop "enters the ILSpy layout": the three real ILSpy pads are registered as
        // OpenDevelop pads (DockWorkspace.ToolPanes, via DockWorkspaceExtensibility.AddToolPane)
        // and visible, instead of the legacy launch-ILSpy.exe integration. Decompiled output is
        // NOT one of those pads - it opens as a document tab (see the active-view assertion
        // below).
        var panes = status.GetProperty("panes").EnumerateArray()
            .Select(p => (Title: p.GetProperty("title").GetString(), IsVisible: p.GetProperty("isVisible").GetBoolean()))
            .ToList();

        // "Search"/"Analyze" are ILSpy's own real pane titles (SearchPaneModel/
        // AnalyzerTreeViewModel set them in their constructors); "Assemblies" is one this addin
        // assigns itself (see IlSpyWorkspaceHost).
        foreach (var expectedTitle in new[] { "Assemblies", "Search", "Analyze"})
        {
            var pane = panes.SingleOrDefault(p => p.Title == expectedTitle);
            Assert.True(pane != default, $"Expected an ILSpy pad titled '{expectedTitle}' to be registered; got: {string.Join(", ", panes.Select(p => p.Title))}");
            Assert.True(pane.IsVisible, $"Expected the '{expectedTitle}' pad to be visible after opening an assembly");
        }
        Assert.DoesNotContain(panes, p => p.Title == "Decompiled Code");

        // Decompiled output opens as a document tab (a read-only, virtual file) - ShowView
        // activates it, so the active view should be the DecompiledCodeViewContent document.
        var activeView = await _app.InvokeAsync("od.active-view");
        Assert.True(activeView.GetProperty("active").GetBoolean());
        Assert.Equal("ICSharpCode.ILSpyAddIn.DecompiledCodeViewContent", activeView.GetProperty("typeName").GetString());

        // Assembly tree pad: the opened assembly shows up in the real ILSpy AssemblyList.
        var loadedAssemblies = status.GetProperty("loadedAssemblies").EnumerateArray()
            .Select(a => a.GetString())
            .ToList();
        Assert.Contains("DebugTestApp", loadedAssemblies);

        // Runtime-added tool panes don't reliably dock at all - od.ilspy.show-pane re-registers
        // the pane so its tab deterministically appears in the layout.
        var showPaneResult = await _app.InvokeAsync("od.ilspy.show-pane", "Assemblies");
        Assert.True(showPaneResult.GetProperty("found").GetBoolean(), "Could not find the Assemblies ILSpy pane");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement uiTree = default;
        List<string> texts = new();
        List<JsonElement> allElements = new();
        while (DateTime.UtcNow < deadline)
        {
            uiTree = await _app.GetUITreeAsync();
            allElements = FlattenElements(uiTree).ToList();
            texts = allElements
                .Where(e => e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock"
                    && e.TryGetProperty("text", out var txt) && !string.IsNullOrEmpty(txt.GetString()))
                .Select(e => e.GetProperty("text").GetString())
                .ToList();
            if (texts.Contains("Assemblies") && texts.Contains("Decompiled Code"))
                break;
            await Task.Delay(500);
        }
        Assert.Contains(texts, t => t == "Assemblies");
        Assert.Contains(texts, t => t == "Decompiled Code");

        // Regression coverage for doc/technotes/ilspy.md's "empty pane content area" failure mode
        // (fixed 2026-08-02 - a Base.Light/Dark.xaml merge into a since-modernized, no-longer-
        // shipped AvalonDock theme resource threw at startup, which previously left the assembly
        // tree pane's content unrendered): the "Assemblies" pane's content is a real, walkable
        // SharpTreeView tree, not an empty container. Assert on actual node instances with
        // non-zero size, not just the tab header text asserted above.
        var treeNodes = allElements
            .Where(e => e.TryGetProperty("fullType", out var ft)
                && ft.GetString() == "ICSharpCode.ILSpy.Controls.TreeView.SharpTreeNodeView"
                && e.TryGetProperty("bounds", out var b)
                && b.TryGetProperty("width", out var w) && w.GetDouble() > 0
                && b.TryGetProperty("height", out var h) && h.GetDouble() > 0)
            .ToList();
        Assert.True(treeNodes.Count > 0,
            "Expected the Assemblies pane's SharpTreeView to render at least one real, non-zero-size tree node - got an empty pane (the historical failure mode)");

        // Decompiled Code pad: opening the assembly auto-selects and decompiles its tree node,
        // so the real DecompilerTextView should show non-empty, real decompiled output (not a
        // blank/placeholder pane).
        Assert.True(status.GetProperty("decompiledTextLength").GetInt32() > 0,
            "Expected the Decompiled Code pad to show non-empty decompiled output after opening an assembly");

        // The decompiled text must be the fixture's real IL - not a placeholder/error pane.
        // Private members (ComputeGreeting) aren't decompiled by default, so assert on the
        // type name being present and the ILSpy file-not-found placeholder being absent.
        var snippet = status.GetProperty("decompiledTextSnippet").GetString()!;
        Assert.Contains("Program", snippet);
        Assert.DoesNotContain("The directory was not found", snippet);
    }

    [Fact]
    public async Task SwitchToIlSpyLayout_ActivatesPanesWithoutPriorIlSpyInteraction()
    {
        // Regression coverage for the gap the user flagged 2026-08-02: registering "ILSpy" as an
        // AddIn-contributed named layout (ILayoutTemplateProvider) is not enough by itself -
        // selecting the layout must also activate IlSpyWorkspaceHost, or ILSpy.xml's pane
        // anchorables silently restore nothing (DockWorkspace's LayoutSerializationCallback skips
        // any ContentId that isn't registered yet). od.ilspy.is-initialized reads
        // IlSpyWorkspaceHost.IsInitialized without triggering it (unlike od.ilspy.status/
        // show-pane/open-assembly, which all initialize as a side effect), so it can distinguish
        // "the layout switch itself did this" from "some other ILSpy action already had."
        //
        // Ordering caveat: OpenDevelopAppFixture's app instance is shared across every test in the
        // "OpenDevelop app" collection, so if another test has already opened an assembly or
        // touched an od.ilspy.* action first, IsInitialized may already be true before this test's
        // switch-layout call - that's still a valid state (the addin was activated one way or
        // another), just not proof this specific call did it. The was-it-already-initialized
        // check below is therefore informational, not a hard assertion; what this test actually
        // guards is the behavior that matters end-to-end: after switching to "ILSpy", the addin's
        // panes are unconditionally initialized and visible, however that came about.
        var before = await _app.InvokeAsync("od.ilspy.is-initialized");
        var wasAlreadyInitialized = before.GetProperty("initialized").GetBoolean();

        var switchResult = await _app.InvokeAsync("od.workbench.switch-layout", "ILSpy");
        Assert.True(switchResult.GetProperty("found").GetBoolean(), "Expected the AddIn-contributed \"ILSpy\" layout to be registered");
        Assert.Equal("ILSpy", switchResult.GetProperty("layoutName").GetString());

        var after = await _app.InvokeAsync("od.ilspy.is-initialized");
        Assert.True(after.GetProperty("initialized").GetBoolean(),
            $"Expected switching to the \"ILSpy\" layout to activate IlSpyWorkspaceHost (ILayoutTemplateProvider.OnActivating). " +
            $"wasAlreadyInitialized={wasAlreadyInitialized} (informational - see ordering caveat above)");

        var status = await _app.InvokeAsync("od.ilspy.status");
        var paneTitles = status.GetProperty("panes").EnumerateArray()
            .Select(p => p.GetProperty("title").GetString())
            .ToList();
        foreach (var expectedTitle in new[] { "Assemblies", "Search", "Analyze" })
        {
            Assert.Contains(expectedTitle, paneTitles);
        }
    }

    static IEnumerable<JsonElement> FlattenElements(JsonElement tree)
    {
        foreach (var root in tree.GetProperty("elements").EnumerateArray())
            foreach (var node in Flatten(root))
                yield return node;
    }

    static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                foreach (var descendant in Flatten(child))
                    yield return descendant;
    }
}
