using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

/// <summary>
/// A running upstream Surface process and its authenticated connection. The surface accepts exactly one
/// client and ends when it disconnects, so one process serves one document. Disposing closes the
/// connection and kills the process tree: a surface must never outlive the document that asked for it.
/// </summary>
public sealed class SurfaceProcessHost : IAsyncDisposable
{
	readonly Process process;
	readonly StringBuilder log;

	SurfaceProcessHost(Process process, SurfaceConnection connection, StringBuilder log)
	{
		this.process = process;
		Connection = connection;
		this.log = log;
	}

	public SurfaceConnection Connection { get; }

	/// <summary>The tail of the surface's stderr, for diagnostics.</summary>
	public string Log { get { lock (log) return log.ToString(); } }

	public bool IsAlive => !process.HasExited;

	/// <summary>
	/// Launches <paramref name="surfaceExe"/> with <paramref name="arguments"/> (the matched host's
	/// <see cref="SurfaceLaunch.MatchedHostArguments"/>, or empty for the bundled host) and connects.
	/// </summary>
	public static async Task<SurfaceProcessHost> StartAsync(
		string surfaceExe, string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		string directory = Path.GetDirectoryName(Path.GetFullPath(surfaceExe))!;
		SurfaceLaunch.EnsureShortRoot(directory);

		string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
		var psi = new ProcessStartInfo(surfaceExe, arguments) {
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
			WorkingDirectory = directory,
		};
		// The surface reads and clears this at startup; without it any local process could claim the connection.
		psi.Environment["SURFACE_AUTH_TOKEN"] = token;
		var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + surfaceExe);
		var log = new StringBuilder();
		process.ErrorDataReceived += (_, e) => {
			if (e.Data == null) return;
			lock (log) {
				log.AppendLine(e.Data);
				if (log.Length > 16 * 1024)
					log.Remove(0, log.Length - 12 * 1024);
			}
		};
		process.BeginErrorReadLine();

		try {
			using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutSource.CancelAfter(timeout);
			int? port = null;
			while (port == null) {
				string? line = await process.StandardOutput.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
				if (line == null) {
					// stdout closed: the process is going away, but its exit code may not be available for a moment.
					try {
						await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
					} catch (OperationCanceledException) { }
					int? exitCode = process.HasExited ? process.ExitCode : null;
					string hint = exitCode is int code ? SurfaceLaunch.DescribeStartupExit(code) : "";
					throw new InvalidOperationException("The surface exited before announcing a port (exit "
						+ (exitCode?.ToString() ?? "unknown") + ")." + (hint.Length > 0 ? " " + hint : ""));
				}
				port = SurfaceLaunch.TryParsePort(line);
			}

			var tcp = new TcpClient();
			try {
				await tcp.ConnectAsync("127.0.0.1", port.Value, timeoutSource.Token).ConfigureAwait(false);
				var connection = await SurfaceConnection.ConnectAsync(tcp.GetStream(), token, timeout, timeoutSource.Token).ConfigureAwait(false);
				return new SurfaceProcessHost(process, connection, log);
			} catch {
				tcp.Dispose();
				throw;
			}
		} catch {
			Kill(process);
			process.Dispose();
			throw;
		}
	}

	static void Kill(Process process)
	{
		try {
			if (!process.HasExited)
				process.Kill(true);
		} catch (InvalidOperationException) {
			// already gone
		}
	}

	public async ValueTask DisposeAsync()
	{
		await Connection.DisposeAsync().ConfigureAwait(false);
		Kill(process);
		// Kill returns before the OS has released the process's file handles; a caller that deletes the surface's
		// directory next (the child removes each document's run copy) must wait for the exit.
		try {
			await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			// give up waiting; the caller's delete retries
		}
		process.Dispose();
	}
}
