using Microsoft.Diagnostics.NETCore.Client;

namespace OpenDevelop.Profiler.Collection;

/// <summary>
/// Waits at a diagnostic port address for a .NET program, started elsewhere, to connect to it. This is how a program that
/// is not a local process of this IDE gets profiled: one in a container, one started by a service manager, one reached through a
/// forwarded socket or <c>dotnet-dsrouter</c>. The program is told where to connect with
/// <c>DOTNET_DiagnosticPorts=&lt;address&gt;</c>, with <c>,suspend</c> added to hold it at startup until <see cref="Resume"/>.
/// Keep the object for as long as the capture runs: the connection is what the capture uses.
/// </summary>
public sealed class DiagnosticPortListener : IAsyncDisposable
{
    readonly DiagnosticsClientConnector connector;

    DiagnosticPortListener(DiagnosticsClientConnector connector, string address, IReadOnlyDictionary<string, string> environment)
    {
        this.connector = connector;
        // The diagnostic protocol does not tell a client which process it reached, so the machine is the best name there is.
        DisplayName = environment.TryGetValue("HOSTNAME", out var host) || environment.TryGetValue("COMPUTERNAME", out host)
            ? host
            : address;
    }

    /// <summary>An address that is free to listen at on this machine: a pipe name on Windows, a socket path elsewhere.</summary>
    public static string SuggestAddress() => SuspendedLaunch.CreatePortAddress(OperatingSystem.IsWindows(), Path.GetTempPath());

    /// <summary>What to call the target in the UI: the host it reports, else the address it connected to.</summary>
    public string DisplayName { get; }

    /// <summary>A client for the connected program; pass it to <see cref="CpuCapture.Start(DiagnosticsClient, int, string, CpuCaptureOptions?)"/>.</summary>
    public DiagnosticsClient Client => connector.Instance;

    /// <summary>
    /// Listens on the address and returns once a runtime has connected and answers. Fails if nothing connects within the
    /// timeout, the address cannot be listened on, or the cancellation token fires.
    /// </summary>
    public static async Task<DiagnosticPortListener> ListenAsync(string address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ProfilingException("A diagnostic port address is needed, for example a pipe name or a socket path.");

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);
        DiagnosticsClientConnector? connected = null;
        try
        {
            connected = await DiagnosticsClientConnector.FromDiagnosticPort(address.Trim(), linked.Token).ConfigureAwait(false);
            // The connector hands back a client before the runtime has connected, so ask it something that needs the runtime.
            var environment = await Task.Run(() => connected.Instance.GetProcessEnvironment()).WaitAsync(linked.Token).ConfigureAwait(false);
            return new DiagnosticPortListener(connected, address.Trim(), environment);
        }
        catch (Exception exception) when (exception is not ProfilingException)
        {
            if (connected is not null)
                await connected.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw exception is OperationCanceledException
                ? new ProfilingException(
                    $"No program connected to '{address}' within {timeout.TotalSeconds:0} seconds. "
                    + $"Start it with DOTNET_DiagnosticPorts={address} (add ,suspend to hold it at startup).", exception)
                : new ProfilingException($"Could not listen at '{address}': {exception.Message}", exception);
        }
    }

    /// <summary>Lets a program that was started with <c>,suspend</c> begin running. Call once the capture is recording.</summary>
    public void Resume()
    {
        try
        {
            Client.ResumeRuntime();
        }
        catch (Exception exception) when (exception is ServerNotAvailableException or IOException or TimeoutException)
        {
            throw new ProfilingException($"Could not resume '{DisplayName}' after attaching the profiler: {exception.Message}", exception);
        }
    }

    public ValueTask DisposeAsync() => connector.DisposeAsync();
}
