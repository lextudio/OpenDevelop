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
