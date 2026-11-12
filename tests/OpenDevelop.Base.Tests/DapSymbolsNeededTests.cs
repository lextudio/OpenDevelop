using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Debugger.AddIn.Service.Dap;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// doc/technotes/decompiled-debugging.md: the adapter (started with decompileExternalCode=false) raises <c>symbolsNeeded</c> and
/// WAITS for the IDE's <c>loadSymbols</c>. The IDE must always answer, and the answer must not queue behind the request that waits.
/// </summary>
public class DapSymbolsNeededTests
{
	static readonly Guid Mvid = Guid.Parse("bfb6f089-0fb3-493b-b5e2-d6d8d7df8a93");

	sealed class FakeAdapter : IDisposable
	{
		readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
		public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
		public readonly List<JsonObject> Requests = new List<JsonObject>();
		public Task Run { get; private set; } = Task.CompletedTask;

		public FakeAdapter() { listener.Start(); }
		public void Dispose() => listener.Stop();

		/// <summary>Serves requests; <paramref name="onRequest"/> may write events and returns false to leave the response to later.</summary>
		public void Serve(Func<JsonObject, Stream, bool> onRequest)
		{
			Run = Task.Run(async () => {
				using var peer = await listener.AcceptTcpClientAsync();
				using var stream = peer.GetStream();
				using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
				while (true) {
					string? header = await reader.ReadLineAsync();
					if (header == null) return;
					int length = int.Parse(header["Content-Length:".Length..].Trim());
					await reader.ReadLineAsync();
					var chars = new char[length];
					await reader.ReadBlockAsync(chars, 0, length);
					var request = JsonNode.Parse(new string(chars))!.AsObject();
					lock (Requests) Requests.Add(request);
					if (onRequest(request, stream))
						Respond(stream, request, new JsonObject());
				}
			});
		}

		public static void Respond(Stream stream, JsonObject request, JsonObject body) =>
			Write(stream, new JsonObject {
				["seq"] = 1000, ["type"] = "response", ["request_seq"] = request["seq"]!.GetValue<int>(),
				["success"] = true, ["command"] = request["command"]!.GetValue<string>(), ["body"] = body
			});

		public static void SendSymbolsNeeded(Stream stream) =>
			Write(stream, new JsonObject {
				["seq"] = 1001, ["type"] = "event", ["event"] = "symbolsNeeded",
				["body"] = new JsonObject { ["moduleName"] = "System.Linq.dll", ["modulePath"] = "/usr/share/System.Linq.dll", ["mvid"] = Mvid.ToString() }
			});

		static readonly object writeLock = new object();

		static void Write(Stream stream, JsonObject message)
		{
			byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
			lock (writeLock) {
				stream.Write(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
				stream.Write(body);
			}
		}

		public JsonObject? Find(string command) { lock (Requests) return Requests.FirstOrDefault(r => r["command"]!.GetValue<string>() == command); }
	}

	[Fact]
	public async Task SymbolsNeeded_WithNobodyToAnswer_IsAnsweredAtOnceWithNoSymbols()
	{
		using var adapter = new FakeAdapter();
		adapter.Serve((request, stream) => {
			// The event follows the attach response (the session is running by then)
			if (request["command"]!.GetValue<string>() == "attach") {
				FakeAdapter.Respond(stream, request, new JsonObject());
				FakeAdapter.SendSymbolsNeeded(stream);
				return false;
			}
			return true;
		});
		using var session = new DapSession();
		await session.StartRemoteAsync("127.0.0.1", adapter.Port, new JsonObject { ["sessionToken"] = "t" });

		// No SymbolsNeeded subscriber (the decompiler add-in is disabled): the adapter must not be left waiting for its timeout
		var answered = false;
		for (int i = 0; i < 100 && !(answered = adapter.Find("loadSymbols") != null); i++)
			await Task.Delay(50, TestContext.Current.CancellationToken);
		Assert.True(answered, "no loadSymbols answer was sent");
		var arguments = adapter.Find("loadSymbols")!["arguments"]!.AsObject();
		Assert.Equal(Mvid.ToString(), arguments["mvid"]!.GetValue<string>());
		Assert.False(arguments.ContainsKey("pdbPath"), "an answer without symbols names no PDB");
		session.Stop();
	}

	[Fact]
	public async Task TheAnswer_DoesNotQueueBehindTheRequestThatWaitsForIt()
	{
		using var adapter = new FakeAdapter();
		JsonObject? pendingResolve = null;
		adapter.Serve((request, stream) => {
			switch (request["command"]!.GetValue<string>()) {
				case "resolveStackFrame":
					// Like the adapter: ask for symbols and hold this request until they have been supplied
					pendingResolve = request;
					FakeAdapter.SendSymbolsNeeded(stream);
					return false;
				case "loadSymbols":
					FakeAdapter.Respond(stream, request, new JsonObject { ["accepted"] = true });
					FakeAdapter.Respond(stream, pendingResolve!, new JsonObject {
						["stackFrame"] = new JsonObject { ["id"] = 2, ["name"] = "resolved", ["line"] = 7, ["column"] = 1, ["source"] = new JsonObject { ["path"] = "/cache/Monitor.cs" } }
					});
					return false;
				default:
					return true;
			}
		});
		using var session = new DapSession();
		await session.StartRemoteAsync("127.0.0.1", adapter.Port, new JsonObject { ["sessionToken"] = "t" });
		session.SymbolsNeeded += (s, e) => _ = s.LoadSymbolsAsync(e.Mvid, "/cache/System.Linq.pdb");

		// If loadSymbols waited for resolveStackFrame's request lock, neither would ever complete
		var frame = await session.ResolveStackFrameAsync(new DapStackFrameInfo { Id = 2, ThreadId = 1 })
			.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

		Assert.Equal("/cache/Monitor.cs", frame.FilePath);
		var load = adapter.Find("loadSymbols")!["arguments"]!.AsObject();
		Assert.Equal("/cache/System.Linq.pdb", load["pdbPath"]!.GetValue<string>());
		Assert.Equal(Mvid.ToString(), load["mvid"]!.GetValue<string>());
		session.Stop();
	}
}
