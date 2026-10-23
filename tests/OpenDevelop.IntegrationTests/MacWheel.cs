using System.Runtime.InteropServices;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Posts a real scroll-wheel event to the window under the pointer through Quartz Event Services. DevFlow can click, press, drag
/// and release but has no wheel, so a test that must reach an application's own wheel handling posts it here, after moving the
/// pointer over the target with DevFlow's <c>move</c>.
/// </summary>
static class MacWheel
{
	const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
	const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

	[DllImport(CoreGraphics)] static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, int units, uint wheelCount, int wheel1);
	[DllImport(CoreGraphics)] static extern void CGEventPost(int tap, IntPtr evt);
	[DllImport(CoreFoundation)] static extern void CFRelease(IntPtr value);

	const int LineUnits = 1;
	const int HidEventTap = 0;

	/// <summary>One wheel event of <paramref name="lines"/> lines (positive scrolls up, away from the user). False when it could not be created.</summary>
	public static bool Post(int lines)
	{
		if (!OperatingSystem.IsMacOS())
			return false;
		var evt = CGEventCreateScrollWheelEvent(IntPtr.Zero, LineUnits, 1, lines);
		if (evt == IntPtr.Zero)
			return false;
		try { CGEventPost(HidEventTap, evt); }
		finally { CFRelease(evt); }
		return true;
	}
}
