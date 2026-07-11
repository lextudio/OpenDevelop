// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Debugger.AddIn.Service.Dap
{
	/// <summary>
	/// Minimal Debug Adapter Protocol transport: Content-Length framed JSON over a pair of streams,
	/// with request/response correlation and an event stream.
	/// </summary>
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

			// Defense-in-depth: a DAP request/response is meant to be prompt, but an adapter that
			// doesn't implement a given request simply never replies - awaiting the response then
			// hangs the IDE forever (this is exactly what "modules" did before it was rerouted to
			// events). Cap every request so a missing/slow response surfaces as a TimeoutException
			// instead of an unbreakable freeze.
			using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
				timeoutCts.CancelAfter(RequestTimeout);
				using (timeoutCts.Token.Register(() => {
					TaskCompletionSource<JsonObject> removed;
					pending.TryRemove(sequence, out removed);
					if (cancellationToken.IsCancellationRequested)
						completionSource.TrySetCanceled(cancellationToken);
					else
						completionSource.TrySetException(new TimeoutException(
							"DAP request '" + command + "' timed out after " + RequestTimeout.TotalSeconds + "s (adapter did not respond)."));
				})) {
					return await completionSource.Task.ConfigureAwait(false);
				}
			}
		}

		static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

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
