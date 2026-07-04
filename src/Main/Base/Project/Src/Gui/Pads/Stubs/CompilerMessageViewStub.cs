// MVP stub: the real CompilerMessageView (WinForms output pad UI control) is excluded per the WinForms
// strip policy. MessageViewCategory.Create() only ever calls Instance.AddCategory() to register a new
// category with the pad; a no-op stub keeps that (widely-used) factory method compiling without pulling
// the WinForms pad control back in.
namespace ICSharpCode.SharpDevelop.Gui
{
	public sealed class CompilerMessageView
	{
		public static readonly CompilerMessageView Instance = new CompilerMessageView();

		public void AddCategory(object category)
		{
			// no-op: the output pad UI is not present in this MVP build.
		}
	}
}
