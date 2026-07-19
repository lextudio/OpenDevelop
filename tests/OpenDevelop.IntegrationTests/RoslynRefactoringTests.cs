using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage for the Roslyn-based Find References / Rename commands that replaced the
// deleted NRefactory-era RefactoringService (see doc/technotes/csharp-roslyn.md, Phase 3, and
// RoslynWorkspaceHelper.FindReferencesAt/RenameSymbolAsync). Both commands operate across the
// whole solution, so these tests specifically exercise a symbol (SampleApp.Models.Widget) that is
// declared in one file and referenced from another, to catch cross-file regressions - not just
// "it works for the file you happen to have open".
//
// Renaming mutates files on disk/in open editors, so each test copies SolutionExplorerFixture to
// a private temp dir first (same reasoning as GitAddInTests/NuGetAddInTests) rather than mutating
// the tracked fixture the other Solution Explorer tests also read.
//
// The app instance is shared across every test in the "OpenDevelop app" collection (it's only
// started once, see OpenDevelopAppFixture), so RenameSymbol - which intentionally leaves files
// dirty - MUST revert them in DisposeAsync. Otherwise the next test's od.open-solution call runs
// into a real, blocking "save changes?" dialog: the DevFlow HTTP agent lives on the same UI thread
// the dialog steals, so every subsequent action request just hangs forever instead of erroring.
[Collection("OpenDevelop app")]
public sealed class RoslynRefactoringTests : IAsyncLifetime
{
    readonly OpenDevelopAppFixture _app;
    readonly string _solutionDir;
    readonly string _solutionPath;
    readonly string _widgetPath;
    readonly string _widgetServicePath;

    public RoslynRefactoringTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _solutionDir = Path.Combine(Path.GetTempPath(), "RoslynRefactoringTests-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.GetDirectoryName(app.SolutionExplorerFixturePath)!, _solutionDir);
        _solutionPath = Path.Combine(_solutionDir, Path.GetFileName(app.SolutionExplorerFixturePath));
        _widgetPath = Path.Combine(_solutionDir, "SampleApp", "Models", "Widget.cs");
        _widgetServicePath = Path.Combine(_solutionDir, "SampleApp", "Services", "WidgetService.cs");
    }

    public ValueTask InitializeAsync() => default;

    public async ValueTask DisposeAsync()
    {
        try { await _app.InvokeAsync("od.file.revert-all-dirty"); } catch { }
        try { Directory.Delete(_solutionDir, recursive: true); } catch { }
    }

    static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                dir.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                dir.Contains(Path.DirectorySeparatorChar + ".od" + Path.DirectorySeparatorChar))
                continue;
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + ".od" + Path.DirectorySeparatorChar))
                continue;
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
        }
    }

    [Fact]
    public async Task FindReferences_FindsDeclarationAndCrossFileUsage()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _solutionPath)).GetProperty("success").GetBoolean());
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetPath)).GetProperty("opened").GetBoolean());

        // "Widget" on the class declaration line: "    public sealed class Widget" (line 3, 1-based;
        // "Widget" starts at column 25 - use column 27 to stay safely inside the identifier token).
        var result = await _app.InvokeAsync("od.find-references", _widgetPath, 3, 27);

        Assert.True(result.TryGetProperty("count", out var count), result.ToString());
        Assert.True(count.GetInt32() > 0, $"Expected at least one reference, got: {result}");

        var files = result.GetProperty("references").EnumerateArray()
            .Select(r => r.GetProperty("filePath").GetString()!.Replace('\\', '/'))
            .ToList();

        Assert.True(files.Any(f => f.EndsWith("Services/WidgetService.cs")),
            $"Expected a reference from WidgetService.cs (IEnumerable<Widget>), got: {result}");
    }

    [Fact]
    public async Task RenameSymbol_UpdatesDeclarationAndCrossFileUsage()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _solutionPath)).GetProperty("success").GetBoolean());
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetPath)).GetProperty("opened").GetBoolean());

        var renameResult = await _app.InvokeAsync("od.rename-symbol", _widgetPath, 3, 27, "Gadget");
        Assert.True(renameResult.GetProperty("success").GetBoolean(), renameResult.ToString());
        Assert.Equal("Widget", renameResult.GetProperty("oldName").GetString());

        // Rename touches every changed document's editor, which can switch which view is active
        // (e.g. if WidgetService.cs's own write-back happens to run last) - re-open Widget.cs
        // explicitly rather than assuming it's still the active view, and confirm its live editor
        // buffer reflects the change immediately (not just a background write).
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetPath)).GetProperty("opened").GetBoolean());
        var declarationView = await _app.InvokeAsync("od.active-view");
        Assert.EndsWith("Widget.cs", declarationView.GetProperty("fileName").GetString()!.Replace('\\', '/'));
        Assert.Contains("class Gadget", declarationView.GetProperty("textPreview").GetString());

        // WidgetService.cs was never explicitly opened by this test - Rename must open it itself
        // and leave it dirty (unsaved), rather than silently rewriting the file on disk, so the
        // user can see and review every file the rename touched.
        var onDiskAfterRename = File.ReadAllText(_widgetServicePath);
        Assert.Contains("IEnumerable<Widget>", onDiskAfterRename);

        Assert.True((await _app.InvokeAsync("od.open-file", _widgetServicePath)).GetProperty("opened").GetBoolean());
        var serviceView = await _app.InvokeAsync("od.active-view");
        Assert.EndsWith("WidgetService.cs", serviceView.GetProperty("fileName").GetString()!.Replace('\\', '/'));
        Assert.Contains("IEnumerable<Gadget>", serviceView.GetProperty("textPreview").GetString());
    }
}
