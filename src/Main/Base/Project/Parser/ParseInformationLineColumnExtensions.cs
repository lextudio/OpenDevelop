// MVP mock/extension: several call sites (TaskListPad.cs) call a (line, column) overload of
// GetInnermostTypeDefinition that existed in the real NRefactory-based build; the abstraction layer only
// exposes the TextLocation-based overload (on both ParseInformation.UnresolvedFile and IUnresolvedFile
// directly), so this adapts one to the other for both receiver types.
namespace ICSharpCode.TypeSystem
{
	public static class UnresolvedFileLineColumnExtensions
	{
		public static IUnresolvedTypeDefinition GetInnermostTypeDefinition(this IUnresolvedFile file, int line, int column)
		{
			if (file == null)
				return null;
			return file.GetInnermostTypeDefinition(new TextLocation(line, column));
		}
	}
}

namespace ICSharpCode.SharpDevelop.Parser
{
	using ICSharpCode.TypeSystem;

	public static class ParseInformationLineColumnExtensions
	{
		public static IUnresolvedTypeDefinition GetInnermostTypeDefinition(this ParseInformation parseInfo, int line, int column)
		{
			if (parseInfo == null || parseInfo.UnresolvedFile == null)
				return null;
			return parseInfo.UnresolvedFile.GetInnermostTypeDefinition(new TextLocation(line, column));
		}
	}
}
