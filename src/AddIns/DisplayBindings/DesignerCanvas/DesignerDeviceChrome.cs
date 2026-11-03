using System.Windows.Media;

namespace ICSharpCode.SharpDevelop.Designer.Surface;

/// <summary>What kind of device a design preview represents, for chrome defaults.</summary>
public enum DesignerDeviceKind
{
	Phone,
	Tablet,
}

/// <summary>The camera cut-out drawn over the top of the screen.</summary>
public enum DesignerNotchKind
{
	None,
	Notch,
	Island,
}

/// <summary>
/// Optional device body drawn around a design preview by <see cref="DesignSurface"/>: a rounded
/// frame in the device's own shape, the screen silhouette (including a notch or Dynamic Island
/// cut-out), and the home indicator. The backend supplies it; null means the plain bitmap the
/// canvas has always drawn, so every other designer is unaffected.
/// <para>
/// The screen silhouette is an image (alpha = visible screen) because the exact per-device shape
/// (corner radii, island position) is not exposed as numbers by the platform; without one the
/// canvas falls back to <see cref="ScreenCornerRadius"/>. All lengths are in design units and
/// scale with the canvas zoom.
/// </para>
/// </summary>
public sealed class DesignerDeviceChrome
{
	public DesignerDeviceKind Kind { get; init; } = DesignerDeviceKind.Phone;

	/// <summary>Screen corner radius in design units (e.g. 55 on a modern iPhone, 18 on an iPad,
	/// 0 on a home-button iPhone).</summary>
	public double ScreenCornerRadius { get; init; } = 44;

	/// <summary>Body thickness around the screen, in design units (about 5 on an iPhone, 12 on
	/// an iPad).</summary>
	public double BezelThickness { get; init; } = 6;

	/// <summary>Camera cut-out drawn over the page: Dynamic Island, notch, or none (iPad and
	/// home-button iPhones).</summary>
	public DesignerNotchKind Notch { get; init; } = DesignerNotchKind.None;

	/// <summary>Draws the iOS home indicator over the bottom of the design.</summary>
	public bool HomeIndicator { get; init; } = true;

	public Brush BodyBrush { get; init; } =
		new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));

	public Brush EdgeBrush { get; init; } =
		new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));

	public Brush HomeIndicatorBrush { get; init; } =
		new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
}
