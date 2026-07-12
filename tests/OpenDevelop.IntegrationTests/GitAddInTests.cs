using System.Diagnostics;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

// End-to-end coverage of the Git addin's Project Browser overlay icons
// (src/AddIns/VersionControl/GitAddIn/Src/OverlayIconManager.cs): opening a solution that lives
// inside a real git working copy with dirty/staged/clean files should make the real
// ProjectBrowserView.xaml overlay <Image> bound to each file's node carry an AutomationId that
// reflects GitStatusCache's status for that file - the same status the overlay icon itself is
// drawn from (see OverlayIconManager.GitProjectBrowserOverlayProvider.GetOverlayKey). This walks
// the real WPF visual tree via od.ui.tree instead of querying GitStatusCache/the overlay service
// directly, so the test actually exercises the UI binding chain, not just the backend cache.
//
// Prerequisites:
//   1. Build OpenDevelop in Debug:
//        dotnet build src/Main/SharpDevelop/SharpDevelop.csproj -c Debug
//   2. A real `git` executable available on PATH.
[Collection("OpenDevelop app")]
public sealed class GitAddInTests : IDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _repoDir;

    public GitAddInTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _repoDir = Path.Combine(Path.GetTempPath(), "GitAddInTests-" + Guid.NewGuid().ToString("N"));
        SetUpGitRepo();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    void SetUpGitRepo()
    {
        CopyDirectory(_app.GitFixtureTemplatePath, _repoDir);

        RunGit("init -q");
        RunGit("add GitFixture.sln GitFixtureApp/GitFixtureApp.csproj GitFixtureApp/Clean.cs GitFixtureApp/Modified.cs");
        RunGit("commit -q -m initial");

        // Unstaged modification of a tracked file -> "M" in `git status --porcelain` -> GitStatus.Modified.
        File.AppendAllText(Path.Combine(_repoDir, "GitFixtureApp", "Modified.cs"), "\n// dirtied by GitAddInTests\n");

        // Staged-but-uncommitted new file -> "A" in `git status --porcelain` -> GitStatus.Added.
        RunGit("add GitFixtureApp/Added.cs");

        // GitFixtureApp/Untracked.cs is left untouched: `git status --porcelain --untracked-files=no`
        // (what GitStatusCache actually runs) never reports it, so it should show no overlay at all.
    }

    static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                dir.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            Directory.CreateDirectory(dir.Replace(sourceDir, destDir));
        }
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                continue;
            File.Copy(file, file.Replace(sourceDir, destDir), overwrite: true);
        }
    }

    void RunGit(string arguments)
    {
        var psi = new ProcessStartInfo("git", $"-c user.name=\"OpenDevelop Test\" -c user.email=\"test@example.invalid\" {arguments}")
        {
            WorkingDirectory = _repoDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed ({process.ExitCode}): {stdout}\n{stderr}");
    }

    [Fact]
    public async Task AddInsList_ContainsGitAddIn()
    {
        var result = await _app.InvokeAsync("od.addins");
        var addins = result.GetProperty("addins").EnumerateArray().ToList();
        Assert.Contains(addins, a => a.GetProperty("fileName").GetString()!.Contains("GitAddIn.addin"));
    }

    [Fact]
    public async Task OpenSolution_WithGitRepo_OverlayIconsReflectFileStatus()
    {
        var solutionPath = Path.Combine(_repoDir, "GitFixture.sln");
        var openResult = await _app.InvokeAsync("od.open-solution", solutionPath);
        Assert.True(openResult.GetProperty("success").GetBoolean(), $"Failed to open {solutionPath}");

        // The Project Browser pad's TreeView content is only realized by AvalonDock once the pad
        // is actually shown/activated - opening a solution alone doesn't force that, so without
        // this the UI tree below would contain zero file nodes even though the solution loaded fine.
        var showPadResult = await _app.InvokeAsync("od.show-pad", "ProjectBrowserPad");
        Assert.True(showPadResult.GetProperty("found").GetBoolean(), "Could not find the ProjectBrowser pad");

        var tree = await _app.GetUITreeAsync();
        var elements = FlattenElements(tree).ToList();

        AssertOverlayStatus(elements, "Clean.cs", "OK");
        AssertOverlayStatus(elements, "Modified.cs", "Modified");
        AssertOverlayStatus(elements, "Added.cs", "Added");
        AssertOverlayStatus(elements, "Untracked.cs", null);
    }

    static void AssertOverlayStatus(List<JsonElement> elements, string fileName, string? expectedStatus)
    {
        var textNode = elements.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "TextBlock" &&
            e.TryGetProperty("text", out var txt) && txt.GetString() == fileName);
        Assert.True(textNode.ValueKind != JsonValueKind.Undefined, $"No TextBlock found with Text == '{fileName}' in the Project Browser tree");

        string stackPanelId = textNode.GetProperty("parentId").GetString()!;
        var gridNode = elements.FirstOrDefault(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "Grid" &&
            e.TryGetProperty("parentId", out var p) && p.GetString() == stackPanelId);
        Assert.True(gridNode.ValueKind != JsonValueKind.Undefined, $"No icon Grid found as a sibling of the '{fileName}' TextBlock");

        string gridId = gridNode.GetProperty("id").GetString()!;
        var images = elements.Where(e =>
            e.TryGetProperty("type", out var t) && t.GetString() == "Image" &&
            e.TryGetProperty("parentId", out var p) && p.GetString() == gridId).ToList();
        Assert.Equal(2, images.Count);

        // The overlay <Image> is the smaller (8x8) one; the file/folder icon <Image> is 16x16.
        var overlayImage = images.OrderBy(i => i.GetProperty("bounds").GetProperty("width").GetDouble()).First();

        string? automationId = overlayImage.TryGetProperty("automationId", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(automationId))
            automationId = null;

        Assert.Equal(expectedStatus, automationId);
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
