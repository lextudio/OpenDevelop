using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Encodings.Web;
using Debugger.AddIn.Service.Dap;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class RemoteDapSessionTests
{
	[Fact]
	public async Task RemoteSession_ReportsUnexpectedConnectionLoss()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var server = Task.Run(async () => {
			using var peer = await listener.AcceptTcpClientAsync();
			using var stream = peer.GetStream();
			using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
			for (int i = 0; i < 2; i++) {
				string header = (await reader.ReadLineAsync())!;
				int length = int.Parse(header["Content-Length:".Length..].Trim());
				await reader.ReadLineAsync();
				var chars = new char[length];
				Assert.Equal(length, await reader.ReadBlockAsync(chars, 0, length));
				var request = JsonNode.Parse(new string(chars))!.AsObject();
				var response = new JsonObject { ["seq"] = i + 1, ["type"] = "response", ["request_seq"] = request["seq"]!.GetValue<int>(), ["success"] = true, ["command"] = request["command"]!.GetValue<string>(), ["body"] = new JsonObject() };
				var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
				await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"));
				await stream.WriteAsync(bytes);
			}
			await close.Task;
		});
		using var session = new DapSession();
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		session.Exited += () => exited.TrySetResult();
		await session.StartRemoteAsync("127.0.0.1", port, null!);
		Assert.True(session.IsRunning);
		close.SetResult();
		await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(session.IsRunning);
		await server;
	}

	[Fact]
	public async Task RemoteSession_ConfiguresBreakpointsAndDisconnectsWithoutTerminatingDebuggee()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		var requests = new List<JsonObject>();
		var server = Task.Run(async () => {
			using var peer = await listener.AcceptTcpClientAsync();
			using var stream = peer.GetStream();
			using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
			while (true) {
				string? header = await reader.ReadLineAsync();
				if (header == null) return;
				int length = int.Parse(header["Content-Length:".Length..].Trim());
				Assert.Equal("", await reader.ReadLineAsync());
				var chars = new char[length];
				Assert.Equal(length, await reader.ReadBlockAsync(chars, 0, length));
				var request = JsonNode.Parse(new string(chars))!.AsObject();
				requests.Add(request);
				string command = request["command"]!.GetValue<string>();
				var response = new JsonObject {
					["seq"] = requests.Count, ["type"] = "response", ["request_seq"] = request["seq"]!.GetValue<int>(),
					["success"] = true, ["command"] = command,
					["body"] = command == "setBreakpoints" ? new JsonObject { ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 12, ["verified"] = true }) } : new JsonObject { ["description"] = "启动" }
				};
				byte[] body = Encoding.UTF8.GetBytes(response.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
				await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
				await stream.WriteAsync(body);
				if (command == "disconnect") return;
			}
		});
		using var session = new DapSession();
		await session.StartRemoteAsync("127.0.0.1", port, new JsonObject { ["sessionToken"] = "token" });
		Assert.True(session.IsRunning);
		var results = await session.SetBreakpointsAsync("/src/MainActivity.cs", new[] { (12, (string)null!, (string)null!, (string)null!) });
		Assert.True(Assert.Single(results).Verified);
		await session.ConfigurationDoneAsync();
		session.Stop();
		await server.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.False(session.IsRunning);
		Assert.Equal(new[] { "initialize", "attach", "setBreakpoints", "configurationDone", "disconnect" }, requests.Select(r => r["command"]!.GetValue<string>()));
		Assert.Equal("token", requests[1]["arguments"]!["sessionToken"]!.GetValue<string>());
		Assert.Equal("/src/MainActivity.cs", requests[2]["arguments"]!["source"]!["path"]!.GetValue<string>());
		Assert.False(requests[4]["arguments"]!["terminateDebuggee"]!.GetValue<bool>());
	}
}
