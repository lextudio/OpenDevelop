// End-to-end cover for the "Stop solution build after first failure" option.
//  - Two independent projects, built one at a time so the failing project really starts first:
//    with the option on, the second project must not be built at all; with it off, it must be
//    built as before (the per-project scheduler).
//  - Four projects, enough stale ones for the one-process build to take them (it needs three):
//    one fails in the first wave, so the next wave never runs. Whether the project in that next
//    wave, which depends on one that built, is then built on its own is what the option decides.

using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class StopAfterFirstFailureBuildTests : IAsyncDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _dir;
    readonly string _slnx;
    readonly string _oneProcessSlnx;

    public StopAfterFirstFailureBuildTests(OpenDevelopAppFixture app)
    {
        _app = app;
        _dir = Path.Combine(Path.GetTempPath(), "StopAfterFirstFailure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        WriteProject("AFails", "namespace AFails {\n  public class A {\n    public void M() { int x = 1 }\n  }\n}\n");
        WriteProject("BIndependent", "namespace BIndependent {\n  public class B {\n    public int Value() => 42;\n  }\n}\n");
        _slnx = Path.Combine(_dir, "StopAfterFirstFailure.slnx");
        // The scheduler picks the first ready project by name when their dependent counts tie, so
        // AFails is started first and the second project is the one the option must skip.
        File.WriteAllText(_slnx, "<Solution>\n  <Project Path=\"AFails/AFails.csproj\" />\n  <Project Path=\"BIndependent/BIndependent.csproj\" />\n</Solution>\n");

        // Wave 0 of the one-process build: WaveFails (compile error), WaveLib and WaveIndependent
        // (both fine). Wave 1: WaveApp, which references only the healthy WaveLib.
        WriteProject("WaveFails", "namespace WaveFails {\n  public class F {\n    public void M() { int x = 1 }\n  }\n}\n");
        WriteProject("WaveLib", "namespace WaveLib {\n  public class L {\n    public int Value() => 1;\n  }\n}\n");
        WriteProject("WaveIndependent", "namespace WaveIndependent {\n  public class I {\n    public int Value() => 2;\n  }\n}\n");
        WriteProject("WaveApp", "namespace WaveApp {\n  public class A {\n    public int Value() => new WaveLib.L().Value();\n  }\n}\n", "WaveLib");
        _oneProcessSlnx = Path.Combine(_dir, "OneProcess.slnx");
        File.WriteAllText(_oneProcessSlnx, "<Solution>\n  <Project Path=\"WaveFails/WaveFails.csproj\" />\n  <Project Path=\"WaveLib/WaveLib.csproj\" />\n  <Project Path=\"WaveIndependent/WaveIndependent.csproj\" />\n  <Project Path=\"WaveApp/WaveApp.csproj\" />\n</Solution>\n");
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SettingOn_AfterAProjectFails_DoesNotBuildAnIndependentProject()
    {
        var preference = await _app.InvokeAsync("od.build.set-stop-after-first-failure", true);
        try {
            Assert.True((await _app.InvokeAsync("od.open-solution", _slnx)).GetProperty("success").GetBoolean());
            var result = await BuildAsync();

            Assert.Equal("Error", result.GetProperty("result").GetString());
            // The failing project's own errors are still reported.
            Assert.Contains(result.GetProperty("diagnostics").EnumerateArray(),
                d => !d.GetProperty("isWarning").GetBoolean() && d.GetProperty("errorCode").GetString() == "CS1002");
            Assert.False(File.Exists(OutputPath("BIndependent")),
                "BIndependent was built even though the solution build had already failed.");
            var log = result.GetProperty("buildLog").GetString() ?? "";
            Assert.Contains("BIndependent was not built because an earlier project failed.", log);
            AssertOnlyProjectsAreReportedAsSkipped(log, 1);
        } finally {
            await _app.InvokeAsync("od.build.set-stop-after-first-failure", preference.GetProperty("previous").GetBoolean());
        }
    }

    [Fact]
    public async Task SettingOff_AfterAProjectFails_StillBuildsAnIndependentProject()
    {
        var preference = await _app.InvokeAsync("od.build.set-stop-after-first-failure", false);
        try {
            Assert.True((await _app.InvokeAsync("od.open-solution", _slnx)).GetProperty("success").GetBoolean());
            var result = await BuildAsync();

            Assert.Equal("Error", result.GetProperty("result").GetString());
            Assert.True(File.Exists(OutputPath("BIndependent")),
                "BIndependent was not built even though the option is off.");
            Assert.DoesNotContain("BIndependent was not built because an earlier project failed.",
                result.GetProperty("buildLog").GetString() ?? "");
        } finally {
            await _app.InvokeAsync("od.build.set-stop-after-first-failure", preference.GetProperty("previous").GetBoolean());
        }
    }

    [Fact]
    public async Task SettingOn_OneProcessBuild_DoesNotBuildTheNextWaveAfterAFailure()
    {
        var preference = await _app.InvokeAsync("od.build.set-stop-after-first-failure", true);
        try {
            Assert.True((await _app.InvokeAsync("od.open-solution", _oneProcessSlnx)).GetProperty("success").GetBoolean());
            var result = await BuildAsync(parallelProjectCount: 4);
            var log = result.GetProperty("buildLog").GetString() ?? "";

            Assert.Equal("Error", result.GetProperty("result").GetString());
            Assert.Contains(result.GetProperty("diagnostics").EnumerateArray(),
                d => !d.GetProperty("isWarning").GetBoolean() && d.GetProperty("errorCode").GetString() == "CS1002");
            // Proves the failure happened in the one-process build, not in a per-project build.
            Assert.Contains("WaveFails failed in the one-process build", log);
            // The healthy projects of the failing wave had already built.
            Assert.True(File.Exists(OutputPath("WaveLib")), "WaveLib should have built in the first wave.");
            Assert.True(File.Exists(OutputPath("WaveIndependent")), "WaveIndependent should have built in the first wave.");
            // The next wave never ran, and the option keeps it from being built on its own afterwards.
            Assert.False(File.Exists(OutputPath("WaveApp")),
                "WaveApp was built even though the solution build had already failed.");
            Assert.Contains("WaveApp was not built because an earlier project failed.", log);
            AssertOnlyProjectsAreReportedAsSkipped(log, 1);
        } finally {
            await _app.InvokeAsync("od.build.set-stop-after-first-failure", preference.GetProperty("previous").GetBoolean());
        }
    }

    [Fact]
    public async Task SettingOff_OneProcessBuild_StillBuildsTheNextWaveOnItsOwn()
    {
        var preference = await _app.InvokeAsync("od.build.set-stop-after-first-failure", false);
        try {
            Assert.True((await _app.InvokeAsync("od.open-solution", _oneProcessSlnx)).GetProperty("success").GetBoolean());
            var result = await BuildAsync(parallelProjectCount: 4);
            var log = result.GetProperty("buildLog").GetString() ?? "";

            Assert.Equal("Error", result.GetProperty("result").GetString());
            Assert.Contains("WaveFails failed in the one-process build", log);
            Assert.True(File.Exists(OutputPath("WaveApp")),
                "WaveApp references only a project that built, so it should be built on its own when the option is off.");
            Assert.DoesNotContain("was not built because an earlier project failed.", log);
        } finally {
            await _app.InvokeAsync("od.build.set-stop-after-first-failure", preference.GetProperty("previous").GetBoolean());
        }
    }

    // The solution itself is skipped too once a project failed, but it is not a project: only the
    // projects that were really held back are named in the log.
    static void AssertOnlyProjectsAreReportedAsSkipped(string log, int expected) =>
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.Matches(log, "was not built because an earlier project failed\\.").Count);

    Task<JsonElement> BuildAsync(int parallelProjectCount = 1) => _app.InvokeAsync("od.build-solution", "", "", parallelProjectCount);

    string OutputPath(string name) => Path.Combine(_dir, name, "bin", "Debug", "net10.0", name + ".dll");

    void WriteProject(string name, string source, params string[] references)
    {
        Directory.CreateDirectory(Path.Combine(_dir, name));
        var projectReferences = references.Length == 0 ? "" :
            "  <ItemGroup>\n" + string.Concat(references.Select(r => "    <ProjectReference Include=\"../" + r + "/" + r + ".csproj\" />\n")) + "  </ItemGroup>\n";
        File.WriteAllText(Path.Combine(_dir, name, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n" + projectReferences + "</Project>\n");
        File.WriteAllText(Path.Combine(_dir, name, name + ".cs"), source);
    }
}
