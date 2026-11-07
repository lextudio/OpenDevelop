using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Issue #24: on a project node the Project Browser offered Delete and Remove but neither did
/// anything useful, and Rename was not offered at all. Each journey works on its own copy of
/// SlnxFixture, because it changes files on disk and rewrites the .slnx. Prompts are answered
/// through od.test.queue-dialog-answer: under OD_TEST_MODE they are never shown.
/// </summary>
[Collection("20 General workbench fixture")]
public sealed class ProjectLifecycleTests : IAsyncDisposable
{
    const string Commands = "ICSharpCode.SharpDevelop.Commands.";

    readonly OpenDevelopAppFixture _app;
    readonly string _dir;
    readonly string _slnx;

    public ProjectLifecycleTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "ProjectLifecycleTests-" + Guid.NewGuid().ToString("N"));
        CopyFixtureDirectory(Path.GetDirectoryName(app.SlnxFixturePath)!, _dir);
        _slnx = Path.Combine(_dir, Path.GetFileName(app.SlnxFixturePath));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.InvokeAsync("od.test.clear-dialog-answers");
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // The real context menu, conditions included - not just the command classes being reachable.
    [Fact]
    public async Task ProjectNode_ContextMenu_OffersRenameDeleteAndRemove()
    {
        await OpenAsync();
        var menu = await _app.InvokeAsync("od.project-browser.context-menu", "Project", "Lib");
        Assert.True(menu.GetProperty("success").GetBoolean(), menu.ToString());

        var entries = Flatten(menu.GetProperty("items"))
            .Where(entry => entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            .ToDictionary(entry => entry.GetProperty("id").GetString()!, entry => entry);

        Assert.True(entries.ContainsKey("Rename"), "The project node has no Rename entry: " + menu);
        Assert.True(entries["Rename"].GetProperty("enabled").GetBoolean(), "Rename is disabled: " + menu);
        Assert.True(entries.ContainsKey("Delete"), "The project node has no Delete entry: " + menu);
        Assert.True(entries["Delete"].GetProperty("enabled").GetBoolean(), "Delete is disabled: " + menu);
        Assert.True(entries.ContainsKey("RemoveFromProject"), "The project node has no Remove entry: " + menu);
        Assert.True(entries["RemoveFromProject"].GetProperty("enabled").GetBoolean(), "Remove is disabled: " + menu);
    }

    // Renaming a project renames its file and the .slnx entry, and the project keeps loading and
    // building: a rename that only moves the file leaves a solution naming a path that is gone.
    [Fact]
    public async Task RenameProject_RenamesTheFileAndTheSolutionEntry()
    {
        await OpenAsync();
        await SelectAsync("Project", "Lib");
        await QueueAsync("input", "Lib2");
        await RunAsync("RenameProjectBrowserItemCommand");

        var projectDirectory = Path.Combine(_dir, "Lib");
        Assert.True(File.Exists(Path.Combine(projectDirectory, "Lib2.csproj")),
            "the project file was not renamed; the folder holds: "
            + string.Join(", ", Directory.GetFiles(projectDirectory).Select(Path.GetFileName)));
        Assert.False(File.Exists(Path.Combine(projectDirectory, "Lib.csproj")));
        Assert.Contains("Lib/Lib2.csproj", ProjectsIn("/src/"));
        Assert.DoesNotContain("Lib/Lib.csproj", ProjectsIn("/src/"));

        // Reopening proves the .slnx itself is valid and the project reloads under its new name.
        await OpenAsync();
        Child(Child(await SolutionNodeAsync(), "src", "SolutionFolder"), "Lib2", "Project");

        var build = await _app.InvokeAsync("od.build-solution", "Lib2", "rebuild");
        Assert.True(build.GetProperty("success").GetBoolean(), build.ToString());
        Assert.Equal("Success", build.GetProperty("result").GetString());
    }

    // Deleting a project takes it out of the solution and removes its folder; Remove (covered by
    // SolutionFolderTests) keeps the files, which is the difference the two entries exist for.
    [Fact]
    public async Task DeleteProject_RemovesItFromTheSolutionAndTheDisk()
    {
        await OpenAsync();
        await SelectAsync("Project", "Lib");
        await QueueAsync("question", "yes");
        await RunAsync("DeleteProjectBrowserItemCommand");

        Assert.False(Directory.Exists(Path.Combine(_dir, "Lib")),
            "the project directory is still on disk: " + string.Join(", ", Directory.GetFileSystemEntries(_dir)));
        Assert.DoesNotContain("Lib/Lib.csproj", ProjectsIn("/src/"));
        Assert.DoesNotContain(await ProjectNamesAsync(), name => name == "Lib");

        // The remaining project is untouched and the solution still opens.
        await OpenAsync();
        Assert.Contains("App", await ProjectNamesAsync());
    }

    static IEnumerable<JsonElement> Flatten(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            yield return item;
            if (item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in Flatten(children))
                    yield return child;
            }
        }
    }

    async Task OpenAsync()
    {
        var result = await _app.ReopenSolutionAsync(_slnx);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
    }

    async Task SelectAsync(string kind, string? name)
    {
        var result = await _app.InvokeAsync("od.project-browser.select", kind, name ?? "");
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
    }

    async Task QueueAsync(string kind, params string[] values)
    {
        var result = await _app.InvokeAsync("od.test.queue-dialog-answer", kind, JsonSerializer.Serialize(values));
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
    }

    async Task RunAsync(string command)
    {
        var result = await _app.InvokeAsync("od.menu.invoke", Commands + command);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
    }

    async Task<JsonElement> SolutionNodeAsync()
    {
        var result = await _app.InvokeAsync("od.project-browser.solution-tree");
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        return result.GetProperty("roots")[0];
    }

    async Task<string[]> ProjectNamesAsync()
    {
        var tree = await _app.InvokeAsync("od.solution-tree");
        return tree.GetProperty("projects").EnumerateArray()
            .Select(project => project.GetProperty("name").GetString()!)
            .ToArray();
    }

    static JsonElement Child(JsonElement node, string name, string kind)
    {
        foreach (var child in node.GetProperty("children").EnumerateArray())
        {
            if (child.GetProperty("name").GetString() == name)
            {
                Assert.Equal(kind, child.GetProperty("kind").GetString());
                return child;
            }
        }
        Assert.Fail($"'{node.GetProperty("name").GetString()}' has no child '{name}': {node}");
        return default;
    }

    string[] ProjectsIn(string folder) =>
        XDocument.Load(_slnx).Root!.Elements("Folder")
            .Where(f => (string?)f.Attribute("Name") == folder)
            .SelectMany(f => f.Elements("Project"))
            .Select(e => (string)e.Attribute("Path")!)
            .ToArray();

    static void CopyFixtureDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj") || parts.Contains(".od"))
                continue;
            var target = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
