using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Child;

/// <summary>
/// The reply of <c>design/native-live</c>. <see cref="Native"/> says whether the document is now in the surface's own window
/// (<see cref="Hwnd"/> and the sizes describe it). Otherwise <see cref="State"/> is the snapshot to show, and <see cref="Message"/>,
/// when set, says why the switch did not happen (for the shell's recovery banner).
/// </summary>
public sealed class SurfaceNativeLiveReply
{
	public bool Native { get; set; }
	public long Hwnd { get; set; }
	public int PixelWidth { get; set; }
	public int PixelHeight { get; set; }
	public double DipWidth { get; set; }
	public double DipHeight { get; set; }
	public double Scale { get; set; } = 1;
	public DesignerSessionState? State { get; set; }
	public string? Message { get; set; }
}
