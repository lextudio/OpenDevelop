// An SDK-style project file changed outside the IDE is re-read in place
// (IProjectService.ReloadProject, doc/technotes/fast-mode.md): the solution stays open, so do its
// documents with their unsaved edits, and the language service gets just that project again.
// Reloading the whole solution instead closed every document - asking to save each modified one
// (silently discarding them under OD_TEST_MODE) - for a change the user never made in the IDE.

using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class ProjectReloadTests : IAsyncDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _dir;

    public ProjectReloadTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "ProjectReloadTests-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.GetDirectoryName(app.SolutionExplorerFixturePath)!, _dir);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.InvokeAsync("od.file.revert-all-dirty"); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ExternalProjectFileChange_ReloadsTheProjectInPlace_KeepingUnsavedEdits()
    {
        var solution = Path.Combine(_dir, Path.GetFileName(_app.SolutionExplorerFixturePath));
        var app = Path.Combine(_dir, "SampleApp");
        var widget = Path.Combine(app, "Models", "Widget.cs");
        var program = Path.Combine(app, "Program.cs");
        Assert.True((await _app.InvokeAsync("od.open-solution", solution)).GetProperty("success").GetBoolean());
        await WaitForMilestoneAsync("roslyn-projects-pushed", TimeSpan.FromMinutes(2));

        await _app.InvokeAsync("od.open-file", widget);
        await _app.InvokeAsync("od.file.edit-text", widget, "\nnamespace SampleApp { public class OdUnsavedThing { } }\n");
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.file.replace-text", program, "Console.WriteLine(",
            "var a = new OdUnsavedThing(); var b = new OdExternalThing(); var c = new OdMissingThing(); Console.WriteLine(");

        // Outside the IDE: a new source file, then a change to the project file.
        File.WriteAllText(Path.Combine(app, "OdExternalThing.cs"), "namespace SampleApp { public class OdExternalThing { } }\n");
        File.SetLastWriteTimeUtc(Path.Combine(app, "SampleApp.csproj"), DateTime.UtcNow);

        // The reload is debounced (0.5 s) and the project is then pushed to the language service:
        // poll for the observable result, the new file's class resolving.
        string[] errors = Array.Empty<string>();
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60)) {
            var diagnostics = await _app.InvokeAsync("od.diagnostics", program);
            errors = diagnostics.GetProperty("diagnostics").EnumerateArray()
                .Where(d => (d.GetProperty("id").GetString() ?? "").StartsWith("CS", StringComparison.Ordinal))
                .Select(d => d.GetProperty("message").GetString() ?? "").ToArray();
            if (!errors.Any(e => e.Contains("'OdExternalThing'", StringComparison.Ordinal)))
                break;
            await Task.Delay(500);
        }

        Assert.True((await _app.InvokeAsync("od.file.is-dirty", widget)).GetProperty("isDirty").GetBoolean(), "Widget.cs lost its unsaved edit.");
        Assert.True((await _app.InvokeAsync("od.file.is-dirty", program)).GetProperty("isDirty").GetBoolean(), "Program.cs lost its unsaved edit.");
        Assert.Contains("OdUnsavedThing", (await _app.InvokeAsync("od.file.query-vs-text-buffer", widget)).GetProperty("text").GetString());
        // The externally added file is in the reloaded project, and the unsaved class is still in
        // the language service's buffer; the class that really does not exist proves diagnostics
        // were computed at all. (Other errors are possible: the copy is never restored, so the
        // framework references may not resolve - that is not what this test is about.)
        Assert.DoesNotContain(errors, e => e.Contains("'OdExternalThing'", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("'OdUnsavedThing'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'OdMissingThing'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExternalTargetFrameworksChange_FallsBackToReloadingTheSolution()
    {
        // The language service cannot drop a TargetFramework slice, so a change to the set of
        // slices must not be applied in place: the stale slice would stay in the Roslyn host.
        var solution = Path.Combine(_dir, Path.GetFileName(_app.SolutionExplorerFixturePath));
        var projectFile = Path.Combine(_dir, "SampleApp", "SampleApp.csproj");
        Assert.True((await _app.InvokeAsync("od.open-solution", solution)).GetProperty("success").GetBoolean());
        await WaitForMilestoneAsync("roslyn-projects-pushed", TimeSpan.FromMinutes(2));
        var logPath = _app.AppLogPath ?? throw new InvalidOperationException("The app log path is unknown.");
        long logStart = new FileInfo(logPath).Length;

        File.WriteAllText(projectFile, File.ReadAllText(projectFile)
            .Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFrameworks>net10.0;net9.0</TargetFrameworks>"));

        string log = "";
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(60)) {
            log = ReadFrom(logPath, logStart);
            if (log.Contains("Reloading solution after", StringComparison.Ordinal))
                break;
            await Task.Delay(500);
        }
        Assert.Contains("The target frameworks of SampleApp changed; reloading the solution.", log);
        Assert.Contains("Reloading solution after", log);
        Assert.DoesNotContain("Reloaded SampleApp after", log);
    }

    static string ReadFrom(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
        return new StreamReader(stream).ReadToEnd();
    }

    async Task WaitForMilestoneAsync(string milestone, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout) {
            var timeline = await _app.InvokeAsync("od.perf.timeline");
            if (timeline.GetProperty("scopes").TryGetProperty("solution-open", out var marks)
                && marks.EnumerateArray().Any(mark => mark.GetProperty("name").GetString() == milestone))
                return;
            await Task.Delay(500);
        }
        Assert.Fail($"'{milestone}' did not appear within {timeout.TotalSeconds:F0} s.");
    }

    static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".od"))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }
    }
}
