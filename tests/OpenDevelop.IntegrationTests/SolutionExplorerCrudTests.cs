using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// Covers Solution Explorer's write-side operations (previously only the read-side od.solution-tree
// was exercised, by SolutionExplorerAndEditorTests): adding/removing/renaming a project file item,
// and adding an assembly reference. Drives the app via the od.solution.* DevFlow actions added to
// OpenDevelopDevFlowActions.cs.
//
// Every test restores SampleApp.csproj to its original bytes (captured before the mutation) in a
// finally block, and deletes any scratch files it created, so this repo-tracked fixture is left
// exactly as it was found regardless of test outcome. Each test re-opens the solution first so it
// starts from that on-disk state rather than a stale in-memory project from a previous test.
[Collection("OpenDevelop app")]
public sealed class SolutionExplorerCrudTests
{
    const string ProjectName = "SampleApp";

    readonly OpenDevelopAppFixture _app;

    public SolutionExplorerCrudTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    string SampleAppDirectory => Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp");
    string ProjectFilePath => Path.Combine(SampleAppDirectory, "SampleApp.csproj");

    [Fact]
    public async Task AddFileToProject_CreatesFileAndAppearsInSolutionTree()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var newFilePath = Path.Combine(SampleAppDirectory, "Models", "ScratchWidgetPart.txt");
        try
        {
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

            // "None" (not "Compile") - SDK-style projects implicitly glob *.cs as Compile items, so
            // adding an explicit <Compile Include> for a file that glob would already pick up
            // creates a duplicate-item build error (NETSDK1022). "None" avoids that entirely.
            var addResult = await _app.InvokeAsync("od.solution.add-file", ProjectName, newFilePath, "None");
            Assert.True(addResult.GetProperty("success").GetBoolean());
            Assert.True(File.Exists(newFilePath));

            var files = addResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.Contains(files, f => f.EndsWith("Models/ScratchWidgetPart.txt"));

            var tree = await _app.InvokeAsync("od.solution-tree");
            var treeFiles = tree.GetProperty("projects").EnumerateArray().Single()
                .GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.Contains(treeFiles, f => f.EndsWith("Models/ScratchWidgetPart.txt"));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(newFilePath);
        }
    }

    // A file added *outside* the project directory (SDK-style implicit item globs, e.g.
    // Microsoft.NET.Sdk.DefaultItems.props' `<None Include="**">`, are rooted at the project
    // directory) is a genuinely explicit ProjectItem, not glob-derived - so it's actually
    // removable via the normal item-collection API. See the sibling test below for what happens
    // to a file *inside* the project directory, which the glob covers regardless of item removal.
    [Fact]
    public async Task RemoveExplicitFileFromProject_DropsItFromTreeButKeepsFileOnDisk()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var sharedDirectory = Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "Shared");
        Directory.CreateDirectory(sharedDirectory);
        var filePath = Path.Combine(sharedDirectory, "ScratchExplicit.txt");
        try
        {
            File.WriteAllText(filePath, "scratch content");
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.solution.add-file", ProjectName, filePath, "None");

            var removeResult = await _app.InvokeAsync("od.solution.remove-file", ProjectName, filePath);
            Assert.True(removeResult.GetProperty("success").GetBoolean());

            var files = removeResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.DoesNotContain(files, f => f.EndsWith("Shared/ScratchExplicit.txt"));

            // Removing the ProjectItem must not touch the file on disk.
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(filePath);
            try { Directory.Delete(sharedDirectory); } catch { }
        }
    }

    // A file physically inside the project directory is always covered by the SDK's own implicit
    // item glob, independent of any explicit ProjectItem add/remove - od.solution.remove-file
    // detects this (item.IsAddedToProject == false) and reports failure honestly instead of
    // silently no-op'ing while claiming success.
    [Fact]
    public async Task RemoveGlobCoveredFile_ReportsUnsupportedInsteadOfSilentlyNoOp()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var filePath = Path.Combine(SampleAppDirectory, "Models", "ScratchToRemove.txt");
        try
        {
            File.WriteAllText(filePath, "scratch content");
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.solution.add-file", ProjectName, filePath, "None");

            var removeResult = await _app.InvokeAsync("od.solution.remove-file", ProjectName, filePath);
            Assert.False(removeResult.GetProperty("success").GetBoolean());
            Assert.Contains("implicit item glob", removeResult.GetProperty("error").GetString());

            var files = removeResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.Contains(files, f => f.EndsWith("Models/ScratchToRemove.txt"));
            Assert.True(File.Exists(filePath));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(filePath);
        }
    }

    [Fact]
    public async Task RenameProjectFile_MovesFileAndUpdatesTree()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        var oldPath = Path.Combine(SampleAppDirectory, "Models", "ScratchOldName.txt");
        var newPath = Path.Combine(SampleAppDirectory, "Models", "ScratchNewName.txt");
        try
        {
            File.WriteAllText(oldPath, "scratch content");
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
            await _app.InvokeAsync("od.solution.add-file", ProjectName, oldPath, "None");

            var renameResult = await _app.InvokeAsync("od.solution.rename-file", ProjectName, oldPath, newPath);
            Assert.True(renameResult.GetProperty("success").GetBoolean());

            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(newPath));

            var files = renameResult.GetProperty("files").EnumerateArray()
                .Select(f => f.GetString()!.Replace('\\', '/')).ToList();
            Assert.DoesNotContain(files, f => f.EndsWith("Models/ScratchOldName.txt"));
            Assert.Contains(files, f => f.EndsWith("Models/ScratchNewName.txt"));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
            TryDelete(oldPath);
            TryDelete(newPath);
        }
    }

    [Fact]
    public async Task AddProjectReference_AddsReferenceProjectItem()
    {
        var originalProjectFile = File.ReadAllText(ProjectFilePath);
        try
        {
            await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);

            var addResult = await _app.InvokeAsync("od.solution.add-reference", ProjectName, "System.Xml");
            Assert.True(addResult.GetProperty("success").GetBoolean());

            var references = addResult.GetProperty("references").EnumerateArray()
                .Select(r => r.GetString()).ToList();
            Assert.Contains("System.Xml", references);

            Assert.Contains("System.Xml", File.ReadAllText(ProjectFilePath));
        }
        finally
        {
            File.WriteAllText(ProjectFilePath, originalProjectFile);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
