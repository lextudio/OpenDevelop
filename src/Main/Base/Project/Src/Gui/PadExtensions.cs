#if !HAS_UNO
using ICSharpCode.SharpDevelop;
#endif

namespace ICSharpCode.SharpDevelop.Gui;

public static class PadExtensions
{
	public static void BringPadToFront(this PadDescriptor pad)
	{
#if !HAS_UNO
		pad.BringPadToFront();
#endif
	}
}
