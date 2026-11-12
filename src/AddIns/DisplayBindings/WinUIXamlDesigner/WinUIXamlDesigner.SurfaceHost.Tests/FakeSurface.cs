using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

/// <summary>One-shot fake surface: <c>script</c> gets each received message and the writer.</summary>
internal sealed class FakeSurface : IAsyncDisposable
{
	public static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };

	readonly TcpListener listener = new(IPAddress.Loopback, 0);
	readonly Task serve;
	public List<JsonDocument> Received { get; } = new();

	public FakeSurface(Func<JsonElement, StreamWriter, Task> script)
	{
		listener.Start();
		serve = Task.Run(async () => {
			using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
			using var stream = client.GetStream();
			var utf8 = new UTF8Encoding(false);
			using var reader = new StreamReader(stream, utf8);
			using var writer = new StreamWriter(stream, utf8) { NewLine = "\n", AutoFlush = true };
			string? line;
			while ((line = await reader.ReadLineAsync(TestContext.Current.CancellationToken)) != null) {
				var doc = JsonDocument.Parse(line);
				Received.Add(doc);
				await script(doc.RootElement, writer);
			}
		});
	}

	public async Task<Stream> ConnectAsync()
	{
		var client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
		return client.GetStream();
	}

	public async ValueTask DisposeAsync()
	{
		listener.Stop();
		try { await serve.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); } catch { }
	}

	public static string Type(JsonElement m) => m.GetProperty("type").GetString()!;

	public static Task Ready(StreamWriter w, string caps = "\"frame-stream\",\"native-hwnd\"", int protocol = 1, int? min = 1)
		=> w.WriteLineAsync("{\"type\":\"Ready\",\"protocol\":" + protocol
			+ (min is int m ? ",\"minProtocol\":" + m : "") + ",\"caps\":[" + caps + "],\"wasdk\":\"2.4.0\"}");

	public static Task Frame(StreamWriter w, int width = 1024, int height = 720, int dipWidth = 512, int dipHeight = 360)
		=> w.WriteLineAsync("{\"type\":\"Frame\",\"format\":\"png\",\"width\":" + width + ",\"height\":" + height
			+ ",\"dipWidth\":" + dipWidth + ",\"dipHeight\":" + dipHeight + ",\"data\":\"" + Convert.ToBase64String(Png) + "\"}");
}
