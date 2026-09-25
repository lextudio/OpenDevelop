// The WPF Add Reference dialog (AddReferenceWindow): it lists only projects the target does not
// reference yet, ticking one writes a path-only ProjectReference, browsing for assembly files
// writes a Reference with a relative HintPath, and a file that is not a .NET assembly is refused.
// Works on a copy of SlnxFixture with App's ProjectReference to Lib removed. The file picker is
// answered through od.test.queue-dialog-answer; the dialog itself is a real window, clicked for
// real through DevFlow's tap (a native click on macOS since DevFlow 0.2.7 - earlier versions only
// raised Click, which never ticked a CheckBox).

using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class AddReferenceTests : IAsyncDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _dir;
    readonly string _slnx;
    readonly string _appProject;

    public AddReferenceTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "AddReferenceTests-" + Guid.NewGuid().ToString("N"));
        var source = Path.GetDirectoryName(app.SlnxFixturePath)!;
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj") || parts.Contains(".od"))
                continue;
            var target = Path.Combine(_dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        _slnx = Path.Combine(_dir, Path.GetFileName(app.SlnxFixturePath));
        _appProject = Path.Combine(_dir, "App", "App.csproj");
        File.WriteAllText(_appProject, Regex.Replace(File.ReadAllText(_appProject),
            @"\s*<ItemGroup>\s*<ProjectReference[^>]*/>\s*</ItemGroup>", ""));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.InvokeAsync("od.test.clear-dialog-answers");
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task OpenSlnx_AddReference_ProjectAndBrowsedAssembly()
    {
        // Any real .NET assembly will do; the test's own dependency is always present.
        var libs = Path.Combine(_dir, "libs");
        Directory.CreateDirectory(libs);
        var assembly = Path.Combine(libs, "Xunit.Probe.dll");
        File.Copy(typeof(FactAttribute).Assembly.Location, assembly);
        var notAssembly = Path.Combine(libs, "notes.txt");
        File.WriteAllText(notAssembly, "not an assembly");
        var assemblyName = System.Reflection.AssemblyName.GetAssemblyName(assembly).Name!;

        var opened = await _app.ReopenSolutionAsync(_slnx);
        Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
        var selected = await _app.InvokeAsync("od.project-browser.select", "Project", "App");
        Assert.True(selected.GetProperty("success").GetBoolean(), selected.ToString());

        // Taps are real OS clicks, and OD_TEST_MODE never activates the app: an inactive app spends
        // the first click on activating itself, so the CheckBox tap below would be lost.
        var activated = await _app.InvokeAsync("od.activate");
        Assert.True(activated.GetProperty("foregrounded").GetBoolean(), activated.ToString());

        // The dialog is modal: the command only returns once it closes, so drive it meanwhile.
        var command = _app.InvokeAsync("od.menu.invoke", "ICSharpCode.SharpDevelop.Commands.AddReferenceProjectBrowserCommand");
        var dialog = await WaitForDialogAsync();

        // Lib is not referenced yet, so it is offered; tick it.
        Assert.Contains(Texts(dialog), t => t == "Lib");
        await _app.TapAsync(FindId(dialog, "CheckBox", null));
        Assert.Contains("1 project(s)", await WaitForStatusAsync("1 project(s)"));

        var queued = await _app.InvokeAsync("od.test.queue-dialog-answer", "files", JsonSerializer.Serialize(new[] { assembly, notAssembly }));
        Assert.True(queued.GetProperty("success").GetBoolean(), queued.ToString());
        await _app.TapAsync(FindId(dialog, "Button", "Browse..."));
        await _app.TapAsync(FindId(dialog, "Button", "Add"));
        var result = await command;
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());

        var projectReference = Assert.Single(XDocument.Load(_appProject).Descendants("ProjectReference"));
        Assert.Equal("../Lib/Lib.csproj", ((string?)projectReference.Attribute("Include"))?.Replace('\\', '/'));
        Assert.False(projectReference.HasElements, "SDK-style ProjectReference must carry no Project/Name metadata: " + projectReference);

        var references = XDocument.Load(_appProject).Descendants("Reference").ToArray();
        var reference = Assert.Single(references);
        Assert.Equal(assemblyName, (string?)reference.Attribute("Include"));
        Assert.Equal("../libs/Xunit.Probe.dll", ((string?)reference.Element("HintPath"))?.Replace('\\', '/'));
        Assert.DoesNotContain("notes.txt", File.ReadAllText(_appProject));
    }

    async Task<JsonElement> WaitForDialogAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var tree = await _app.GetUITreeAsync();
            foreach (var window in tree.GetProperty("elements").EnumerateArray())
            {
                if (window.TryGetProperty("type", out var type) && type.GetString() == "AddReferenceWindow")
                    return window;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException("The Add Reference dialog did not open.");
    }

    // The tap returns once the OS click has been posted, before the app has necessarily processed
    // it, so poll the dialog's status line rather than reading it once.
    async Task<string> WaitForStatusAsync(string expected)
    {
        var status = string.Empty;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            status = FindText(await WaitForDialogAsync(), "StatusText") ?? string.Empty;
            if (status.Contains(expected, StringComparison.Ordinal))
                break;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        return status;
    }

    static string? FindText(JsonElement element, string id)
    {
        if (element.TryGetProperty("id", out var i) && i.GetString() == id)
            return element.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                if (FindText(child, id) is { } found)
                    return found;
        return null;
    }

    static IEnumerable<string> Texts(JsonElement element)
    {
        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            yield return text.GetString()!;
        if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                foreach (var t in Texts(child))
                    yield return t;
    }

    static string FindId(JsonElement element, string type, string? text)
    {
        if (element.TryGetProperty("type", out var t) && t.GetString() == type
            && (text is null || (element.TryGetProperty("text", out var x) && x.GetString() == text)))
            return element.GetProperty("id").GetString()!;
        if (element.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
            {
                var id = FindId(child, type, text);
                if (id.Length > 0)
                    return id;
            }
        return string.Empty;
    }
}
