using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Project.HotReload.Wpf
{
	/// <summary>
	/// Newline-delimited JSON client for the in-process WPF Hot Reload agent.
	/// The agent serves exactly one request per connection and then recreates its listener, so
	/// every call connects afresh - and on Unix, where a .NET named pipe is a socket file under
	/// $TMPDIR, the endpoint briefly does not exist between requests. A connect failure therefore
	/// means "try again", not "the agent is gone", which is why <see cref="SendAsync"/> retries.
	/// </summary>
	sealed class WpfHotReloadPipeClient
	{
		static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		};

		readonly string pipeName;

		public WpfHotReloadPipeClient(string pipeName)
		{
			this.pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
		}

		public async Task<AgentResponse> SendAsync(AgentRequest request, TimeSpan timeout, CancellationToken token)
		{
			var deadline = DateTime.UtcNow + timeout;
			Exception last = null;
			while (DateTime.UtcNow < deadline) {
				token.ThrowIfCancellationRequested();
				try {
					return await SendOnceAsync(request, deadline, token).ConfigureAwait(false);
				} catch (Exception ex) when (ex is not OperationCanceledException) {
					// Everything except cancellation is retried on purpose. The endpoint legitimately
					// comes and goes - the application may not have started yet, and between requests
					// the agent has torn its listener down - and the failure that produces varies by
					// platform (IOException, SocketException, FileNotFoundException on a Unix socket
					// path). Enumerating those types got it wrong once already: an unlisted
					// exception escaped the loop and the session reported "never became ready" after
					// 84 ms, which reads like a dead agent rather than one that was still starting.
					last = ex;
					await Task.Delay(100, token).ConfigureAwait(false);
				}
			}
			throw new TimeoutException(
				$"The WPF Hot Reload agent on pipe '{pipeName}' did not answer within {timeout}"
				+ (last is null ? "." : $"; last error: {last.GetType().Name}: {last.Message}"), last);
		}

		async Task<AgentResponse> SendOnceAsync(AgentRequest request, DateTime deadline, CancellationToken token)
		{
			using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			var remaining = deadline - DateTime.UtcNow;
			if (remaining <= TimeSpan.Zero)
				throw new TimeoutException("No time left to connect to the WPF Hot Reload agent.");
			await pipe.ConnectAsync((int)Math.Min(remaining.TotalMilliseconds, int.MaxValue), token).ConfigureAwait(false);

			var payload = JsonSerializer.Serialize(request, JsonOptions);
			var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
			await writer.WriteLineAsync(payload).ConfigureAwait(false);

			var reader = new StreamReader(pipe, new UTF8Encoding(false));
			var line = await reader.ReadLineAsync().ConfigureAwait(false);
			if (string.IsNullOrEmpty(line))
				throw new IOException("The WPF Hot Reload agent closed the connection without answering.");
			return JsonSerializer.Deserialize<AgentResponse>(line, JsonOptions)
				?? throw new IOException("The WPF Hot Reload agent returned an unreadable response.");
		}

		public async Task<string> QueryAsync(string query, TimeSpan timeout, CancellationToken token)
		{
			var response = await SendAsync(new AgentRequest { Kind = "query", Query = query }, timeout, token)
				.ConfigureAwait(false);
			return response.Value;
		}

		internal sealed class AgentRequest
		{
			public string Kind { get; set; }
			public string Action { get; set; }
			public string FilePath { get; set; }
			public string XamlText { get; set; }
			public string PreviousXamlText { get; set; }
			public string ChangeKind { get; set; }
			public string Query { get; set; }
		}

		internal sealed class AgentResponse
		{
			public string Result { get; set; }
			public string Value { get; set; }

			/// <summary>The agent answers "ok" or "ok: &lt;detail&gt;" on success, "error: ..." otherwise.</summary>
			public bool IsOk => Result != null && Result.StartsWith("ok", StringComparison.OrdinalIgnoreCase);
		}
	}
}
