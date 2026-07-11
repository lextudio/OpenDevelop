using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamJsonRpc;

namespace ICSharpCode.UnitTesting.Mtp
{
	// Client for Microsoft.Testing.Platform's "server mode" JSON-RPC protocol (--server
	// --client-host --client-port), the same integration point Visual Studio/Rider Test Explorer
	// use, instead of a long-lived vstest.console process talked to over its own socket protocol
	// (the VsTestAdapter/VsTestRunAdapter/VsTestDiscoveryAdapter classes this replaces). Modeled
	// directly on the reference client in microsoft/testfx's samples/Playground/ServerMode (same
	// transport: the IDE opens a loopback TcpListener, launches the test host with --server, and
	// the host connects back in). Method names/shapes are taken from testfx's own
	// JsonRpcMethods.cs and Json.TestNodeSerializer.cs, not from the (looser) public docs.
	public sealed class MtpServerProcess : IAsyncDisposable
	{
		readonly Process _process;
		readonly TcpListener _listener;
		readonly TcpClient _tcpClient;
		readonly JsonRpc _rpc;
		readonly List<string> _processOutput;
		readonly ConcurrentDictionary<Guid, PendingRun> _pendingRuns = new();

		// Snapshot of everything the test host printed to stdout/stderr since launch. Useful when
		// initialize/discover/run hangs or the host exits unexpectedly - StreamJsonRpc's own
		// exception rarely explains *why* the host went quiet.
		public IReadOnlyList<string> ProcessOutput => _processOutput;

		// Fires for stdout/stderr lines received after this instance was constructed (i.e. after the
		// host has already connected back). Lines from the initial connect handshake are only in
		// ProcessOutput, not replayed here.
		public event Action<string>? OutputLine;

		MtpServerProcess(Process process, TcpListener listener, TcpClient tcpClient, JsonRpc rpc, List<string> processOutput)
		{
			_process = process;
			_listener = listener;
			_tcpClient = tcpClient;
			_rpc = rpc;
			_processOutput = processOutput;
		}

		public static async Task<MtpServerProcess> StartAsync(string testAssemblyPath, string? workingDirectory, CancellationToken cancellationToken)
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint)listener.LocalEndpoint).Port;

			var psi = new ProcessStartInfo
			{
				FileName = "dotnet",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			if (!string.IsNullOrEmpty(workingDirectory))
				psi.WorkingDirectory = workingDirectory;

			psi.ArgumentList.Add("exec");
			psi.ArgumentList.Add(testAssemblyPath);
			psi.ArgumentList.Add("--server");
			psi.ArgumentList.Add("--client-host");
			psi.ArgumentList.Add("localhost");
			psi.ArgumentList.Add("--client-port");
			psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));

			var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
			var output = new List<string>();
			process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };
			process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Add(e.Data); };

			try {
				process.Start();
			} catch {
				listener.Stop();
				throw;
			}

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			TcpClient tcpClient;
			try {
				using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				acceptCts.CancelAfter(TimeSpan.FromSeconds(30));
				tcpClient = await listener.AcceptTcpClientAsync(acceptCts.Token);
			} catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
				listener.Stop();
				TryKill(process);
				throw new TimeoutException(
					"Timed out waiting for the MTP test host to connect back on the server-mode TCP port. Output:\n"
					+ string.Join('\n', output));
			}

			var stream = tcpClient.GetStream();
			var formatter = new SystemTextJsonFormatter();
			var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
			var rpc = new JsonRpc(handler);

			var serverProcess = new MtpServerProcess(process, listener, tcpClient, rpc, output);
			process.OutputDataReceived += (_, e) => { if (e.Data is not null) serverProcess.OutputLine?.Invoke(e.Data); };
			process.ErrorDataReceived += (_, e) => { if (e.Data is not null) serverProcess.OutputLine?.Invoke(e.Data); };

			rpc.AddLocalRpcMethod("testing/testUpdates/tests", new Action<Guid, JsonElement>(serverProcess.OnTestUpdates));
			rpc.AddLocalRpcMethod("client/log", new Action<JsonElement, JsonElement>((_, _) => { }));
			rpc.AddLocalRpcMethod("telemetry/update", new Action<JsonElement>(_ => { }));
			rpc.StartListening();

			return serverProcess;
		}

		public async Task<MtpServerCapabilities> InitializeAsync(CancellationToken cancellationToken)
		{
			var response = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
				"initialize",
				new {
					processId = Environment.ProcessId,
					clientInfo = new { name = "OpenDevelop", version = "1.0.0" },
					capabilities = new { testing = new { debuggerProvider = false } },
				},
				cancellationToken);

			return MtpServerCapabilities.FromJson(response);
		}

		public Task<IReadOnlyList<MtpTestNode>> DiscoverTestsAsync(CancellationToken cancellationToken)
			=> RunAndCollectAsync("testing/discoverTests", testsFilter: null, cancellationToken);

		public Task<IReadOnlyList<MtpTestNode>> RunTestsAsync(CancellationToken cancellationToken)
			=> RunAndCollectAsync("testing/runTests", testsFilter: null, cancellationToken);

		// Runs only the given tests (nodes from a prior DiscoverTestsAsync call on this same host).
		// The host's own TestNode deserializer requires "display-name" and "node-type" on each filter
		// entry, not just "uid" - a bare {uid} filter throws server-side (KeyNotFoundException on
		// "display-name"), so the full node is echoed back rather than just its identifier.
		public Task<IReadOnlyList<MtpTestNode>> RunTestsAsync(IReadOnlyList<MtpTestNode> tests, CancellationToken cancellationToken)
			=> RunAndCollectAsync("testing/runTests", tests, cancellationToken);

		async Task<IReadOnlyList<MtpTestNode>> RunAndCollectAsync(string method, IReadOnlyList<MtpTestNode>? testsFilter, CancellationToken cancellationToken)
		{
			var runId = Guid.NewGuid();
			var pending = new PendingRun();
			if (!_pendingRuns.TryAdd(runId, pending))
				throw new InvalidOperationException("Duplicate MTP run id (should not happen with a fresh Guid).");

			try {
				// "Run/discover everything" is expressed by omitting the "tests" property entirely,
				// not by sending it as an explicit JSON null: MSTest's own MTP host binds "tests" by
				// unconditionally calling JsonElement.EnumerateArray() and throws
				// InvalidOperationException on a null value instead of treating it as "no filter".
				object parameters = testsFilter is { Count: > 0 }
					? new { runId, tests = testsFilter.Select(node => node.ToFilterPayload()).ToArray() }
					: new { runId };

				await _rpc.InvokeWithParameterObjectAsync(method, parameters, cancellationToken: cancellationToken);
				await pending.Completed.Task.WaitAsync(cancellationToken);
			} finally {
				_pendingRuns.TryRemove(runId, out _);
			}

			return pending.Nodes.Values.ToList();
		}

		void OnTestUpdates(Guid runId, JsonElement changes)
		{
			if (!_pendingRuns.TryGetValue(runId, out var pending))
				return;

			if (changes.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
				pending.Completed.TrySetResult();
				return;
			}

			if (changes.ValueKind != JsonValueKind.Array)
				return;

			// Each state transition (discovered -> in-progress -> passed/failed/...) for the same
			// test arrives as its own "testing/testUpdates/tests" notification carrying the same
			// uid, so callers only care about the latest state per uid, not every transition.
			foreach (var change in changes.EnumerateArray()) {
				if (change.TryGetProperty("node", out var node))
					pending.Nodes[MtpTestNode.FromJson(node).Uid] = MtpTestNode.FromJson(node);
			}
		}

		public async Task ExitAsync()
		{
			try {
				await _rpc.NotifyWithParameterObjectAsync("exit", new object());
			} catch {
				// Best-effort: the host may have already disconnected.
			}
		}

		public async ValueTask DisposeAsync()
		{
			await ExitAsync();
			_rpc.Dispose();
			_tcpClient.Dispose();
			_listener.Stop();

			try {
				if (!_process.HasExited)
					await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
			} catch (OperationCanceledException) {
				TryKill(_process);
			}

			_process.Dispose();
		}

		static void TryKill(Process process)
		{
			try { process.Kill(entireProcessTree: true); } catch { }
		}

		sealed class PendingRun
		{
			public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

			// Keyed by uid so repeated state transitions for the same test (discovered -> in-progress
			// -> passed) collapse to the latest state instead of appearing as duplicate entries.
			public Dictionary<string, MtpTestNode> Nodes { get; } = new();
		}
	}
}
