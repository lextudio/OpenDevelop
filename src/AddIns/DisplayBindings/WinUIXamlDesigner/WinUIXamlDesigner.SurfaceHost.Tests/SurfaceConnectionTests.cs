using System.Text.Json;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

public class SurfaceConnectionTests
{
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
	static readonly byte[] Png = FakeSurface.Png;

	static CancellationToken Ct => TestContext.Current.CancellationToken;

	static string Type(JsonElement m) => FakeSurface.Type(m);
	static Task Ready(StreamWriter w, string caps = "\"frame-stream\",\"native-hwnd\"", int protocol = 1, int? min = 1)
		=> FakeSurface.Ready(w, caps, protocol, min);
	static Task Frame(StreamWriter w) => FakeSurface.Frame(w);

	[Fact]
	public async Task Handshake_sends_token_and_reads_ready()
	{
		await using var surface = new FakeSurface((m, w) => Type(m) == "Hello" ? Ready(w) : Task.CompletedTask);
		await using var conn = await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), "abc123", Timeout, Ct);

		Assert.Equal("2.4.0", conn.Ready.Wasdk);
		var hello = surface.Received[0].RootElement;
		Assert.Equal("abc123", hello.GetProperty("token").GetString());
		Assert.Equal(1, hello.GetProperty("protocol").GetInt32());
	}

	[Fact]
	public async Task Surface_without_frame_stream_is_refused()
	{
		await using var surface = new FakeSurface((m, w) => Ready(w, caps: "\"native-hwnd\""));
		await Assert.ThrowsAsync<SurfaceProtocolException>(
			async () => await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct));
	}

	[Fact]
	public async Task Protocol_error_instead_of_ready_is_refused()
	{
		await using var surface = new FakeSurface((m, w)
			=> w.WriteLineAsync("{\"type\":\"Error\",\"phase\":\"protocol\",\"message\":\"no shared version\"}"));
		var ex = await Assert.ThrowsAsync<SurfaceProtocolException>(
			async () => await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct));
		Assert.Contains("no shared version", ex.Message);
	}

	[Fact]
	public async Task Surface_that_needs_a_newer_protocol_is_refused()
	{
		await using var surface = new FakeSurface((m, w) => Ready(w, protocol: 3, min: 2));
		await Assert.ThrowsAsync<SurfaceProtocolException>(
			async () => await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct));
	}

	[Fact]
	public async Task LoadXaml_returns_the_frame_and_skips_unrelated_messages()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			switch (Type(m)) {
				case "Hello": await Ready(w); break;
				case "LoadXaml":
					await w.WriteLineAsync("{\"type\":\"Pong\"}");
					await w.WriteLineAsync("{\"type\":\"View\",\"fit\":true,\"zoom\":1,\"offsetX\":0,\"offsetY\":0}");
					await Frame(w);
					break;
			}
		});
		await using var conn = await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct);

		var result = await conn.LoadXamlAsync("<Page/>", 480, 320, 1.0, false, null, Ct);

		Assert.Null(result.Error);
		Assert.Equal(Png, result.Frame!.Png);
		Assert.Equal(512, result.Frame.DipWidth);
		Assert.Equal(1024, result.Frame.Width);
		var load = surface.Received.Last().RootElement;
		Assert.Equal("<Page/>", load.GetProperty("xaml").GetString());
		Assert.Equal(JsonValueKind.Null, load.GetProperty("documentPath").ValueKind);
		Assert.False(load.GetProperty("sampleData").GetBoolean());
	}

	[Fact]
	public async Task Render_error_carries_the_position_and_keeps_the_connection_usable()
	{
		int loads = 0;
		await using var surface = new FakeSurface(async (m, w) => {
			if (Type(m) == "Hello") { await Ready(w); return; }
			if (loads++ == 0)
				await w.WriteLineAsync("{\"type\":\"Error\",\"phase\":\"parse\",\"message\":\"bad\",\"line\":3,\"column\":7}");
			else
				await Frame(w);
		});
		await using var conn = await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct);

		var bad = await conn.LoadXamlAsync("<Page", 1, 1, 1, true, null, Ct);
		Assert.Null(bad.Frame);
		Assert.Equal(("parse", 3, 7), (bad.Error!.Phase, bad.Error.Line, bad.Error.Column));

		var good = await conn.UpdateXamlAsync("<Page/>", true, null, Ct);
		Assert.NotNull(good.Frame);
	}

	[Fact]
	public async Task A_timeout_faults_the_connection_so_a_late_frame_cannot_answer_the_next_request()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			if (Type(m) == "Hello") await Ready(w);
			// LoadXaml is never answered in time.
		});
		await using var conn = await SurfaceConnection.ConnectAsync(
			await surface.ConnectAsync(), null, TimeSpan.FromMilliseconds(200), Ct);

		await Assert.ThrowsAsync<SurfaceConnectionFaultedException>(
			() => conn.LoadXamlAsync("<Page/>", 1, 1, 1, true, null, Ct));
		// The second request must be refused outright, not sent and paired with a stale reply.
		int sent = surface.Received.Count;
		await Assert.ThrowsAsync<SurfaceConnectionFaultedException>(
			() => conn.UpdateXamlAsync("<Page/>", true, null, Ct));
		Assert.Equal(sent, surface.Received.Count);
	}

	[Fact]
	public async Task Disposing_a_connection_twice_is_allowed()
	{
		await using var surface = new FakeSurface((m, w) => Type(m) == "Hello" ? Ready(w) : Task.CompletedTask);
		var conn = await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct);

		await conn.DisposeAsync();
		await conn.DisposeAsync();   // must not throw: a document disposed by its owner and again by a caller is normal
	}

	[Fact]
	public async Task Closed_connection_is_reported_as_faulted()
	{
		await using var surface = new FakeSurface(async (m, w) => {
			if (Type(m) == "Hello") await Ready(w);
		});
		await using var conn = await SurfaceConnection.ConnectAsync(await surface.ConnectAsync(), null, Timeout, Ct);
		await surface.DisposeAsync();

		await Assert.ThrowsAsync<SurfaceConnectionFaultedException>(
			() => conn.LoadXamlAsync("<Page/>", 1, 1, 1, true, null, Ct));
	}
}
