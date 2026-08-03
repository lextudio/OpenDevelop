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

        // ILSpy's AssemblyTreeModel.ShowAssemblyList would rename Application.Current.MainWindow
        // to "ILSpy {version}"; the OpenDevelop-hosted build skips that (OPOPENDEVELOP conditional
        // in the linked ILSpy source) so the IDE keeps its own title.
        var windowTitle = await _app.InvokeAsync("od.window.title");
        Assert.True(windowTitle.GetProperty("hasWindow").GetBoolean());
        Assert.DoesNotContain("ILSpy", windowTitle.GetProperty("title").GetString());

        // Assembly tree pad: the opened assembly shows up in the real ILSpy AssemblyList.
        var loadedAssemblies = status.GetProperty("loadedAssemblies").EnumerateArray()
            .Select(a => a.GetString())
            .ToList();
        Assert.Contains("DebugTestApp", loadedAssemblies);

        // Opening the assembly auto-selects its tree node (AssemblyTreeModel.SelectNode during
        // OpenFiles) - the model-side selected state must report it.
        var selectedNodes = status.GetProperty("selectedNodes").EnumerateArray()
            .Select(a => a.GetString())
            .ToList();
        Assert.True(selectedNodes.Contains("DebugTestApp"),
            $"Expected the opened assembly's tree node to be selected; got: {string.Join(", ", selectedNodes)}");

        // Jump to the node explicitly (the same real SelectNode path) and confirm the selection
        // sticks and the node's rendered text is reported.
        var selectResult = await _app.InvokeAsync("od.ilspy.select-node", "DebugTestApp");
        Assert.True(selectResult.GetProperty("success").GetBoolean(),
            selectResult.TryGetProperty("error", out var error) ? error.GetString() : null);
        Assert.True(selectResult.GetProperty("selected").GetBoolean(),
            "Expected the DebugTestApp tree node to be selected after od.ilspy.select-node");
        Assert.Contains("DebugTestApp", selectResult.GetProperty("selectedNodes").EnumerateArray()
            .Select(a => a.GetString()).ToList());

        // Activate the Assemblies pad. Deliberately od.ilspy.activate-pane and NOT
        // od.ilspy.show-pane: the latter removes and re-adds the anchorable, which was needed back
        // when runtime-added panes didn't reliably dock, but is destructive now that the ILSpy
        // layout template actually restores (see doc/technotes/ilspy.md's layout-schema work) -
        // measured: after one show-pane, activating a *different* pane fails to materialize it at
        // all, and repeated churn eventually leaves none of them rendered.
        var showPaneResult = await _app.InvokeAsync("od.ilspy.activate-pane", "Assemblies");
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

        // The selected assembly's tree node is rendered as a real TextBlock (the theme fix made
        // the pane content renderable, and the node was selected above): the node text is
        // LoadedAssembly.Text, "DebugTestApp (1.0.0.0, .NETCoreApp, v10.0)".
        Assert.True(texts.Any(t => t.StartsWith("DebugTestApp", StringComparison.Ordinal)),
            $"Expected the selected assembly node to be rendered in the Assemblies tree; got: {string.Join(" | ", texts.Take(20))}");

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

        // --- Dedicated-pad visible-content coverage (added 2026-08-03) -------------------------
        // An audit found that of the three ILSpy tool pads, only "Assemblies" had a real *visible
        // content* assertion (the SharpTreeNodeView check above). "Search" and "Analyze" were
        // covered by nothing but title + IsVisible - which is exactly what the historical "empty
        // pane content area" failure mode (doc/technotes/ilspy.md) passes: correct tab header,
        // blank content. The SearchBox specifically had its own such regression ("rendered as a
        // blank gap", fixed via generic.xaml) with nothing guarding it.
        //
        // These live INSIDE this one test rather than as separate [Fact]s: the app fixture is shared
        // across the whole collection, so separate test methods that each activate panes interfere
        // in an order-dependent way (measured - as three [Fact]s this suite failed 3/5 with "pane
        // not materialized" and a lost active document). Sequenced here it is deterministic.
        //
        // No pane switching is needed for these assertions: the restored ILSpy layout docks all
        // three anchorables, so all three pads' content is laid out simultaneously.

        // Search pad: real content, including the SearchBox that once rendered as a blank gap.
        var activateSearch = await _app.InvokeAsync("od.ilspy.activate-pane", "Search");
        Assert.True(activateSearch.GetProperty("found").GetBoolean(), "Could not find the Search ILSpy pane");
        await AssertRenderedWithNonZeroSizeAsync(
            "ICSharpCode.ILSpy.Search.SearchPane",
            "ICSharpCode.ILSpy.Controls.SearchBox");

        // Search -> results -> activate a result -> the Assemblies tree jumps to that member.
        // "ComputeGreeting" is unique to the DebugTestApp fixture (its only private helper), so this
        // stays deterministic even though ILSpy also searches the auto-loaded framework assemblies.
        var search = await _app.InvokeAsync("od.ilspy.search", "ComputeGreeting");
        Assert.True(search.GetProperty("success").GetBoolean(), ErrorOf(search));
        var searchResults = search.GetProperty("results").EnumerateArray()
            .Select(r => (
                Name: r.GetProperty("name").GetString(),
                Location: r.GetProperty("location").GetString(),
                HasReference: r.GetProperty("hasReference").GetBoolean()))
            .ToList();
        Assert.True(searchResults.Count > 0, "Expected the ILSpy search to find 'ComputeGreeting' in the DebugTestApp fixture");
        int hitIndex = searchResults.FindIndex(r => r.Location == "DebugTestApp.Program");
        Assert.True(hitIndex >= 0,
            $"Expected a search hit inside DebugTestApp.Program; got: {string.Join(" | ", searchResults.Select(r => r.Location + " :: " + r.Name))}");
        Assert.True(searchResults[hitIndex].HasReference,
            "A search result must carry a navigable Reference, otherwise activating it cannot navigate anywhere");

        // Double-clicking a result does exactly one thing (SearchPane.JumpToSelectedItem):
        // MessageBus.Send(new NavigateToReferenceEventArgs(result.Reference)) - which
        // AssemblyTreeModel subscribes to and turns into JumpToReferenceAsync -> SelectNode. So
        // activating it must move the Assemblies tree selection off the assembly node onto the member.
        var activate = await _app.InvokeAsync("od.ilspy.search-activate", hitIndex);
        Assert.True(activate.GetProperty("success").GetBoolean(), ErrorOf(activate));
        Assert.True(activate.GetProperty("selectionChanged").GetBoolean(),
            "Expected activating a search result to change the Assemblies tree selection (the jump)");
        var jumpedTo = activate.GetProperty("selectedNodeDetails").EnumerateArray()
            .Select(n => (Type: n.GetProperty("nodeType").GetString(), Text: n.GetProperty("text").GetString()))
            .ToList();
        Assert.Contains(jumpedTo, n => n.Type == "MethodTreeNode"
            && n.Text != null && n.Text.StartsWith("ComputeGreeting", StringComparison.Ordinal));

        // Analyze pad: real content. AnalyzerTreeView *is* a SharpTreeView (its XAML root element),
        // so this also covers the shared tree control rendering inside this pad.
        var activateAnalyze = await _app.InvokeAsync("od.ilspy.activate-pane", "Analyze");
        Assert.True(activateAnalyze.GetProperty("found").GetBoolean(), "Could not find the Analyze ILSpy pane");
        await AssertRenderedWithNonZeroSizeAsync("ICSharpCode.ILSpy.Analyzers.AnalyzerTreeView");
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

    /// <summary>
    /// Asserts each given CLR type renders as a real, laid-out element (non-zero width AND height)
    /// somewhere in the UI tree - i.e. the pane's content area is actually populated, not merely
    /// present-but-unrendered (bounds arrive as null for an element that exists in the tree but was
    /// never laid out, e.g. content of a non-selected tab).
    /// </summary>
    async Task AssertRenderedWithNonZeroSizeAsync(params string[] fullTypes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<string> missing;
        while (true)
        {
            var tree = await _app.GetUITreeAsync();
            var all = FlattenElements(tree).ToList();
            missing = fullTypes.Where(ft => !all.Any(e => IsRenderedInstanceOf(e, ft))).ToList();
            if (missing.Count == 0)
                return;
            if (DateTime.UtcNow >= deadline)
                break;
            await Task.Delay(500);
        }
        // Distinguish "not in the tree at all" from "in the tree but never laid out" - they have
        // completely different causes (pane/DataTemplate not materialized vs. collapsed/zero-size).
        var finalTree = await _app.GetUITreeAsync();
        var finalAll = FlattenElements(finalTree).ToList();
        var diagnosis = missing.Select(ft =>
        {
            var instances = finalAll.Where(e => e.TryGetProperty("fullType", out var t) && t.GetString() == ft).ToList();
            if (instances.Count == 0)
                return ft + " => absent from the UI tree entirely";
            var boundsDesc = instances.Select(e =>
                e.TryGetProperty("bounds", out var b) && b.ValueKind == JsonValueKind.Object
                    ? $"{b.GetProperty("width").GetDouble()}x{b.GetProperty("height").GetDouble()}"
                    : "bounds=null").ToList();
            return $"{ft} => present x{instances.Count} but unrendered ({string.Join(", ", boundsDesc)})";
        });
        Assert.Fail("Expected these ILSpy pane controls to render with a non-zero size: "
            + string.Join(" | ", diagnosis));
    }

    static bool IsRenderedInstanceOf(JsonElement element, string fullType)
    {
        return element.TryGetProperty("fullType", out var ft) && ft.GetString() == fullType
            && element.TryGetProperty("bounds", out var bounds) && bounds.ValueKind == JsonValueKind.Object
            && bounds.TryGetProperty("width", out var w) && w.GetDouble() > 0
            && bounds.TryGetProperty("height", out var h) && h.GetDouble() > 0;
    }

    static string? ErrorOf(JsonElement result)
    {
        return result.TryGetProperty("error", out var error) ? error.GetString() : null;
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
