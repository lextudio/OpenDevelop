using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

/// <summary>The surface's reply to a successful Hello.</summary>
public sealed record SurfaceReady(int Protocol, int MinProtocol, IReadOnlyList<string> Caps, string Wasdk);

/// <summary>A rendered frame. <see cref="DipWidth"/>/<see cref="DipHeight"/> are the display size; the PNG may be larger.</summary>
public sealed record SurfaceFrame(int Width, int Height, int DipWidth, int DipHeight, byte[] Png);

/// <summary>A surface error. <see cref="Line"/>/<see cref="Column"/> are positions in the XAML that was sent.</summary>
public sealed record SurfaceError(string Phase, string Message, int? Line, int? Column, bool NotDesignable);

/// <summary>Exactly one of <see cref="Frame"/> and <see cref="Error"/> is set.</summary>
public sealed record SurfaceRenderResult(SurfaceFrame? Frame, SurfaceError? Error);

/// <summary>
/// The element a pick landed on. <see cref="Name"/> is its x:Name when it has one; <see cref="Path"/> is the surface's own tree path
/// and may be unresolved. The bounds are in the artboard's DIPs.
/// </summary>
public sealed record SurfacePick(int Id, string ElementType, string? Name, string? Path, double X, double Y, double Width, double Height);

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
/// treats any timeout or cancellation of a render request as fatal: a late reply to the abandoned request would otherwise be
/// taken for the answer to the next one and paint a superseded document (the upstream "R1" gap).
///
/// One background task owns the socket and turns lines into messages on a channel. A caller waits on that channel with a
/// timeout, so abandoning a wait never abandons a half-read line. That is what lets a <em>pick</em> that hits nothing, which the
/// surface answers with silence, end as an ordinary miss instead of a fault.
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
	readonly Channel<JsonDocument> messages = Channel.CreateUnbounded<JsonDocument>(new UnboundedChannelOptions { SingleWriter = true });
	readonly CancellationTokenSource stopReading = new();
	readonly Task readLoop;
	Exception? fault;

	SurfaceConnection(Stream stream, TimeSpan requestTimeout, StreamReader reader, StreamWriter writer)
	{
		this.stream = stream;
		this.requestTimeout = requestTimeout;
		this.reader = reader;
		this.writer = writer;
		readLoop = Task.Run(ReadLoopAsync);
	}

	public SurfaceReady Ready { get; private set; } = new(0, 0, Array.Empty<string>(), "");

	/// <summary>Performs the Hello/Ready exchange. <paramref name="token"/> is the launch token given to the process, if any.</summary>
	public static async Task<SurfaceConnection> ConnectAsync(
		Stream stream, string? token, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		var utf8 = new UTF8Encoding(false);
		var reader = new StreamReader(stream, utf8, false, 1 << 16, leaveOpen: true);
		var writer = new StreamWriter(stream, utf8, 1 << 16, leaveOpen: true) { NewLine = "\n", AutoFlush = true };
		var connection = new SurfaceConnection(stream, timeout, reader, writer);
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
				using var doc = await connection.NextAsync(timeout, cancellationToken).ConfigureAwait(false);
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
						connection.Ready = ready;
						return connection;
					case "Error":
						throw new SurfaceProtocolException(
							"The surface refused the handshake (" + GetString(root, "phase") + "): " + GetString(root, "message"));
				}
			}
		} catch {
			await connection.DisposeAsync().ConfigureAwait(false);
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

	/// <summary>
	/// Asks the surface which element is at a design point, for a click on the canvas. See <see cref="NativeExcursionAsync"/> for how:
	/// the surface only selects in its native-window mode, and a hit (<c>Selected</c>) cannot be told from a miss (silence) by
	/// waiting, so the answer is whatever <c>Selected</c> came before an ordering fence. None is a certain miss and returns null,
	/// without faulting the connection.
	/// </summary>
	public async Task<SurfacePick?> PickAtAsync(
		string xaml, double width, double height, double scale, double x, double y, TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		var picks = await NativeExcursionAsync(xaml, width, height, scale,
			new[] { new Dictionary<string, object?> { ["type"] = "PickAt", ["x"] = x, ["y"] = y } }, timeout, cancellationToken).ConfigureAwait(false);
		return picks.FirstOrDefault();
	}

	/// <summary>
	/// Selects each of <paramref name="paths"/> (the surface's own tree paths, dot separated: "0" is the root, "0.0" its first child) and
	/// returns the elements that exist, keyed by path, with their names, types and bounds in design space. A path that resolves to nothing
	/// is silent, so it is simply absent from the result. This is how a tree with geometry is assembled, because the surface has no message
	/// that lists one.
	/// </summary>
	public async Task<IReadOnlyDictionary<string, SurfacePick>> SelectPathsAsync(
		string xaml, double width, double height, double scale, IReadOnlyList<string> paths, TimeSpan timeout,
		CancellationToken cancellationToken = default)
	{
		var picks = await NativeExcursionAsync(xaml, width, height, scale,
			paths.Select(path => new Dictionary<string, object?> { ["type"] = "SelectByPath", ["path"] = path }).ToList(), timeout, cancellationToken).ConfigureAwait(false);
		var byPath = new Dictionary<string, SurfacePick>(StringComparer.Ordinal);
		foreach (var pick in picks)
			if (pick.Path != null)
				byPath.TryAdd(pick.Path, pick);
		return byPath;
	}

	/// <summary>
	/// Runs <paramref name="requests"/> against the page in the surface's native-window mode and returns every <c>Selected</c> they caused.
	///
	/// The surface only selects in native mode: in the frame mode this client renders with, <c>PickAt</c> finds nothing, even on a named
	/// button. So this is a self-contained excursion under the request gate: <c>EnterNative</c> with the document and a fixed 1:1 view
	/// (a fitted view scales the pane, and picks and bounds are in pane space), <c>SetMode</c> design, the requests, a fence, then
	/// <c>ExitNative</c> so the next render is a frame again (in native mode <c>UpdateXaml</c> answers with a window handle, not a frame).
	/// The native window is only built when something needs it.
	///
	/// The fence: a hit is answered with <c>Selected</c> and a miss with silence, which waiting cannot tell from a slow hit. The surface
	/// handles messages on its UI thread in order, and <c>UpdateXaml</c> in native mode re-hosts the page and answers <c>Hwnd</c>, so
	/// sending one after the requests means that when its <c>Hwnd</c> arrives every <c>Selected</c> has already been sent.
	/// <paramref name="timeout"/> only bounds how long a stuck surface is waited for (a fence that never comes faults the connection).
	/// </summary>
	async Task<IReadOnlyList<SurfacePick>> NativeExcursionAsync(
		string xaml, double width, double height, double scale, IReadOnlyList<Dictionary<string, object?>> requests, TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			ThrowIfFaulted();
			try {
				DrainStale();
				await SendAsync(new Dictionary<string, object?> {
					["type"] = "EnterNative",
					["xaml"] = xaml,
					["width"] = width,
					["height"] = height,
					["scale"] = scale,
					["sampleData"] = false,
					["documentPath"] = null,
					["paneWidthPx"] = (int)Math.Ceiling(width * scale),
					["paneHeightPx"] = (int)Math.Ceiling(height * scale),
					["view"] = new Dictionary<string, object?> { ["fit"] = false, ["zoom"] = 1.0, ["offsetX"] = 0.0, ["offsetY"] = 0.0 },
				}).ConfigureAwait(false);

				bool hosted = false;
				while (true) {
					using var doc = await NextAsync(requestTimeout, cancellationToken).ConfigureAwait(false);
					string type = GetString(doc.RootElement, "type");
					if (type == "Hwnd") { hosted = true; break; }
					if (type == "Error") break;   // the native flag stays set either way, so leave native mode below
				}

				var selected = new List<SurfacePick>();
				if (hosted && requests.Count > 0) {
					await SendAsync(new Dictionary<string, object?> { ["type"] = "SetMode", ["design"] = true }).ConfigureAwait(false);
					foreach (var request in requests)
						await SendAsync(request).ConfigureAwait(false);
					await SendAsync(new Dictionary<string, object?> {
						["type"] = "UpdateXaml", ["xaml"] = xaml, ["sampleData"] = false, ["documentPath"] = null,
					}).ConfigureAwait(false);
					await CollectUntilFenceAsync(selected, timeout, cancellationToken).ConfigureAwait(false);
				}

				await SendAsync(new Dictionary<string, object?> { ["type"] = "ExitNative" }).ConfigureAwait(false);
				while (true) {
					using var doc = await NextAsync(requestTimeout, cancellationToken).ConfigureAwait(false);
					if (GetString(doc.RootElement, "type") == "NativeExited")
						break;
				}
				return selected;
			} catch (Exception ex) when (ex is not SurfaceConnectionFaultedException && ex is not OperationCanceledException) {
				fault = ex;
				throw new SurfaceConnectionFaultedException("The surface connection was lost during a native excursion.", ex);
			}
		} finally {
			gate.Release();
		}
	}

	/// <summary>Reads until the fence (<c>Hwnd</c>, or an <c>Error</c>), adding every <c>Selected</c> seen before it to <paramref name="selected"/>.</summary>
	async Task CollectUntilFenceAsync(List<SurfacePick> selected, TimeSpan timeout, CancellationToken cancellationToken)
	{
		while (true) {
			// A fence that never comes means the surface is stuck or gone: the TimeoutException faults the connection.
			using var doc = await NextAsync(timeout, cancellationToken).ConfigureAwait(false);
			var root = doc.RootElement;
			switch (GetString(root, "type")) {
				case "Selected":
					selected.Add(new SurfacePick(GetInt(root, "id"), GetString(root, "elementType"), GetNullableString(root, "name"),
						GetNullableString(root, "path"), GetDouble(root, "x"), GetDouble(root, "y"), GetDouble(root, "w"), GetDouble(root, "h")));
					break;
				case "Hwnd" or "Error":
					return;
			}
		}
	}

	Task SendAsync(Dictionary<string, object?> message) => writer.WriteLineAsync(JsonSerializer.Serialize(message));
	async Task<SurfaceRenderResult> RequestAsync(Dictionary<string, object?> message, CancellationToken cancellationToken)
	{
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try {
			ThrowIfFaulted();
			try {
				DrainStale();
				await writer.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
				while (true) {
					using var doc = await NextAsync(requestTimeout, cancellationToken).ConfigureAwait(false);
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

	void ThrowIfFaulted()
	{
		if (fault != null)
			throw new SurfaceConnectionFaultedException("The surface connection is no longer usable.", fault);
	}

	/// <summary>Drops anything the surface said between requests (a late selection, a view change) so it cannot be mistaken for a reply.</summary>
	void DrainStale()
	{
		while (messages.Reader.TryRead(out var stale))
			stale.Dispose();
	}

	/// <summary>The next message, or a <see cref="TimeoutException"/> after <paramref name="timeout"/> (the read itself is never cancelled).</summary>
	async Task<JsonDocument> NextAsync(TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(timeout);
		try {
			return await messages.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
		} catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
			throw new TimeoutException("The surface did not reply within " + timeout + ".");
		} catch (ChannelClosedException e) {
			throw new EndOfStreamException("The surface closed the connection.", e.InnerException);
		}
	}

	async Task ReadLoopAsync()
	{
		try {
			string? line;
			while ((line = await reader.ReadLineAsync(stopReading.Token).ConfigureAwait(false)) != null) {
				if (line.Length == 0)
					continue;
				messages.Writer.TryWrite(JsonDocument.Parse(line));
			}
			messages.Writer.TryComplete();
		} catch (OperationCanceledException) {
			messages.Writer.TryComplete();
		} catch (Exception e) {
			messages.Writer.TryComplete(e);
		}
	}

	static string GetString(JsonElement e, string name)
		=> e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

	static string? GetNullableString(JsonElement e, string name)
		=> e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

	static int GetInt(JsonElement e, string name, int fallback = 0)
		=> e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : fallback;

	static double GetDouble(JsonElement e, string name)
		=> e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var v) ? v : 0;

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

	int disposed;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref disposed, 1) != 0)
			return;   // disposing twice is allowed and does nothing the second time
		stopReading.Cancel();
		// Closing the stream also ends a read that is blocked inside the socket.
		try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
		try { await readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
		DrainStale();
		reader.Dispose();
		try { await writer.DisposeAsync().ConfigureAwait(false); } catch { }
		stopReading.Dispose();
		gate.Dispose();
	}
}
