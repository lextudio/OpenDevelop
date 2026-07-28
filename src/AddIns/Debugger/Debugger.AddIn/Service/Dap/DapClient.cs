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
		// Requests must be written one at a time (a torn/interleaved Content-Length + body from two
		// concurrent SendRequestAsync callers would corrupt the stream for both), and only one
		// caller may occupy a given sequence-number slot's write-then-await span at once - a plain
		// Interlocked.Increment for the sequence number was not enough on its own once reverse
		// requests (below) started sharing the same writer from the read loop.
		readonly SemaphoreSlim writeLock = new SemaphoreSlim(1, 1);
		readonly SemaphoreSlim requestLock = new SemaphoreSlim(1, 1);
		readonly Action<string> log;
		int sequenceNumber;

		public event Action<string, JsonObject> EventReceived;

		/// <param name="log">Optional sink for a SEND/RECV/error trace of every message - useful when
		/// diagnosing a hung or misbehaving adapter session. No-op by default.</param>
		public DapClient(Stream input, Stream output, Action<string> log = null)
		{
			writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
			reader = new StreamReader(input, new UTF8Encoding(false));
			this.log = log ?? (_ => { });
		}

		public void Start()
		{
			Task.Run(ReadLoopAsync, cancellationTokenSource.Token);
		}

		public async Task<JsonObject> SendRequestAsync(string command, JsonObject arguments = null, CancellationToken cancellationToken = default)
		{
			await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				return await SendRequestCoreAsync(command, arguments, cancellationToken).ConfigureAwait(false);
			} finally {
				requestLock.Release();
			}
		}

		async Task<JsonObject> SendRequestCoreAsync(string command, JsonObject arguments, CancellationToken cancellationToken)
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
			log("SEND " + json);
			byte[] body = Encoding.UTF8.GetBytes(json);
			await writeLock.WaitAsync().ConfigureAwait(false);
			try {
				await writer.WriteAsync("Content-Length: " + body.Length + "\r\n\r\n").ConfigureAwait(false);
				await writer.BaseStream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
				await writer.BaseStream.FlushAsync().ConfigureAwait(false);
			} finally {
				writeLock.Release();
			}
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

					string json = new string(buffer);
					log("RECV " + json);
					Dispatch(json);
				}
			} catch (ObjectDisposedException) {
			} catch (IOException) {
			} catch (OperationCanceledException) {
			} catch (Exception ex) {
				log("READ LOOP ERROR " + ex);
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
			} else if (type == "request") {
				// A "reverse request" - the adapter asking the client (us) to do something, e.g.
				// "runInTerminal". We advertised supportsRunInTerminalRequest=false and don't
				// implement any reverse request's actual body, but an adapter that sends one anyway
				// is left waiting forever for a response unless something replies - acknowledge with
				// an empty success response rather than hanging that side of the adapter.
				_ = RespondToReverseRequestAsync(message);
			}
		}

		async Task RespondToReverseRequestAsync(JsonObject request)
		{
			int requestSequence = request["seq"] != null ? request["seq"].GetValue<int>() : 0;
			string command = request["command"] != null ? request["command"].GetValue<string>() : string.Empty;
			var response = new JsonObject {
				["seq"] = Interlocked.Increment(ref sequenceNumber),
				["type"] = "response",
				["request_seq"] = requestSequence,
				["success"] = true,
				["command"] = command,
				["body"] = new JsonObject()
			};
			await WriteMessageAsync(response).ConfigureAwait(false);
		}

		public void Dispose()
		{
			cancellationTokenSource.Cancel();
			requestLock.Dispose();
			writeLock.Dispose();
			writer.Dispose();
			reader.Dispose();
			cancellationTokenSource.Dispose();
		}
	}
}
