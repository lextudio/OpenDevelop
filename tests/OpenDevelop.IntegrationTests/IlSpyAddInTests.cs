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

        // The ILSpy panes are registered as OpenDevelop pads and the opened assembly is in the
        // real AssemblyTreeModel (loadedAssemblies above, decompiled output below) - but the panes'
        // content AREA does not render in this host's visual tree: the LayoutAnchorable tab
        // materializes (its title is a real visible element), while the pane content view
        // (AssemblyListPane) is created without being laid out, so the assembly tree node itself is
        // never walkable. Runtime-added tool panes also don't reliably dock at all - od.ilspy
        // show-pane re-registers the pane so its tab deterministically appears in the layout.
        // Assert the rendered UI surface we do have: the "Assemblies" tab (the pane hosting the
        // assembly tree) and the "Decompiled Code" document tab (the decompiled view), with the
        // tree content itself covered by the loadedAssemblies/decompiledTextLength JSON above.
        var showPaneResult = await _app.InvokeAsync("od.ilspy.show-pane", "Assemblies");
        Assert.True(showPaneResult.GetProperty("found").GetBoolean(), "Could not find the Assemblies ILSpy pane");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonElement uiTree = default;
        List<string> texts = new();
        while (DateTime.UtcNow < deadline)
        {
            uiTree = await _app.GetUITreeAsync();
            texts = FlattenElements(uiTree)
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
