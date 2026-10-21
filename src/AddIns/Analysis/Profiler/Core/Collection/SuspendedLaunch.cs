using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace OpenDevelop.Profiler.Collection;

/// <summary>
/// A .NET process started with its runtime held at startup, so a capture can be attached before any
/// managed code runs and the first moments of the program are not lost. The runtime connects out to a
/// diagnostic port this object listens on (<c>DOTNET_DiagnosticPorts=...,suspend</c>) and waits there until
/// <see cref="Resume"/> is called. Keep the object alive for as long as the capture runs: the port is the
/// connection the capture uses.
/// </summary>
public sealed class SuspendedLaunch : IAsyncDisposable
{
    readonly DiagnosticsClientConnector connector;

    SuspendedLaunch(DiagnosticsClientConnector connector, Process process)
    {
        this.connector = connector;
        Process = process;
    }

    public Process Process { get; }

    /// <summary>A client for the suspended process; pass it to <see cref="CpuCapture.Start(DiagnosticsClient, int, string, CpuCaptureOptions?)"/>.</summary>
    public DiagnosticsClient Client => connector.Instance;

    /// <summary>
    /// Starts the process and waits for its runtime to connect. Fails with an explanation when the program
    /// is not a .NET program, ends early, or uses a runtime that does not support startup suspension, in
    /// which case the process is ended.
    /// </summary>
    public static async Task<SuspendedLaunch> StartAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        var address = CreatePortAddress(OperatingSystem.IsWindows(), Path.GetTempPath());
        startInfo.UseShellExecute = false;
        startInfo.Environment["DOTNET_DiagnosticPorts"] = address + ",suspend";

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var exited = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, exited.Token);

        // Listen before the process exists, so the runtime's first connection attempt finds the port.
        var connecting = DiagnosticsClientConnector.FromDiagnosticPort(address, linked.Token);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new ProfilingException($"Could not start '{startInfo.FileName}'.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or ProfilingException)
        {
            await DiscardAsync(connecting, linked).ConfigureAwait(false);
            throw exception as ProfilingException
                ?? new ProfilingException($"Could not start '{startInfo.FileName}': {exception.Message}", exception);
        }

        // The handler runs on a thread-pool thread whenever the program ends, which for a short-lived program (a
        // test host) can be after this method has returned and disposed the cancellation sources. An exception
        // escaping it there would be unhandled and end the whole IDE, so it must tolerate that and is removed
        // before the sources go away.
        void OnExited(object? sender, EventArgs e)
        {
            try
            {
                exited.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        if (process.HasExited)
            OnExited(process, EventArgs.Empty); // it ended before the handler was attached
        SuspendedLaunch? launch = null;
        try
        {
            launch = new SuspendedLaunch(await connecting.WaitAsync(linked.Token).ConfigureAwait(false), process);
            // The connector hands back a client before the runtime has connected, so ask it something that
            // needs the runtime: a program that is not a .NET program never answers.
            await Task.Run(() => launch.Client.GetProcessEnvironment()).WaitAsync(linked.Token).ConfigureAwait(false);
            return launch;
        }
        catch (Exception exception) when (exception is not ProfilingException)
        {
            var exitedEarly = process.HasExited;
            var exitCode = exitedEarly ? process.ExitCode : 0;
            TryKill(process);
            // Release the diagnostic port: the launch's connector if the runtime had connected, else the pending one.
            if (launch is not null)
                await launch.connector.DisposeAsync().ConfigureAwait(false);
            else
                await DiscardAsync(connecting, linked).ConfigureAwait(false);
            process.Dispose();
            throw new ProfilingException(
                exitedEarly
                    ? $"'{startInfo.FileName}' exited with code {exitCode} before it could be profiled."
                    : exception is OperationCanceledException
                        ? $"'{startInfo.FileName}' did not connect to the profiler within {timeout.TotalSeconds:0} seconds. "
                          + "Only .NET 5 and later applications can be profiled from startup."
                        : $"'{startInfo.FileName}' stopped answering the profiler: {exception.Message}",
                exception);
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    /// <summary>
    /// The diagnostic port address for one launch. Windows takes a pipe name. Elsewhere the port is a Unix domain
    /// socket, whose path is limited to about 100 characters (104 on macOS, 108 on Linux): macOS's per-user temp
    /// folder alone is around 50, so the name is kept short, and a temp folder that is still too long falls back to /tmp.
    /// </summary>
    internal static string CreatePortAddress(bool windows, string tempFolder)
    {
        var name = "odp-" + Guid.NewGuid().ToString("N")[..12];
        if (windows)
            return name;

        // The limit is on bytes, not characters, so a temp folder with non-ASCII characters counts for more.
        const int MaxSocketPathBytes = 100;
        var path = Path.Combine(tempFolder, name + ".sock");
        return System.Text.Encoding.UTF8.GetByteCount(path) <= MaxSocketPathBytes ? path : Path.Combine("/tmp", name + ".sock");
    }

    /// <summary>Lets the held runtime start running the program. Call once the capture is recording.</summary>
    public void Resume()
    {
        try
        {
            Client.ResumeRuntime();
        }
        catch (Exception exception) when (exception is ServerNotAvailableException or IOException or TimeoutException)
        {
            throw new ProfilingException($"Could not resume '{Process.ProcessName}' after attaching the profiler: {exception.Message}", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await connector.DisposeAsync().ConfigureAwait(false);
        Process.Dispose();
    }

    // Cleanup on a path that is already failing: whatever the pending connection did (cancelled, faulted, connected),
    // it must not replace the failure being reported.
    static async Task DiscardAsync(Task<DiagnosticsClientConnector> connecting, CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await (await connecting.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
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
