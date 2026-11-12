using System.Text;
using System.Text.Json;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

/// <summary>The surface's reply to a successful Hello.</summary>
public sealed record SurfaceReady(int Protocol, int MinProtocol, IReadOnlyList<string> Caps, string Wasdk);

/// <summary>A rendered frame. <see cref="DipWidth"/>/<see cref="DipHeight"/> are the display size; the PNG may be larger.</summary>
public sealed record SurfaceFrame(int Width, int Height, int DipWidth, int DipHeight, byte[] Png);

/// <summary>A surface error. <see cref="Line"/>/<see cref="Column"/> are positions in the XAML that was sent.</summary>
public sealed record SurfaceError(string Phase, string Message, int? Line, int? Column, bool NotDesignable);

/// <summary>Exactly one of <see cref="Frame"/> and <see cref="Error"/> is set.</summary>
public sealed record SurfaceRenderResult(SurfaceFrame? Frame, SurfaceError? Error);

/// <summary>The peer cannot speak a shared protocol version, or lacks a capability this client requires.</summary>
public sealed class SurfaceProtocolException : Exception
{
	public SurfaceProtocolException(string message) : base(message) { }
}

/// <summary>The connection can no longer be trusted (closed, timed out or cancelled mid-request).</summary>
public sealed class SurfaceConnectionFaultedException : Exception
{
	public SurfaceConnectionFaultedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// One client connection to a Surface process. The v1 wire has no request or document id, so a reply
/// can only be paired with its request by order. This type therefore allows one request in flight and
/// treats any timeout or cancellation as fatal: a late reply to the abandoned request would otherwise be
/// taken for the answer to the next one and paint a superseded document (the upstream "R1" gap).
/// </summary>
public sealed class SurfaceConnection : IAsyncDisposable
{
	/// <summary>The only wire version this client speaks.</summary>
	public const int ProtocolVersion = 1;
	/// <summary>The capability a frame-based canvas needs; a surface that does not advertise it is refused.</summary>
	public const string FrameStreamCap = "frame-stream";

	readonly Stream stream;
	readonly StreamReader reader;
	readonly StreamWriter writer;
	readonly SemaphoreSlim gate = new(1, 1);
	readonly TimeSpan requestTimeout;
	Exception? fault;

	SurfaceConnection(Stream stream, TimeSpan requestTimeout, SurfaceReady ready, StreamReader reader, StreamWriter writer)
	{
		this.stream = stream;
		this.requestTimeout = requestTimeout;
		this.reader = reader;
		this.writer = writer;
		Ready = ready;
	}

	public SurfaceReady Ready { get; }

	/// <summary>Performs the Hello/Ready exchange. <paramref name="token"/> is the launch token given to the process, if any.</summary>
	public static async Task<SurfaceConnection> ConnectAsync(
		Stream stream, string? token, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		var utf8 = new UTF8Encoding(false);
		var reader = new StreamReader(stream, utf8, false, 1 << 16, leaveOpen: true);
		var writer = new StreamWriter(stream, utf8, 1 << 16, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
		try {
			var hello = new Dictionary<string, object?> {
				["type"] = "Hello",
				["client"] = "opendevelop",
				["caps"] = new[] { FrameStreamCap },
				["protocol"] = ProtocolVersion,
				["minProtocol"] = ProtocolVersion,
			};
			if (token != null)
				hello["token"] = token;
			await writer.WriteLineAsync(JsonSerializer.Serialize(hello)).ConfigureAwait(false);

			while (true) {
				using var doc = await ReadMessageAsync(reader, timeout, cancellationToken).ConfigureAwait(false);
				var root = doc.RootElement;
				switch (GetString(root, "type")) {
					case "Ready":
						var ready = ParseReady(root);
						// Capabilities are advertisements upstream (not enforced), so the client must check them.
						if (!ready.Caps.Contains(FrameStreamCap, StringComparer.Ordinal))
							throw new SurfaceProtocolException("The surface does not advertise '" + FrameStreamCap + "'.");
						if (ready.Protocol < ProtocolVersion || ready.MinProtocol > ProtocolVersion)
							throw new SurfaceProtocolException(
								$"No shared protocol version (surface {ready.MinProtocol}..{ready.Protocol}, client {ProtocolVersion}).");
						return new SurfaceConnection(stream, timeout, ready, reader, writer);
					case "Error":
						throw new SurfaceProtocolException(
							"The surface refused the handshake (" + GetString(root, "phase") + "): " + GetString(root, "message"));
				}
			}
		} catch {
			reader.Dispose();
			await writer.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>Loads a document (the first of a session, or when the viewport changes) and returns its frame or error.</summary>
	public Task<SurfaceRenderResult> LoadXamlAsync(
		string xaml, double width, double height, double scale, bool sampleData, string? documentPath,
		CancellationToken cancellationToken = default)
		=> RequestAsync(new Dictionary<string, object?> {
			["type"] = "LoadXaml",
			["xaml"] = xaml,
			["width"] = width,
			["height"] = height,
			["scale"] = scale,
			["sampleData"] = sampleData,
			["documentPath"] = documentPath,
		}, cancellationToken);

	/// <summary>Replaces the document, keeping the saved viewport.</summary>
	public Task<SurfaceRenderResult> UpdateXamlAsync(
		string xaml, bool sampleData, string? documentPath, CancellationToken cancellationToken = default)
		=> RequestAsync(new Dictionary<string, object?> {
			["type"] = "UpdateXaml",
			["xaml"] = xaml,
			["sampleData"] = sampleData,
			["documentPath"] = documentPath,
		}, cancellationToken);

	async Task<SurfaceRenderResult> RequestAsync(Dictionary<string, object?> message, CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			if (fault != null)
				throw new SurfaceConnectionFaultedException("The surface connection is no longer usable.", fault);
			try {
				await writer.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
				while (true) {
					using var doc = await ReadMessageAsync(reader, requestTimeout, cancellationToken).ConfigureAwait(false);
					var root = doc.RootElement;
					switch (GetString(root, "type")) {
						case "Frame":
							return new SurfaceRenderResult(ParseFrame(root), null);
						case "Error":
							if (GetString(root, "phase") == "protocol")
								throw new SurfaceProtocolException("The surface reported a protocol error: " + GetString(root, "message"));
							return new SurfaceRenderResult(null, ParseError(root));
						// Pong, View, Selected, ElementProps, ContentProps and friends are not replies to a render.
					}
				}
			} catch (Exception ex) when (ex is not SurfaceProtocolException) {
				// A late reply to this request could be mistaken for the answer to the next one.
				fault = ex;
				throw new SurfaceConnectionFaultedException("The surface did not answer; the connection was abandoned.", ex);
			}
		} finally {
			gate.Release();
		}
	}

	static async Task<JsonDocument> ReadMessageAsync(StreamReader reader, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(timeout);
		while (true) {
			string? line;
			try {
				line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
			} catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
				throw new TimeoutException("The surface did not reply within " + timeout + ".");
			}
			if (line == null)
				throw new EndOfStreamException("The surface closed the connection.");
			if (line.Length == 0)
				continue;
			return JsonDocument.Parse(line);
		}
	}

	static string GetString(JsonElement e, string name)
		=> e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

	static int GetInt(JsonElement e, string name, int fallback = 0)
		=> e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : fallback;

	static int? GetNullableInt(JsonElement e, string name)
		=> e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;

	static SurfaceReady ParseReady(JsonElement root)
	{
		var caps = new List<string>();
		if (root.TryGetProperty("caps", out var c) && c.ValueKind == JsonValueKind.Array)
			foreach (var item in c.EnumerateArray())
				if (item.ValueKind == JsonValueKind.String)
					caps.Add(item.GetString()!);
		int protocol = GetInt(root, "protocol", 1);
		// A missing minProtocol means the peer speaks only its own protocol.
		return new SurfaceReady(protocol, GetInt(root, "minProtocol", protocol), caps, GetString(root, "wasdk"));
	}

	static SurfaceFrame ParseFrame(JsonElement root)
	{
		if (GetString(root, "format") is { Length: > 0 } format && format != "png")
			throw new InvalidDataException("Unsupported frame format '" + format + "'.");
		int width = GetInt(root, "width");
		int height = GetInt(root, "height");
		return new SurfaceFrame(width, height, GetInt(root, "dipWidth", width), GetInt(root, "dipHeight", height),
			Convert.FromBase64String(GetString(root, "data")));
	}

	static SurfaceError ParseError(JsonElement root)
		=> new(GetString(root, "phase"), GetString(root, "message"), GetNullableInt(root, "line"), GetNullableInt(root, "column"),
			root.TryGetProperty("notDesignable", out var nd) && nd.ValueKind == JsonValueKind.True);

	public async ValueTask DisposeAsync()
	{
		reader.Dispose();
		await writer.DisposeAsync().ConfigureAwait(false);
		await stream.DisposeAsync().ConfigureAwait(false);
		gate.Dispose();
	}
}
