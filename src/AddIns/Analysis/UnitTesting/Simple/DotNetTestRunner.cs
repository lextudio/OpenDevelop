using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.UnitTesting.Mtp;

namespace ICSharpCode.UnitTesting.Simple;

// A discovered MTP test, before it's wrapped into a full TestInfo (which needs project/solution
// context this class doesn't have). Uid is the identifier RunTestsAsync must use to run just
// this test - MTP has no separate "fully qualified name" concept. TypeFullName/MethodName/
// ParameterCount are the declaring type/method (for "double-click to open the test's source",
// resolved later via Roslyn since the test host doesn't report file/line) - null when the host
// doesn't supply location.* at all (NUnit's current MTP bridge never does).
internal sealed record MtpDiscoveredTest(
    string Uid,
    string DisplayName,
    string? TypeFullName,
    string? MethodName,
    int? ParameterCount);

// Talks to Microsoft.Testing.Platform's server-mode JSON-RPC protocol (via MtpServerProcess) to
// discover and run tests, instead of scraping `dotnet run -- --list-tests`/TRX console output.
// That console-based approach used to live here; it was fragile (silently swallowed a project's
// tests, or produced fake ones from banner/summary lines the parser didn't know to ignore - see
// DotNetTestListParserTests' regression case) and MTP's own docs point IDE integrations at the
// server-mode protocol instead. See samples/Playground/ServerMode in microsoft/testfx for the
// reference client this is modeled on.
internal sealed class DotNetTestRunner
{
    public event Action<string>? OutputLine;

    public async Task<IReadOnlyList<MtpDiscoveredTest>> ListTestsAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken)
    {
        var assemblyPath = await BuildAndResolveAssemblyAsync(projectPath, targetFramework, cancellationToken);
        if (assemblyPath is null)
            return Array.Empty<MtpDiscoveredTest>();

        await using var server = await StartServerAsync(assemblyPath, cancellationToken);
        if (server is null)
            return Array.Empty<MtpDiscoveredTest>();

        await server.InitializeAsync(cancellationToken);
        var nodes = await server.DiscoverTestsAsync(cancellationToken);

        return nodes
            .Where(node => node.NodeType == "action")
            .Select(node => new MtpDiscoveredTest(node.Uid, node.DisplayName, node.LocationType, node.LocationMethodName, node.LocationMethodParameterCount))
            .ToList();
    }

    public async Task<IReadOnlyList<TestResultInfo>> RunTestsAsync(
        string projectPath,
        string? targetFramework,
        IReadOnlyList<string> testUids,
        CancellationToken cancellationToken)
    {
        var assemblyPath = await BuildAndResolveAssemblyAsync(projectPath, targetFramework, cancellationToken);
        if (assemblyPath is null)
            return Array.Empty<TestResultInfo>();

        await using var server = await StartServerAsync(assemblyPath, cancellationToken);
        if (server is null)
            return Array.Empty<TestResultInfo>();

        await server.InitializeAsync(cancellationToken);

        IReadOnlyList<MtpTestNode> nodes;
        if (testUids.Count > 0)
        {
            // The host's run-filter deserializer needs the full node (uid + display-name +
            // node-type), not just a uid - re-discover on this same live host instance right
            // before running so the filter nodes are guaranteed consistent with it, rather than
            // reusing possibly-stale nodes from an earlier discovery call/process.
            var discovered = await server.DiscoverTestsAsync(cancellationToken);
            var uidSet = new HashSet<string>(testUids, StringComparer.Ordinal);
            var filter = discovered.Where(node => uidSet.Contains(node.Uid)).ToList();
            nodes = filter.Count > 0
                ? await server.RunTestsAsync(filter, cancellationToken)
                : Array.Empty<MtpTestNode>();
        }
        else
        {
            nodes = await server.RunTestsAsync(cancellationToken);
        }

        return nodes
            .Where(node => node.NodeType == "action")
            .Select(node => new TestResultInfo(node.DisplayName, ToResultType(node.ExecutionState), node.ErrorMessage, StackTrace: null))
            .ToList();
    }

    private async Task<MtpServerProcess?> StartServerAsync(string assemblyPath, CancellationToken cancellationToken)
    {
        try
        {
            var server = await MtpServerProcess.StartAsync(assemblyPath, Path.GetDirectoryName(assemblyPath), cancellationToken);
            server.OutputLine += line => OutputLine?.Invoke(line);
            return server;
        }
        catch (TimeoutException)
        {
            // MtpServerProcess.StartAsync's own 30s AcceptTcpClientAsync timeout: the launched
            // process never connected back over the server-mode TCP port at all - the single
            // most common cause is that this test project doesn't actually speak
            // Microsoft.Testing.Platform's server-mode protocol (still classic VSTest:
            // Microsoft.NET.Test.Sdk + xunit.runner.visualstudio / NUnit3TestAdapter /
            // MSTest.TestAdapter, with no UseMicrosoftTestingPlatformRunner / EnableMSTestRunner /
            // EnableNUnitRunner), not a slow/hung host - worth calling out explicitly rather than
            // reporting a generic "0 tests found" with no actionable explanation.
            OutputLine?.Invoke(
                "> WARNING: " + Path.GetFileNameWithoutExtension(assemblyPath) + " did not connect back as an "
                + "MTP server-mode test host within 30s. UnoDevelop's test runner only supports "
                + "Microsoft.Testing.Platform (MTP) - if this project still uses classic VSTest "
                + "(Microsoft.NET.Test.Sdk + xunit.runner.visualstudio / NUnit3TestAdapter / "
                + "MSTest.TestAdapter), it will never be discovered or run. Upgrade to xunit.v3 "
                + "(UseMicrosoftTestingPlatformRunner), MSTest (EnableMSTestRunner), or NUnit "
                + "(EnableNUnitRunner) to enable test discovery and running.");
            return null;
        }
        catch (Exception ex)
        {
            OutputLine?.Invoke("> Failed to start MTP server-mode host for " + assemblyPath + ": " + ex.Message);
            return null;
        }
    }

    private async Task<string?> BuildAndResolveAssemblyAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken)
    {
        projectPath = Path.GetFullPath(projectPath);
        var workingDirectory = Path.GetDirectoryName(projectPath);

        var args = new List<string> { "build", projectPath, "-tl:off" };
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            args.Add("-f");
            args.Add(targetFramework);
        }

        var build = await RunProcessAsync("dotnet", args, cancellationToken, workingDirectory);
        if (build.ExitCode != 0)
        {
            OutputLine?.Invoke("> Build failed for " + projectPath);
            return null;
        }

        var assemblyPath = ResolveOutputAssembly(projectPath, targetFramework);
        if (assemblyPath is null)
            OutputLine?.Invoke("> Could not locate the built test assembly for " + projectPath);

        return assemblyPath;
    }

    private static string? ResolveOutputAssembly(string projectPath, string? targetFramework)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(projectDirectory))
            return null;

        var binDirectory = Path.Combine(projectDirectory, "bin");
        if (!Directory.Exists(binDirectory))
            return null;

        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        IEnumerable<string> candidates = Directory.EnumerateFiles(binDirectory, projectName + ".dll", SearchOption.AllDirectories);

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            var matchingTargetFramework = candidates
                .Where(path => path.Split(Path.DirectorySeparatorChar).Contains(targetFramework, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (matchingTargetFramework.Count > 0)
                candidates = matchingTargetFramework;
        }

        return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static TestResultType ToResultType(string? executionState) => executionState switch
    {
        "passed" => TestResultType.Passing,
        "failed" or "timed-out" or "error" or "canceled" => TestResultType.Failing,
        "skipped" => TestResultType.Skipped,
        "in-progress" or "discovered" => TestResultType.Running,
        _ => TestResultType.None,
    };

    private async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        OutputLine?.Invoke("> " + FormatCommandLine(fileName, arguments));

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        AppendOutput(stdout);
        AppendOutput(stderr);
        OutputLine?.Invoke($"> dotnet exited with code {process.ExitCode}.");
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private void AppendOutput(string output)
    {
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            OutputLine?.Invoke(rawLine);
    }

    private static string FormatCommandLine(string fileName, IReadOnlyList<string> arguments)
        => fileName + " " + string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
        => argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal)
            ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : argument;

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
