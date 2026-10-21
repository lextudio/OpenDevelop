using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace OpenDevelop.Profiler.Collection;

/// <summary>A running process that exposes a .NET diagnostic port.</summary>
public sealed record ManagedProcess(int Id, string Name);

/// <summary>Finds and starts the processes a profiling session can attach to.</summary>
public static class ProcessTargets
{
    /// <summary>Lists .NET processes the current user can profile, excluding this one.</summary>
    public static IReadOnlyList<ManagedProcess> ListManagedProcesses()
    {
        var self = Environment.ProcessId;
        var result = new List<ManagedProcess>();
        foreach (var id in DiagnosticsClient.GetPublishedProcesses())
        {
            if (id == self)
                continue;
            try
            {
                using var process = Process.GetProcessById(id);
                result.Add(new ManagedProcess(id, process.ProcessName));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited between listing and lookup (which exception says so differs by platform), or not ours to inspect.
            }
        }
        return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id).ToList();
    }

    /// <summary>
    /// Starts a process and waits until its diagnostic port answers, so a capture can attach. Events
    /// before that point (the first moments of startup) are not part of the capture.
    /// </summary>
    public static Process StartAndWaitForDiagnostics(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new ProfilingException($"Could not start '{startInfo.FileName}'.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProfilingException($"Could not start '{startInfo.FileName}': {exception.Message}", exception);
        }
        var deadline = Stopwatch.StartNew();
        try
        {
            while (deadline.Elapsed < timeout)
            {
                if (process.HasExited)
                    throw new ProfilingException(
                        $"'{startInfo.FileName}' exited with code {process.ExitCode} before it could be profiled.");
                if (DiagnosticsClient.GetPublishedProcesses().Contains(process.Id))
                    return process;
                Thread.Sleep(50);
            }
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }

        TryKill(process);
        process.Dispose();
        throw new ProfilingException(
            $"'{startInfo.FileName}' did not expose a diagnostic port within {timeout.TotalSeconds:0} seconds. "
            + "Only .NET (CoreCLR) applications can be profiled.");
    }

    static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }
}
