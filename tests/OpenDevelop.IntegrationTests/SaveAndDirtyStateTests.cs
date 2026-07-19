using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// Covers file save / dirty-state tracking end-to-end: opening a file starts clean, a real
// AvalonEdit.Document.Insert (not a flag flip) marks it dirty, od.file.save/od.file.save-all clear
// the flag and actually persist the new content to disk. Drives the app via the od.file.* DevFlow
// actions added to OpenDevelopDevFlowActions.cs alongside the existing od.open-file/od.open-solution.
//
// Uses scratch .cs files created next to the SolutionExplorerFixture's SampleApp project (not added
// as project items - dirty-state tracking doesn't require project membership) and deletes them in a
// finally block so the fixture directory is left exactly as it was found.
[Collection("OpenDevelop app")]
public sealed class SaveAndDirtyStateTests
{
    readonly OpenDevelopAppFixture _app;

    public SaveAndDirtyStateTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    string ScratchDirectory => Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp");

    [Fact]
    public async Task OpenFile_IsNotDirtyInitially()
    {
        var path = Path.Combine(ScratchDirectory, "ScratchNotDirty.cs");
        File.WriteAllText(path, "namespace SampleApp { class ScratchNotDirty { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", path);

            var status = await _app.InvokeAsync("od.file.is-dirty", path);
            Assert.True(status.GetProperty("isOpen").GetBoolean());
            Assert.False(status.GetProperty("isDirty").GetBoolean());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task EditFile_MarksDirty()
    {
        var path = Path.Combine(ScratchDirectory, "ScratchEditDirty.cs");
        File.WriteAllText(path, "namespace SampleApp { class ScratchEditDirty { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", path);

            var editResult = await _app.InvokeAsync("od.file.edit-text", path, "\n// edited by test\n");
            Assert.True(editResult.GetProperty("success").GetBoolean());
            Assert.True(editResult.GetProperty("isDirty").GetBoolean());

            var status = await _app.InvokeAsync("od.file.is-dirty", path);
            Assert.True(status.GetProperty("isDirty").GetBoolean());
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task SaveFile_ClearsDirtyFlagAndPersistsContent()
    {
        var path = Path.Combine(ScratchDirectory, "ScratchSave.cs");
        File.WriteAllText(path, "namespace SampleApp { class ScratchSave { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", path);
            await _app.InvokeAsync("od.file.edit-text", path, "\n// saved by test\n");

            var saveResult = await _app.InvokeAsync("od.file.save", path);
            Assert.True(saveResult.GetProperty("success").GetBoolean());
            Assert.False(saveResult.GetProperty("isDirty").GetBoolean());

            var status = await _app.InvokeAsync("od.file.is-dirty", path);
            Assert.False(status.GetProperty("isDirty").GetBoolean());

            var diskContent = File.ReadAllText(path);
            Assert.Contains("// saved by test", diskContent);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task SaveAllOpenFiles_SavesEveryDirtyFile()
    {
        var pathA = Path.Combine(ScratchDirectory, "ScratchSaveAllA.cs");
        var pathB = Path.Combine(ScratchDirectory, "ScratchSaveAllB.cs");
        File.WriteAllText(pathA, "namespace SampleApp { class ScratchSaveAllA { } }");
        File.WriteAllText(pathB, "namespace SampleApp { class ScratchSaveAllB { } }");
        try
        {
            await _app.InvokeAsync("od.open-file", pathA);
            await _app.InvokeAsync("od.open-file", pathB);
            await _app.InvokeAsync("od.file.edit-text", pathA, "\n// A dirty\n");
            await _app.InvokeAsync("od.file.edit-text", pathB, "\n// B dirty\n");

            var result = await _app.InvokeAsync("od.file.save-all");
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Empty(result.GetProperty("stillDirtyFiles").EnumerateArray());

            Assert.False((await _app.InvokeAsync("od.file.is-dirty", pathA)).GetProperty("isDirty").GetBoolean());
            Assert.False((await _app.InvokeAsync("od.file.is-dirty", pathB)).GetProperty("isDirty").GetBoolean());
            Assert.Contains("// A dirty", File.ReadAllText(pathA));
            Assert.Contains("// B dirty", File.ReadAllText(pathB));
        }
        finally
        {
            TryDelete(pathA);
            TryDelete(pathB);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
