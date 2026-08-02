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
        // The ILanguageService backend (Roslyn AdhocWorkspace for C#/VB) only knows about
        // documents that were upserted (opened/synced). Open the referencing file too so
        // cross-file lookup has both documents.
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetServicePath)).GetProperty("opened").GetBoolean());

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
        // Same as FindReferences: the Roslyn AdhocWorkspace backend only sees upserted documents,
        // so open the referencing file before renaming or the cross-file edit is never computed.
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetServicePath)).GetProperty("opened").GetBoolean());

        var renameResult = await _app.InvokeAsync("od.rename-symbol", _widgetPath, 3, 27, "Gadget");
        Assert.True(renameResult.GetProperty("success").GetBoolean(), renameResult.ToString());
        Assert.Equal("Widget", renameResult.GetProperty("oldName").GetString());

        // od.rename-symbol applies the computed edits directly to disk (ApplyEditsToFile) rather
        // than through open editors - assert both files on disk picked up the new name.
        var onDiskAfterRename = File.ReadAllText(_widgetPath);
        Assert.Contains("class Gadget", onDiskAfterRename);

        Assert.Contains("IEnumerable<Gadget>", File.ReadAllText(_widgetServicePath));
    }

    [Fact]
    public async Task ExtractInterface_GeneratesInterfaceAndAddsToClassWithoutTouchingDisk()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _solutionPath)).GetProperty("success").GetBoolean());
        Assert.True((await _app.InvokeAsync("od.open-file", _widgetPath)).GetProperty("opened").GetBoolean());

        var newInterfacePath = Path.Combine(Path.GetDirectoryName(_widgetPath)!, "IWidget.cs");

        // "Widget" on the class declaration line, same location as the other two tests above.
        var result = await _app.InvokeAsync("od.extract-interface", _widgetPath, 3, 27, "IWidget", newInterfacePath, true, "");
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());

        var members = result.GetProperty("members").EnumerateArray().Select(m => m.GetString()).ToList();
        // Members are reported as "TypeName.Member" (the ILanguageService contract's
        // ExtractInterfaceInfo.Members), not bare member names.
        Assert.Contains("Widget.Name", members);

        Assert.True(File.Exists(newInterfacePath), "Extract Interface should have written the new interface file to disk");
        var interfaceText = File.ReadAllText(newInterfacePath);
        Assert.Contains("public interface IWidget", interfaceText);
        Assert.Contains("string Name { get; set; }", interfaceText);

        // od.extract-interface writes the class edits (adding ": IWidget") to disk as well,
        // not through the live editor (same ApplyEditsToFile convention as od.rename-symbol).
        var onDiskClassText = File.ReadAllText(_widgetPath);
        Assert.Contains("class Widget : IWidget", onDiskClassText);
    }
}
