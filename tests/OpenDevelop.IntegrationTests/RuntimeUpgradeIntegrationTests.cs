using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Covers the "retarget my project to the next runtime" story end to end: a net10.0 project builds and
/// debugs, and after its target framework is edited to net11.0 the same project builds and debugs again.
/// </summary>
/// <remarks>
/// The retarget is done by rewriting the project file rather than through the IDE, because
/// OpenDevelop has no target-framework UI for SDK-style projects: <c>TargetFramework</c> only models
/// .NET Framework 2.0-4.8.1 and the inherited SharpDevelop upgrade view (Src/Project/Converter) is
/// built on that same legacy list, so it cannot offer net10.0 -> net11.0. Editing the project file
/// and reloading is what a user does today, and it is the build/debug behaviour on either side of
/// that edit that this test is actually about.
/// </remarks>
[Collection("60 Runtime upgrade fixture")]
public sealed class RuntimeUpgradeIntegrationTests : IDisposable
{
    readonly OpenDevelopAppFixture _app;
    readonly string _projectDir;

    public RuntimeUpgradeIntegrationTests(OpenDevelopAppFixture app)
    {
        _app = app;
        // The test rewrites TargetFramework, so it works on a copy instead of this repo's tracked
        // fixture (same reasoning as the NuGet and Git fixtures).
        _projectDir = Path.Combine(Path.GetTempPath(), "RuntimeUpgradeTests-" + Guid.NewGuid().ToString("N"));
        CopyTemplate(_app.RuntimeUpgradeTemplatePath, _projectDir);
    }

    string ProjectPath => Path.Combine(_projectDir, "RuntimeUpgradeApp.csproj");
    string ProgramPath => Path.Combine(_projectDir, "Program.cs");

    [Fact]
    public async Task Net10ProjectBuildsAndDebugs_AfterRetargetingToNet11ItStillBuildsAndDebugs()
    {
        // The second half needs an SDK 11 and a runtime 11 to build and launch on; without them the
        // retarget would fail for a reason that is not about this scenario.
        if (!IsRuntimeInstalled("11."))
            Assert.Skip("No .NET 11 runtime is installed, so the net11.0 half of this scenario cannot run here.");

        var breakpointLine = FindLine(ProgramPath, "var message = ComputeGreeting(\"Runtime\");");

        // ---------- net10.0 ----------
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", File.ReadAllText(ProjectPath));
        await _app.InvokeAsync("od.open-solution", ProjectPath);
        await BuildAndDebugAsync(breakpointLine);

        // ---------- retarget to net11.0 ----------
        // The project file is offered for editing straight from the project's context menu, so this
        // is an edit a user can actually make in the IDE rather than only from outside it.
        var contextMenu = await _app.InvokeAsync("od.project-context-menu", "RuntimeUpgradeApp");
        Assert.Contains(
            contextMenu.GetProperty("labels").EnumerateArray(),
            l => l.GetString()!.Replace("&", "").Contains("Edit Project File", StringComparison.OrdinalIgnoreCase));

        await _app.InvokeAsync("od.open-file", ProjectPath);
        File.WriteAllText(
            ProjectPath,
            File.ReadAllText(ProjectPath)
                .Replace("<TargetFramework>net10.0</TargetFramework>", "<TargetFramework>net11.0</TargetFramework>"));

        // No explicit reopen: an SDK-style project file that changes is re-applied on its own. If it
        // instead put up the "solution was altered externally" prompt, that modal would block the
        // shared UI thread and the next action below would time out rather than quietly pass.
        await WaitForRetargetAsync();

        // ---------- net11.0 ----------
        await BuildAndDebugAsync(breakpointLine);
    }

    async Task BuildAndDebugAsync(int breakpointLine)
    {
        var build = await _app.InvokeAsync("od.build-solution");
        Assert.True(build.GetProperty("success").GetBoolean(), build.ToString());
        if (build.GetProperty("errorCount").GetInt32() != 0)
            Assert.Fail(await _app.DescribeBuildAsync(build));

        await _app.InvokeAsync("od.open-file", ProgramPath);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", ProgramPath, breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", ProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.True(start.GetProperty("isDebugging").GetBoolean(), start.ToString());

            // Stopped on the breakpoint we asked for, not merely "a debugger attached somewhere".
            var stack = await _app.InvokeAsync("od.debug.call-stack");
            Assert.Contains(
                stack.EnumerateArray(),
                f => f.GetProperty("Name").GetString()!.Contains("Main", StringComparison.Ordinal));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Waits for the <em>loaded project model</em> to reflect the new target framework.
    /// </summary>
    /// <remarks>
    /// Asks the model rather than building. A build hands the project path to MSBuild, which re-reads
    /// the file itself, so it succeeds and emits net10.0 output whether or not the workbench ever
    /// noticed the edit - which makes a build useless as evidence that the change was applied.
    /// The watcher also debounces before re-reading, so this is not observable the instant the file
    /// is written.
    /// </remarks>
    async Task WaitForRetargetAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        string lastSeen = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            var properties = await _app.InvokeAsync("od.project.properties", "RuntimeUpgradeApp");
            if (!properties.GetProperty("success").GetBoolean())
                continue;
            lastSeen = properties.GetProperty("properties").GetProperty("TargetFramework").GetString();
            if (lastSeen == "net11.0")
                return;
        }

        throw new InvalidOperationException(
            $"The project file was retargeted to net11.0 but the loaded project model still reports '{lastSeen}'.");
    }

    /// <summary>Looks for an installed shared framework whose version starts with <paramref name="versionPrefix"/>.</summary>
    static bool IsRuntimeInstalled(string versionPrefix)
    {
        // This process runs from <root>/shared/Microsoft.NETCore.App/<version>/, so the dotnet root is
        // three levels up. (ProcessPath would be the test's own apphost, not the dotnet host.)
        var root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var sharedFramework = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(sharedFramework))
            return false;
        return Directory.GetDirectories(sharedFramework)
            .Select(Path.GetFileName)
            .Any(name => name!.StartsWith(versionPrefix, StringComparison.Ordinal));
    }

    static int FindLine(string path, string marker)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        }
        throw new InvalidOperationException($"Marker '{marker}' not found in {path}.");
    }

    static void CopyTemplate(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
    }
}
