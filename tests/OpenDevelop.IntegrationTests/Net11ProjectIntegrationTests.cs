using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// A project that targets a framework no stable .NET SDK supports yet: net11.0, while .NET 11 is only
/// a release candidate. Without an SDK 11 the project loads but its build fails with NETSDK1045; with
/// a pre-release SDK 11 (installed by dotnetup, see <see cref="DotnetupSdkProvider"/>) OpenDevelop
/// builds it with that SDK and starts it on that SDK's runtime, while the IDE itself keeps its stable
/// SDK (DotNetSdkService.ResolveSdkForProject).
///
/// Each test starts its own IDE (<see cref="IsolatedIde"/>), so the custom SDK root it registers never
/// reaches the shared app instance.
/// </summary>
[Collection("92 .NET 11 project (isolated IDE)")]
public sealed class Net11ProjectIntegrationTests
{
    [Fact]
    public async Task Net11Project_LoadsButFailsToBuild_WithoutAnSdk11()
    {
        using var work = new WorkFolder();
        string solution = await CreateNet11ConsoleSolutionAsync(work.Path);
        using var ide = await IsolatedIde.StartAsync(work.CreateConfigDirectory());

        // Only meaningful where no SDK 11 of any kind is installed; elsewhere the build would rightly succeed.
        var sdk11 = FindAnySdk11(await ide.InvokeAsync("od.sdk.list"));
        Assert.SkipWhen(sdk11 != null, $"This machine has an SDK 11 ({sdk11}), so 'no SDK 11' cannot be tested here.");

        await OpenAndWaitForItemsAsync(ide, solution);

        var build = await ide.InvokeAsync("od.build-solution", "App11", "rebuild");
        Assert.NotEqual("Success", build.GetProperty("result").GetString());
        Assert.Contains(build.GetProperty("diagnostics").EnumerateArray(), d =>
            d.GetProperty("errorCode").GetString() == "NETSDK1045"
            && (d.GetProperty("errorText").GetString() ?? "").Contains(".NET 11.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Net11Project_BuildsAndRuns_WithADotnetupInstalledPreviewSdk11()
    {
        string sdk11Root = await DotnetupSdkProvider.EnsureSdkAsync("11.0", allowPrerelease: true);
        string sdk11Version = DotnetupSdkProvider.FindCompleteSdkVersion(sdk11Root, "11.0", allowPrerelease: true)!;

        using var work = new WorkFolder();
        string solution = await CreateNet11ConsoleSolutionAsync(work.Path);
        using var ide = await IsolatedIde.StartAsync(work.CreateConfigDirectory());

        var added = await ide.InvokeAsync("od.sdk.add-custom-root", sdk11Root);
        Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());

        // The pre-release SDK is known, but it must not become the IDE's own SDK.
        var effective = (await ide.InvokeAsync("od.sdk.list")).GetProperty("effective");
        Assert.True(effective.ValueKind == JsonValueKind.Object, "The IDE has no effective (stable .NET 10+) SDK.");
        string? effectiveVersion = effective.GetProperty("HighestSdkVersion").GetString();
        Assert.False(effectiveVersion?.Contains('-') ?? true, $"A pre-release SDK ({effectiveVersion}) became the IDE's effective SDK.");

        await OpenAndWaitForItemsAsync(ide, solution);

        var build = await ide.InvokeAsync("od.build-solution", "App11", "rebuild");
        Assert.True(build.GetProperty("result").GetString() == "Success",
            $"net11.0 did not build with the dotnetup SDK {sdk11Version}: {build}");

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
        // both go through the debugger, which launches the program with a host of its own - it must
        // still be the pre-release SDK's root, the only place a .NET 11 runtime exists here.
        var withoutDebugging = await ide.InvokeAsync("od.debug.run-without-debugging", "App11");
        Assert.True(withoutDebugging.GetProperty("success").GetBoolean(), withoutDebugging.ToString());
        await WaitForDebugOutputAsync(ide, "HELLO-NET11 11.", "Start Without Debugging");
        await ide.InvokeAsync("od.debug.stop");

        var debugging = await ide.InvokeAsync("od.debug.start", null!, false, 30);
        Assert.True(debugging.GetProperty("started").GetBoolean(), debugging.ToString());
        await WaitForDebugOutputAsync(ide, "HELLO-NET11 11.", "Start Debugging");
        await ide.InvokeAsync("od.debug.stop");
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

    static async Task<string> CreateNet11ConsoleSolutionAsync(string root)
    {
        string projectDir = Path.Combine(root, "project");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "App11.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n" +
            "    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        await File.WriteAllTextAsync(Path.Combine(projectDir, "Program.cs"),
            "System.Console.WriteLine(\"HELLO-NET11 \" + System.Environment.Version);\n");
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

    /// <summary>"root (version)" of any discovered SDK root holding an 11.x SDK, stable or pre-release, or null.</summary>
    static string? FindAnySdk11(JsonElement sdkList)
    {
        foreach (var sdk in sdkList.GetProperty("sdks").EnumerateArray())
        {
            string? root = sdk.GetProperty("RootPath").GetString();
            string sdkDir = Path.Combine(root ?? "", "sdk");
            if (root == null || !Directory.Exists(sdkDir))
                continue;
            string? version = Directory.GetDirectories(sdkDir).Select(Path.GetFileName)
                .FirstOrDefault(name => name != null && name.StartsWith("11.", StringComparison.Ordinal));
            if (version != null)
                return $"{root} ({version})";
        }
        return null;
    }

    sealed class WorkFolder : IDisposable
    {
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
