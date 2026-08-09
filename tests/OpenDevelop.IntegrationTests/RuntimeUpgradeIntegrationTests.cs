using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Covers the "my project targets a runtime this machine does not have" story end to end: a project
/// that compiles fine but cannot be launched, and the retarget that fixes it.
/// </summary>
/// <remarks>
/// The retarget is done by rewriting the project file rather than through the IDE, because
/// OpenDevelop has no target-framework UI for SDK-style projects: <c>TargetFramework</c> only models
/// .NET Framework 2.0-4.8.1 and the inherited SharpDevelop upgrade view (Src/Project/Converter) is
/// built on that same legacy list, so it cannot offer net6.0 -> net10.0. Editing the project file
/// and reloading is what a user does today, and it is the build/debug behaviour on either side of
/// that edit that this test is actually about.
/// </remarks>
[Collection("OpenDevelop app")]
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
    public async Task Net6ProjectBuildsButCannotDebug_AfterRetargetingToNet10ItBuildsAndDebugs()
    {
        // The first half only means anything when the .NET 6 runtime really is missing. Rather than
        // assume the agent machine's install set, check it: with .NET 6 present the app would launch
        // and the "cannot debug" assertions below would be wrong rather than merely unmet.
        if (IsRuntimeInstalled("6."))
            Assert.Skip("A .NET 6 runtime is installed, so the missing-runtime half of this scenario cannot occur here.");

        var breakpointLine = FindLine(ProgramPath, "var message = ComputeGreeting(\"Runtime\");");

        // ---------- net6.0: compiles, but there is no runtime to launch it on ----------
        Assert.Contains("<TargetFramework>net6.0</TargetFramework>", File.ReadAllText(ProjectPath));

        await _app.InvokeAsync("od.open-solution", ProjectPath);

        var net6Build = await _app.InvokeAsync("od.build-solution");
        Assert.True(net6Build.GetProperty("success").GetBoolean(), net6Build.ToString());
        Assert.Equal(0, net6Build.GetProperty("errorCount").GetInt32());

        await _app.InvokeAsync("od.open-file", ProgramPath);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", ProgramPath, breakpointLine);

        try
        {
            // Must report failure promptly - the same contract as
            // DebugStart_WhenTargetMissing_FailsCleanlyInsteadOfHanging: no hang, no phantom
            // "still debugging" state left behind.
            var net6Start = await _app.InvokeAsync("od.debug.start", ProjectPath, true, 20);
            Assert.False(net6Start.GetProperty("started").GetBoolean(), net6Start.ToString());
            Assert.False(net6Start.GetProperty("isDebugging").GetBoolean(), net6Start.ToString());

            var net6Info = await _app.InvokeAsync("od.debug.service-info");
            Assert.False(net6Info.GetProperty("isDebugging").GetBoolean());
            Assert.False(net6Info.GetProperty("isProcessRunning").GetBoolean());

            var net6Output = await _app.InvokeAsync("od.debug.output");
            Assert.Contains("ERROR", net6Output.GetProperty("text").GetString());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }

        // ---------- retarget to net10.0 ----------
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
                .Replace("<TargetFramework>net6.0</TargetFramework>", "<TargetFramework>net10.0</TargetFramework>"));

        // No explicit reopen: an SDK-style project file that changes is re-applied on its own. If it
        // instead put up the "solution was altered externally" prompt, that modal would block the
        // shared UI thread and the next action below would time out rather than quietly pass.
        await WaitForRetargetAsync();

        // ---------- net10.0: builds and debugs ----------
        var net10Build = await _app.InvokeAsync("od.build-solution");
        Assert.True(net10Build.GetProperty("success").GetBoolean(), net10Build.ToString());
        Assert.Equal(0, net10Build.GetProperty("errorCount").GetInt32());

        await _app.InvokeAsync("od.open-file", ProgramPath);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", ProgramPath, breakpointLine);

        try
        {
            var net10Start = await _app.InvokeAsync("od.debug.start", ProjectPath, true, 45);
            Assert.True(net10Start.GetProperty("stopped").GetBoolean(), net10Start.ToString());
            Assert.True(net10Start.GetProperty("isDebugging").GetBoolean(), net10Start.ToString());

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
            if (lastSeen == "net10.0")
                return;
        }

        throw new InvalidOperationException(
            $"The project file was retargeted to net10.0 but the loaded project model still reports '{lastSeen}'.");
    }

    /// <summary>Looks for an installed shared framework whose version starts with <paramref name="versionPrefix"/>.</summary>
    static bool IsRuntimeInstalled(string versionPrefix)
    {
        // Path.GetDirectoryName of the host gives the dotnet root that this test process was started
        // from, which is the same root the debuggee would be launched on.
        var root = Path.GetDirectoryName(Environment.ProcessPath);
        if (root == null)
            return false;
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
