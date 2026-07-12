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
// Prerequisites:
//   1. Build OpenDevelop in Debug:
//        dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
//   2. Build the fixture assembly this test opens in ILSpy:
//        dotnet build tests/fixtures/DebugTestApp/DebugTestApp.csproj -c Debug
[Collection("OpenDevelop app")]
public sealed class IlSpyAddInTests
{
    readonly OpenDevelopAppFixture _app;

    public IlSpyAddInTests(OpenDevelopAppFixture app)
    {
        _app = app;
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

        // OpenDevelop "enters the ILSpy layout": the four real ILSpy pads are registered as
        // OpenDevelop pads (DockWorkspace.ToolPanes, via DockWorkspaceExtensibility.AddToolPane)
        // and visible, instead of the legacy launch-ILSpy.exe integration.
        var panes = status.GetProperty("panes").EnumerateArray()
            .Select(p => (Title: p.GetProperty("title").GetString(), IsVisible: p.GetProperty("isVisible").GetBoolean()))
            .ToList();

        // "Search"/"Analyze" are ILSpy's own real pane titles (SearchPaneModel/
        // AnalyzerTreeViewModel set them in their constructors); "Assemblies" and "Decompiled
        // Code" are ones this addin assigns itself (see IlSpyWorkspaceHost).
        foreach (var expectedTitle in new[] { "Assemblies", "Search", "Analyze", "Decompiled Code"})
        {
            var pane = panes.SingleOrDefault(p => p.Title == expectedTitle);
            Assert.True(pane != default, $"Expected an ILSpy pad titled '{expectedTitle}' to be registered; got: {string.Join(", ", panes.Select(p => p.Title))}");
            Assert.True(pane.IsVisible, $"Expected the '{expectedTitle}' pad to be visible after opening an assembly");
        }

        // Assembly tree pad: the opened assembly shows up in the real ILSpy AssemblyList.
        var loadedAssemblies = status.GetProperty("loadedAssemblies").EnumerateArray()
            .Select(a => a.GetString())
            .ToList();
        Assert.Contains("DebugTestApp", loadedAssemblies);

        // Decompiled Code pad: opening the assembly auto-selects and decompiles its tree node,
        // so the real DecompilerTextView should show non-empty, real decompiled output (not a
        // blank/placeholder pane).
        Assert.True(status.GetProperty("decompiledTextLength").GetInt32() > 0,
            "Expected the Decompiled Code pad to show non-empty decompiled output after opening an assembly");
    }
}
