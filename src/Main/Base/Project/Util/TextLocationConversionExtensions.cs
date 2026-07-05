// ICSharpCode.TypeSystem.Abstractions.TextLocation (used by DomRegion.Begin/End) and
// ICSharpCode.AvalonEdit.Document.TextLocation are separate, unrelated types with the same shape
// (Line/Column). A handful of call sites need to bridge one to the other.
namespace ICSharpCode.TypeSystem
{
	public static class TextLocationConversionExtensions
	{
		public static ICSharpCode.AvalonEdit.Document.TextLocation ToAvalonEditLocation(this TextLocation location)
		{
			return new ICSharpCode.AvalonEdit.Document.TextLocation(location.Line, location.Column);
		}

		public static TextLocation ToTypeSystemLocation(this ICSharpCode.AvalonEdit.Document.TextLocation location)
		{
			return new TextLocation(location.Line, location.Column);
		}
	}
}
