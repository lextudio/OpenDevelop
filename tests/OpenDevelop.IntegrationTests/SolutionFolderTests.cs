// Solution folders and solution items in the WPF Project Browser, and that every change to them is
// written back to the .slnx without disturbing the rest of the file (doc/technotes/solution-explorer.md,
// "Solution folders and solution items"). Each test works on its own copy of SlnxFixture because it
// rewrites the .slnx. Prompts are answered through od.test.queue-dialog-answer: under OD_TEST_MODE
// the file pickers, input boxes and questions are never shown.

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class SolutionFolderTests : IAsyncDisposable
{
    const string Commands = "ICSharpCode.SharpDevelop.Commands.";

    readonly OpenDevelopAppFixture _app;
    readonly string _dir;
    readonly string _slnx;

    public SolutionFolderTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "SolutionFolderTests-" + Guid.NewGuid().ToString("N"));
        CopyFixtureDirectory(Path.GetDirectoryName(app.SlnxFixturePath)!, _dir);
        _slnx = Path.Combine(_dir, Path.GetFileName(app.SlnxFixturePath));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.InvokeAsync("od.test.clear-dialog-answers");
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // One journey over the folder commands: a cancelled prompt changes nothing; create, rename and
    // remove each rewrite only the folder element; the result reads back with nesting intact.
    [Fact]
    public async Task OpenSlnx_SolutionFolders_ShowCreateRenameRemoveAndRoundTrip()
    {
        // .slnx lists nested folders flat by full path; "/src/tests/" must nest under "src".
        var document = XDocument.Load(_slnx);
        document.Root!.Add(new XElement("Folder", new XAttribute("Name", "/src/tests/")));
        document.Save(_slnx);
        await OpenAsync();

        var solution = await SolutionNodeAsync();
        Child(Child(solution, "docs", "SolutionFolder"), "readme.md", "SolutionItem");
        var src = Child(solution, "src", "SolutionFolder");
        Child(src, "App", "Project");
        Child(src, "Lib", "Project");
        Child(src, "tests", "SolutionFolder");
        Assert.Equal("tests", src.GetProperty("children")[0].GetProperty("name").GetString());

        // Nothing queued = every prompt cancelled; the file must not be touched.
        var beforeCancel = File.ReadAllText(_slnx);
        await SelectAsync("SolutionFolder", "src");
        await RunAsync("NewSolutionFolderProjectBrowserCommand");
        await RunAsync("AddExistingProjectProjectBrowserCommand");
        await RunAsync("RemoveFromProjectProjectBrowserCommand");
        Assert.Equal(beforeCancel, File.ReadAllText(_slnx));

        await SelectAsync("SolutionFolder", "src");
        await QueueAsync("input", "tools");
        await RunAsync("NewSolutionFolderProjectBrowserCommand");
        Assert.Contains("/src/tools/", FolderNames());
        Child(Child(await SolutionNodeAsync(), "src", "SolutionFolder"), "tools", "SolutionFolder");

        await SelectAsync("SolutionFolder", "tools");
        await QueueAsync("input", "utilities");
        await RunAsync("RenameProjectBrowserItemCommand");
        Assert.Contains("/src/utilities/", FolderNames());
        Assert.DoesNotContain("/src/tools/", FolderNames());

        await SelectAsync("SolutionFolder", "utilities");
        await QueueAsync("question", "yes");
        await RunAsync("RemoveFromProjectProjectBrowserCommand");
        Assert.DoesNotContain("/src/utilities/", FolderNames());

        // Everything the model does not own survived the saves, and the file is still .slnx.
        var root = XDocument.Load(_slnx).Root!;
        Assert.Equal("Solution", root.Name.LocalName);
        Assert.Equal(new[] { "Debug", "Release" }, root.Element("Configurations")!.Elements("BuildType").Select(e => (string?)e.Attribute("Name")));
        Assert.Equal(new[] { "App/App.csproj", "Lib/Lib.csproj" }, ProjectsIn("/src/").OrderBy(p => p));
        Assert.Equal(new[] { "readme.md" }, FilesIn("/docs/"));

        await OpenAsync();
        src = Child(await SolutionNodeAsync(), "src", "SolutionFolder");
        Assert.Equal(new[] { "tests" }, src.GetProperty("children").EnumerateArray()
            .Where(c => c.GetProperty("kind").GetString() == "SolutionFolder")
            .Select(c => c.GetProperty("name").GetString()));
    }

    // One journey over projects and solution items: add each into a folder, remove each again,
    // and the files themselves stay on disk.
    [Fact]
    public async Task OpenSlnx_ExistingProjectAndSolutionItems_AddAndRemove()
    {
        var extraProject = Path.Combine(_dir, "Extra", "Extra.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(extraProject)!);
        File.WriteAllText(extraProject,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        var notes = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(notes, "notes");
        await OpenAsync();

        // Existing Project on a solution folder puts the project inside that folder.
        await SelectAsync("SolutionFolder", "docs");
        await QueueAsync("files", extraProject);
        await RunAsync("AddExistingProjectProjectBrowserCommand");
        Assert.Contains("Extra/Extra.csproj", ProjectsIn("/docs/"));
        Child(Child(await SolutionNodeAsync(), "docs", "SolutionFolder"), "Extra", "Project");

        // Existing Item on the solution node goes to "Solution Items" (.slnx has no top-level File).
        await SelectAsync("Solution", null);
        await QueueAsync("files", notes);
        await RunAsync("AddSolutionItemsProjectBrowserCommand");
        Assert.Equal(new[] { "notes.txt" }, FilesIn("/Solution Items/"));
        Child(Child(await SolutionNodeAsync(), "Solution Items", "SolutionFolder"), "notes.txt", "SolutionItem");

        await SelectAsync("SolutionItem", "notes.txt");
        await RunAsync("RemoveFromProjectProjectBrowserCommand");
        Assert.Empty(FilesIn("/Solution Items/"));
        Assert.True(File.Exists(notes));

        await SelectAsync("Project", "Lib");
        await RunAsync("RemoveFromProjectProjectBrowserCommand");
        Assert.DoesNotContain("Lib/Lib.csproj", ProjectsIn("/src/"));
        Assert.True(File.Exists(Path.Combine(_dir, "Lib", "Lib.csproj")));
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

    string[] FolderNames() =>
        XDocument.Load(_slnx).Root!.Descendants("Folder").Select(e => (string)e.Attribute("Name")!).ToArray();

    string[] ProjectsIn(string folder) => ChildPaths(folder, "Project");

    string[] FilesIn(string folder) => ChildPaths(folder, "File");

    string[] ChildPaths(string folder, string element) =>
        XDocument.Load(_slnx).Root!.Elements("Folder")
            .Where(f => (string?)f.Attribute("Name") == folder)
            .SelectMany(f => f.Elements(element))
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
