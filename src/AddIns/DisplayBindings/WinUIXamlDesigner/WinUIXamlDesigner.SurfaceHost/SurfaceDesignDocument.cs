using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost;

/// <summary>
/// One designer document served by one Surface process, expressed in the shared DDP shapes
/// (<c>session/open</c>, <c>session/update</c>, <c>design/export-png</c>, <c>session/close</c>).
/// A Surface renders and nothing else: it has no document model, no element ids and no write path, so
/// the parent's XAML source model stays the authority and this backend is a better renderer only
/// (doc/technotes/winui-designer-landscape.md, 7.1). Consequences, all visible in the returned state:
/// <list type="bullet">
/// <item><description><see cref="DesignerSessionState.Tree"/> is null (the surface reports no element tree yet).</description></item>
/// <item><description>Edit RPCs are refused as ordinary results (<see cref="Refuse"/>), never thrown, with the version unchanged.</description></item>
/// <item><description>A render error is a rejected state carrying a positioned diagnostic in the sent text's own coordinates.</description></item>
/// </list>
/// </summary>
public sealed class SurfaceDesignDocument : IAsyncDisposable
{
	readonly SurfaceConnection connection;
	readonly SurfaceProcessHost? owner;
	readonly Action? afterDispose;
	long frameSequence;
	long version = -1;
	byte[]? lastPng;
	DesignerSessionState? lastState;

	public SurfaceDesignDocument(SurfaceConnection connection) => this.connection = connection;

	/// <summary>
	/// Serves the document from <paramref name="owner"/>'s process, which is stopped when the document is disposed.
	/// <paramref name="afterDispose"/> then runs (for example to delete the document's private run directory).
	/// </summary>
	public SurfaceDesignDocument(SurfaceProcessHost owner, Action? afterDispose = null)
	{
		this.owner = owner;
		this.afterDispose = afterDispose;
		connection = owner.Connection;
	}

	/// <summary>The surface's stderr tail, when the document owns the process.</summary>
	public string ChildLog => owner?.Log ?? "";

	/// <summary>The Windows App SDK version the surface was built against (informational; "unknown" when absent).</summary>
	public string Wasdk => connection.Ready.Wasdk;

	/// <summary>Opens the document: the first render, which also fixes the surface's viewport.</summary>
	public async Task<DesignerSessionState> OpenAsync(DesignerDocumentRequest request, CancellationToken cancellationToken = default)
	{
		var snapshot = request.Snapshot;
		var viewport = request.Viewport ?? new DesignerViewport { Width = 1280, Height = 720 };
		var result = await connection.LoadXamlAsync(
			PrimaryText(snapshot), viewport.Width, viewport.Height, viewport.Dpi, false, null, cancellationToken).ConfigureAwait(false);
		return Complete(snapshot, result);
	}

	/// <summary>Re-renders a changed document at the viewport the surface already holds.</summary>
	public async Task<DesignerSessionState> UpdateAsync(DesignerDocumentRequest request, CancellationToken cancellationToken = default)
	{
		var snapshot = request.Snapshot;
		if (version >= 0 && snapshot.Version < version)
			return Rejected(snapshot.SessionId, snapshot.DocumentId, version,
				$"Stale document version {snapshot.Version}; the surface is at {version}.");
		var result = await connection.UpdateXamlAsync(PrimaryText(snapshot), false, null, cancellationToken).ConfigureAwait(false);
		return Complete(snapshot, result);
	}

	/// <summary>An edit request this backend cannot honour; a normal result, with the document version unchanged.</summary>
	public DesignerSessionState Refuse(string sessionId, string documentId, string operation)
		=> Rejected(sessionId, documentId, Math.Max(version, 0),
			operation + " is not supported by the Surface renderer; edits are applied to the source document.");

	/// <summary>Writes the last rendered frame to <paramref name="path"/> and returns the path.</summary>
	public string ExportPng(string path)
	{
		if (lastPng == null)
			throw new InvalidOperationException("No frame has been rendered yet.");
		File.WriteAllBytes(path, lastPng);
		return path;
	}

	DesignerSessionState Complete(DesignerDocumentSnapshot snapshot, SurfaceRenderResult result)
	{
		if (result.Error is { } error) {
			// The document did not change as far as the surface is concerned; keep the previous version and frame.
			var state = Rejected(snapshot.SessionId, snapshot.DocumentId, Math.Max(version, 0), error.Message);
			state.Diagnostics.Add(new DesignerDiagnostic {
				Severity = "Error",
				Message = error.Message,
				FileName = snapshot.PrimaryFileName,
				Line = error.Line ?? 0,
				Column = error.Column ?? 0,
			});
			state.Render = lastState?.Render;
			return state;
		}

		var frame = result.Frame!;
		lastPng = frame.Png;
		version = snapshot.Version;
		var ok = new DesignerSessionState {
			SessionId = snapshot.SessionId,
			DocumentId = snapshot.DocumentId,
			Version = version,
			Accepted = true,
			RootType = RootElementName(PrimaryText(snapshot)),
			Render = new DesignerRenderFrame {
				Sequence = ++frameSequence,
				// Same basis as the Uno/Microsoft hosts: pixels in Width/Height, scale in Dpi.
				Width = frame.Width,
				Height = frame.Height,
				Dpi = frame.DipWidth > 0 ? (double)frame.Width / frame.DipWidth : 1.0,
				PngBase64 = Convert.ToBase64String(frame.Png),
			},
		};
		lastState = ok;
		return ok;
	}

	static DesignerSessionState Rejected(string sessionId, string documentId, long version, string error)
		=> new() { SessionId = sessionId, DocumentId = documentId, Version = version, Accepted = false, Error = error };

	static string PrimaryText(DesignerDocumentSnapshot snapshot)
	{
		var file = snapshot.Files.FirstOrDefault(item => item.FileName == snapshot.PrimaryFileName)
			?? snapshot.Files.FirstOrDefault(item => item.Kind == "Source")
			?? snapshot.Files.FirstOrDefault();
		return file?.Text ?? "";
	}

	/// <summary>The local name of the root element, or "" when the text is not well-formed XML.</summary>
	static string RootElementName(string xaml)
	{
		try {
			return System.Xml.Linq.XDocument.Parse(xaml).Root?.Name.LocalName ?? "";
		} catch (System.Xml.XmlException) {
			return "";
		}
	}

	public async ValueTask DisposeAsync()
	{
		try {
			await (owner != null ? owner.DisposeAsync() : connection.DisposeAsync()).ConfigureAwait(false);
		} finally {
			afterDispose?.Invoke();
		}
	}
}
