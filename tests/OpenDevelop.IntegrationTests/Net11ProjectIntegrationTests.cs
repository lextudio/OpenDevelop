using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// A project that targets net11.0. With only an SDK 10 selected its build fails with NETSDK1045; with
/// an SDK 11 selected OpenDevelop builds it with that SDK and starts it on that SDK's runtime.
/// Each journey selects a root that holds exactly one SDK (<see cref="IsolatedSdkRoot"/>), so the
/// outcome does not depend on which SDKs happen to be installed side by side.
///
/// Each test starts its own IDE (<see cref="IsolatedIde"/>), so the SDK selection it makes never
/// reaches the shared app instance.
/// </summary>
[Collection("92 .NET 11 project (isolated IDE)")]
public sealed class Net11ProjectIntegrationTests
{
    [Fact]
    public async Task Net11Project_LoadsButFailsToBuild_WhenOnlySdk10IsSelected()
    {
        using var work = new WorkFolder();
        using var sdk10 = IsolatedSdkRoot.Create(10, work.Path);
        string solution = await CreateNet11ConsoleSolutionAsync(work.Path);
        using var ide = await IsolatedIde.StartAsync(work.CreateConfigDirectory());

        var added = await ide.InvokeAsync("od.sdk.add-custom-root", sdk10.Path);
        Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());
        await SdkRootCandidates.SelectAsync(ide, sdk10.Path);

        await OpenAndWaitForItemsAsync(ide, solution);

        var build = await ide.InvokeAsync("od.build-solution", "App11", "rebuild");
        Assert.NotEqual("Success", build.GetProperty("result").GetString());
        Assert.Contains(build.GetProperty("diagnostics").EnumerateArray(), d =>
            d.GetProperty("errorCode").GetString() == "NETSDK1045"
            && (d.GetProperty("errorText").GetString() ?? "").Contains(".NET 11.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Net11Project_BuildsAndRuns_WhenSdk11IsSelected()
    {
        using var work = new WorkFolder();
        using var sdk11 = IsolatedSdkRoot.Create(11, work.Path);
        string sdk11Root = sdk11.Path;
        string sdk11Version = sdk11.SdkVersion;
        string solution = await CreateNet11ConsoleSolutionAsync(work.Path);
        using var ide = await IsolatedIde.StartAsync(work.CreateConfigDirectory());

        var added = await ide.InvokeAsync("od.sdk.add-custom-root", sdk11Root);
        Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());
        await SdkRootCandidates.SelectAsync(ide, sdk11Root);

        await OpenAndWaitForItemsAsync(ide, solution);

        var build = await ide.InvokeAsync("od.build-solution", "App11", "rebuild");
        Assert.True(build.GetProperty("result").GetString() == "Success",
            $"net11.0 did not build with SDK {sdk11Version}: {build}");

        var run = await ide.InvokeAsync("od.run-project", "App11");
        Assert.True(run.GetProperty("success").GetBoolean(), run.ToString());

        // The program prints its runtime version; it must have run to completion on .NET 11.
        JsonElement status = default;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            status = await ide.InvokeAsync("od.run-status");
            if (status.TryGetProperty("running", out var running) && !running.GetBoolean())
                break;
            await Task.Delay(500);
        }
        string output = (await ide.InvokeAsync("od.run-output")).GetProperty("text").GetString() ?? "";
        Assert.True(status.TryGetProperty("exitCode", out var exitCode) && exitCode.GetInt32() == 0,
            $"The net11.0 program did not exit cleanly. Status: {status}. Output: {output}");
        Assert.Contains("HELLO-NET11 11.", output, StringComparison.Ordinal);

        // What a user presses: Debug > Start Without Debugging (Ctrl+F5) and Start Debugging (F5)
        // must find the selected SDK 11 root: Start Debugging launches the program with a host of its own, and it must
        // still be that root, the only place a .NET 11 runtime exists here.
        // Start Without Debugging runs the program without the IDE tracking it (no debug session),
        // so a clean exit is what shows it found that runtime.
        var withoutDebugging = await ide.InvokeAsync("od.debug.run-without-debugging", "App11", 60);
        Assert.True(withoutDebugging.GetProperty("success").GetBoolean(), withoutDebugging.ToString());
        Assert.Equal(0, withoutDebugging.GetProperty("exitCode").GetInt32());

        var debugging = await ide.InvokeAsync("od.debug.start", null!, false, 30);
        Assert.True(debugging.GetProperty("started").GetBoolean(), debugging.ToString());
        await WaitForDebugOutputAsync(ide, "HELLO-NET11 11.", "Start Debugging");
        await ide.InvokeAsync("od.debug.stop");
    }

    [Fact]
    public async Task Net11AsyncProject_DebuggerStopsStepsAndKeepsTheUserAsyncFrameOnTop()
    {
        const string breakpointMarker = "Console.WriteLine(answer);";
        const string nextStatementMarker = "answer++;";

        using var work = new WorkFolder();
        string project = Path.Combine(work.Path, "project", "App11.csproj");
        string solution = await CreateNet11ConsoleSolutionAsync(work.Path, """
            using System;
            using System.Threading.Tasks;

            internal static class Program
            {
                public static async Task Main()
                {
                    await PauseAtUserCodeAsync();
                }

                static async Task PauseAtUserCodeAsync()
                {
                    await Task.Delay(3000);
                    int answer = 41;
                    Console.WriteLine(answer);
                    answer++;
                    Console.WriteLine(answer);
                }
            }
            """);
        string program = Path.Combine(work.Path, "project", "Program.cs");
        int breakpointLine = FindLine(program, breakpointMarker);
        int nextStatementLine = FindLine(program, nextStatementMarker);
        using var ide = await IsolatedIde.StartAsync(work.CreateConfigDirectory());

        foreach (string candidate in SdkRootCandidates.WithSdk11())
        {
            var added = await ide.InvokeAsync("od.sdk.add-custom-root", candidate);
            Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());
        }
        string? sdk11Root = FindSdk11Root((await ide.InvokeAsync("od.sdk.list")).GetProperty("sdks"));
        Assert.True(sdk11Root is not null, "The required net11 async debugger journey needs an installed SDK 11 root.");
        await SdkRootCandidates.SelectAsync(ide, sdk11Root!);
        await OpenAndWaitForItemsAsync(ide, solution);

        var build = await ide.InvokeAsync("od.build-solution", "App11", "rebuild");
        Assert.True(build.GetProperty("result").GetString() == "Success",
            $"net11.0 async project did not build with the selected SDK 11 root {sdk11Root}: {build}");

        await ide.InvokeAsync("od.open-file", program);
        await ide.InvokeAsync("od.debug.clear-breakpoints");
        var breakpoint = await ide.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);
        Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());

        try
        {
            var start = await ide.InvokeAsync("od.debug.start", project, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.EndsWith("Program.cs", start.GetProperty("currentFile").GetString());
            Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

            var stack = await ide.InvokeAsync("od.debug.call-stack");
            var top = stack.EnumerateArray().First();
            Assert.Contains("PauseAtUserCodeAsync", top.GetProperty("Name").GetString(), StringComparison.Ordinal);
            Assert.Contains(stack.EnumerateArray(), frame =>
                (frame.GetProperty("Name").GetString() ?? "").Contains("Main", StringComparison.Ordinal));
            Assert.DoesNotContain(stack.EnumerateArray(), frame =>
            {
                string name = frame.GetProperty("Name").GetString() ?? "";
                return name.Contains("System.Environment.CallEntryPoint", StringComparison.Ordinal)
                    || name.Contains("System.Runtime.EH.", StringComparison.Ordinal);
            });

            var locals = await ide.InvokeAsync("od.debug.locals");
            Assert.Contains(locals.EnumerateArray(), value =>
                value.GetProperty("Name").GetString() == "answer"
                && value.GetProperty("Value").GetString()!.Contains("41", StringComparison.Ordinal));
            Assert.Equal("41", (await ide.InvokeAsync("od.debug.evaluate", "answer")).GetProperty("Value").GetString());

            var step = await ide.InvokeAsync("od.debug.step-over", 30);
            Assert.True(step.GetProperty("stopped").GetBoolean(), step.ToString());
            Assert.Equal(nextStatementLine, step.GetProperty("currentLine").GetInt32());

            var stackAfterStep = await ide.InvokeAsync("od.debug.call-stack");
            Assert.Contains("PauseAtUserCodeAsync", stackAfterStep.EnumerateArray().First().GetProperty("Name").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await ide.InvokeAsync("od.debug.stop");
        }
    }

    static async Task WaitForDebugOutputAsync(IsolatedIde ide, string expected, string what)
    {
        string text = "";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            text = (await ide.InvokeAsync("od.debug.output")).GetProperty("text").GetString() ?? "";
            if (text.Contains(expected, StringComparison.Ordinal))
                return;
            await Task.Delay(500);
        }
        Assert.Fail($"{what}: the program's output never reached the Debug channel. Debug output: {text}");
    }

    static async Task<string> CreateNet11ConsoleSolutionAsync(string root, string? programSource = null)
    {
        string projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App11.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n" +
            "    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        await File.WriteAllTextAsync(Path.Combine(projectDir, "Program.cs"),
            programSource ?? "System.Console.WriteLine(\"HELLO-NET11 \" + System.Environment.Version);\n");
        string solution = Path.Combine(projectDir, "App11.slnx");
        await File.WriteAllTextAsync(solution, "<Solution>\n  <Project Path=\"App11.csproj\" />\n</Solution>\n");
        return solution;
    }

    /// <summary>Opens the solution and waits until App11 is evaluated with its items (the IDE's own SDK can load net11.0).</summary>
    static async Task OpenAndWaitForItemsAsync(IsolatedIde ide, string solution)
    {
        var open = await ide.InvokeAsync("od.open-solution", solution);
        Assert.True(open.GetProperty("success").GetBoolean(), open.ToString());
        JsonElement tree = default;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            tree = await ide.InvokeAsync("od.solution-tree");
            if (tree.GetProperty("projects").EnumerateArray().Any(p =>
                    p.GetProperty("name").GetString() == "App11" && p.GetProperty("files").GetArrayLength() > 0))
                return;
            await Task.Delay(500);
        }
        Assert.Fail("App11 never loaded with its items: " + tree);
    }

    static int FindLine(string path, string marker)
    {
        int index = Array.FindIndex(File.ReadAllLines(path), line => line.Contains(marker, StringComparison.Ordinal));
        Assert.True(index >= 0, $"Could not find '{marker}' in {path}.");
        return index + 1;
    }

    static string? FindSdk11Root(JsonElement sdks)
    {
        foreach (JsonElement sdk in sdks.EnumerateArray())
        {
            string? root = sdk.GetProperty("RootPath").GetString();
            string sdkDirectory = Path.Combine(root ?? "", "sdk");
            if (root is not null && Directory.Exists(sdkDirectory)
                && Directory.GetDirectories(sdkDirectory).Any(path => Path.GetFileName(path).StartsWith("11.", StringComparison.Ordinal)))
                return root;
        }
        return null;
    }

    sealed class WorkFolder : IDisposable
    {
        // Keep the generated debuggee independent of this repository's Directory.Build.props,
        // Directory.Packages.props and NuGet.config. It is a user project, not a test project.
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opendevelop-net11-" + Guid.NewGuid().ToString("N"));

        public WorkFolder() => Directory.CreateDirectory(Path);

        public string CreateConfigDirectory()
        {
            string config = System.IO.Path.Combine(Path, "config");
            Directory.CreateDirectory(config);
            return config;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a lingering handle; the temp folder is swept eventually */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
