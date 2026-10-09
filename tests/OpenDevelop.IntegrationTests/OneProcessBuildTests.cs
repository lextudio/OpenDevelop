// End-to-end cover for the one-process build (doc/technotes/fast-mode.md, "One MSBuild process for
// everything that rebuilds"): with three or more projects to rebuild, BuildEngine builds them in one
// `dotnet msbuild` before the per-project scheduling. The fixture is a chain App -> Feature -> Core
// written to a temporary directory - three projects, because fewer intentionally skip the shared
// process. Core's version is a const, which the compiler copies into whoever reads it, so the new
// value only reaches App's output if every project above Core really rebuilt.

using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class OneProcessBuildTests : IAsyncDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _dir;
    readonly string _slnx;
    readonly string _coreSource;

    public OneProcessBuildTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "OneProcessBuildTests-" + Guid.NewGuid().ToString("N"));
        _slnx = Path.Combine(_dir, "Chain.slnx");
        _coreSource = Path.Combine(_dir, "Core", "Version.cs");
        WriteProject("Core", null, null);
        WriteProject("Feature", "Core", null);
        WriteProject("App", "Feature", "Exe");
        File.WriteAllText(_coreSource, CoreSource("v1"));
        File.WriteAllText(Path.Combine(_dir, "Feature", "Feature.cs"),
            "namespace Feature { public static class Feature { public static string Value() => Core.Version.Value; } }\n");
        File.WriteAllText(Path.Combine(_dir, "App", "Program.cs"),
            "System.Console.WriteLine(\"value=\" + Feature.Feature.Value());\n");
        File.WriteAllText(_slnx, "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n  <Project Path=\"Feature/Feature.csproj\" />\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ChangeAtTheBottomOfTheChain_RebuildsEverythingInOneProcess()
    {
        await OpenAndBuildAsync();
        Assert.Equal("value=v1", RunApp());

        File.WriteAllText(_coreSource, CoreSource("v2"));
        var result = await _app.InvokeAsync("od.build-solution");
        Assert.Equal("Success", result.GetProperty("result").GetString());
        Assert.Contains("built-in-one-process", await BuildMilestonesAsync());
        Assert.Equal("value=v2", RunApp());
    }

    [Fact]
    public async Task FailureInTheSharedBuild_IsReportedAndRecoveredFrom()
    {
        await OpenAndBuildAsync();

        // Line 3 misses its semicolon: CS1002 at Core/Version.cs(3, ...).
        File.WriteAllText(_coreSource, "namespace Core {\n  public static class Version {\n    public const string Value = \"v3\"\n  }\n}\n");
        var failed = await _app.InvokeAsync("od.build-solution");
        Assert.Equal("Error", failed.GetProperty("result").GetString());
        var errors = failed.GetProperty("diagnostics").EnumerateArray().Where(d => !d.GetProperty("isWarning").GetBoolean()).ToList();
        Assert.Contains(errors, d => d.GetProperty("errorCode").GetString() == "CS1002"
            && Path.GetFullPath(d.GetProperty("fileName").GetString()!) == Path.GetFullPath(_coreSource)
            && d.GetProperty("line").GetInt32() == 3);
        // The failed project must not have been recorded as built: a second build fails the same way.
        var again = await _app.InvokeAsync("od.build-solution");
        Assert.Equal("Error", again.GetProperty("result").GetString());

        File.WriteAllText(_coreSource, CoreSource("v3"));
        var fixedBuild = await _app.InvokeAsync("od.build-solution");
        Assert.Equal("Success", fixedBuild.GetProperty("result").GetString());
        Assert.Equal("value=v3", RunApp());
    }

    async Task OpenAndBuildAsync()
    {
        Assert.True((await _app.InvokeAsync("od.open-solution", _slnx)).GetProperty("success").GetBoolean());
        var first = await _app.InvokeAsync("od.build-solution");
        Assert.Equal("Success", first.GetProperty("result").GetString());
    }

    async Task<string[]> BuildMilestonesAsync() =>
        (await _app.InvokeAsync("od.perf.timeline")).GetProperty("scopes").GetProperty("build")
            .EnumerateArray().Select(mark => mark.GetProperty("name").GetString() ?? "").ToArray();

    string RunApp()
    {
        var dll = Path.Combine(_dir, "App", "bin", "Debug", "net10.0", "App.dll");
        Assert.True(File.Exists(dll), "App was not built: " + dll);
        // The same host the fixture runs OpenDevelop with: a bare "dotnet" on this process's PATH
        // can be another installation without the runtime the app targets.
        var start = new ProcessStartInfo(OpenDevelopAppFixture.ResolveDotNetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = process.StandardError.ReadToEnd().Trim();
        Assert.True(process.WaitForExit(60_000), "App did not exit.");
        Assert.True(error.Length == 0, "App wrote to stderr: " + error);
        return output;
    }

    void WriteProject(string name, string? reference, string? outputType)
    {
        Directory.CreateDirectory(Path.Combine(_dir, name));
        File.WriteAllText(Path.Combine(_dir, name, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n"
            + (outputType != null ? "    <OutputType>" + outputType + "</OutputType>\n" : "")
            + "  </PropertyGroup>\n"
            + (reference != null ? "  <ItemGroup>\n    <ProjectReference Include=\"../" + reference + "/" + reference + ".csproj\" />\n  </ItemGroup>\n" : "")
            + "</Project>\n");
    }

    static string CoreSource(string value) =>
        "namespace Core {\n  public static class Version {\n    public const string Value = \"" + value + "\";\n  }\n}\n";
}
