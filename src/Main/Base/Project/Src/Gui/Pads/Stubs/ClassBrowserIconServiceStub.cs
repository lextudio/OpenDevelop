// MVP stub: the real ClassBrowserIconService (System.Drawing.Icon-based, layered on the excluded WinForms
// IconService/IWinFormsService.BitmapToIcon surface) is out of MVP scope. This keeps the handful of call
// sites outside the excluded Dom/ClassBrowser and RefactoringService trees (EntityBookmark, GotoDialog,
// DeclaringTypeSubMenuBuilder) compiling; icons are simply not shown (null) in this MVP build.
using ICSharpCode.TypeSystem;

namespace ICSharpCode.SharpDevelop
{
	public static class ClassBrowserIconService
	{
		public static IImage GetIcon(IEntity entity) => null;
		public static IImage GetIcon(IVariable v) => null;
		public static IImage GetIcon(IField v) => null;
		public static IImage GetIcon(IType t) => null;
		public static IImage GetIcon(ITypeDefinition t) => null;
		public static IImage GetIcon(IUnresolvedEntity entity) => null;
		public static IImage GetIcon(IUnresolvedMember m) => null;
		public static IImage GetIcon(IUnresolvedTypeDefinition t) => null;

		public static IImage GotoArrow => null;
		public static IImage CodeTemplate => null;
	}
}
