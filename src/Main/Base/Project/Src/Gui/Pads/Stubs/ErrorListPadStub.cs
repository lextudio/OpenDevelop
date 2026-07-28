#if HAS_UNO
// Stub for ErrorListPad used by AddIn projects that reference Base but not the full host app.
namespace ICSharpCode.SharpDevelop.Gui
{
	public static class _ErrorListPadStub
	{
		public static bool ShowAfterBuild => false;
	}
}

namespace ICSharpCode.SharpDevelop.Gui
{
	public static class ErrorListPad
	{
		public static bool ShowAfterBuild => false;
	}
}
#endif
