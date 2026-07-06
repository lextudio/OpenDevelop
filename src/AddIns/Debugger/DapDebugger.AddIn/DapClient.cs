using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDevelop.Debugger
{
	sealed class DapClient : IDisposable
	{
		readonly StreamWriter writer;
		readonly StreamReader reader;
		readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JsonObject>>();
		readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
		int sequenceNumber;
		
		public event Action<string, JsonObject> EventReceived;
		
		public DapClient(Stream input, Stream output)
		{
			writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
			reader = new StreamReader(input, new UTF8Encoding(false));
		}
		
		public void Start()
		{
			Task.Run(ReadLoopAsync, cancellationTokenSource.Token);
		}
		
		public async Task<JsonObject> SendRequestAsync(string command, JsonObject arguments = null, CancellationToken cancellationToken = default)
		{
			int sequence = Interlocked.Increment(ref sequenceNumber);
			var completionSource = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
			pending[sequence] = completionSource;
			
			var message = new JsonObject {
				["seq"] = sequence,
				["type"] = "request",
				["command"] = command
			};
			if (arguments != null) {
				message["arguments"] = arguments;
			}
			
			await WriteMessageAsync(message).ConfigureAwait(false);
			
			using (cancellationToken.Register(() => {
				TaskCompletionSource<JsonObject> removed;
				pending.TryRemove(sequence, out removed);
				completionSource.TrySetCanceled();
			})) {
				return await completionSource.Task.ConfigureAwait(false);
			}
		}
		
		async Task WriteMessageAsync(JsonObject message)
		{
			string json = message.ToJsonString();
			byte[] body = Encoding.UTF8.GetBytes(json);
			await writer.WriteAsync("Content-Length: " + body.Length + "\r\n\r\n").ConfigureAwait(false);
			await writer.BaseStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
			await writer.BaseStream.FlushAsync().ConfigureAwait(false);
		}
		
		async Task ReadLoopAsync()
		{
			try {
				while (!cancellationTokenSource.IsCancellationRequested) {
					int contentLength = 0;
					while (true) {
						string line = await reader.ReadLineAsync().ConfigureAwait(false);
						if (line == null) {
							return;
						}
						if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) {
							contentLength = int.Parse(line.Substring("Content-Length:".Length).Trim());
						} else if (line.Length == 0 && contentLength > 0) {
							break;
						}
					}
					
					char[] buffer = new char[contentLength];
					int read = 0;
					while (read < contentLength) {
						int count = await reader.ReadAsync(buffer, read, contentLength - read).ConfigureAwait(false);
						if (count == 0) {
							return;
						}
						read += count;
					}
					
					Dispatch(new string(buffer));
				}
			} catch (ObjectDisposedException) {
			} catch (IOException) {
			} catch (OperationCanceledException) {
			}
		}
		
		void Dispatch(string json)
		{
			JsonObject message;
			try {
				message = JsonNode.Parse(json) as JsonObject;
			} catch (JsonException) {
				return;
			}
			if (message == null) {
				return;
			}
			
			string type = message["type"] != null ? message["type"].GetValue<string>() : null;
			if (type == "response") {
				int requestSequence = message["request_seq"] != null ? message["request_seq"].GetValue<int>() : 0;
				TaskCompletionSource<JsonObject> completionSource;
				if (pending.TryRemove(requestSequence, out completionSource)) {
					completionSource.TrySetResult(message);
				}
			} else if (type == "event") {
				string eventName = message["event"] != null ? message["event"].GetValue<string>() : string.Empty;
				EventReceived?.Invoke(eventName, message["body"] as JsonObject);
			}
		}
		
		public void Dispose()
		{
			cancellationTokenSource.Cancel();
			writer.Dispose();
			reader.Dispose();
			cancellationTokenSource.Dispose();
		}
	}
}
