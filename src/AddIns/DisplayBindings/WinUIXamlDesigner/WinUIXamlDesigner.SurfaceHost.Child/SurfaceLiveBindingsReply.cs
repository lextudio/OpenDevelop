namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Child;

/// <summary>
/// Reply to <c>design/live-bindings</c>: whether the preview now evaluates design-time <c>{x:Bind}</c> values, with a
/// message when the surface could not answer.
/// </summary>
public sealed class SurfaceLiveBindingsReply
{
	public bool Live { get; set; }

	public string? Message { get; set; }
}
