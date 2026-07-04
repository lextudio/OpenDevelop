// MVP stub: the real ISymbolSearch (find-references/rename search handle) is declared in
// Src/Services/RefactoringService/FindReferenceService.cs, which is excluded per MVP policy (see
// ICSharpCode.SharpDevelop.csproj). IProject/AbstractProject/ProjectBehavior only ever declare a method
// returning this type or pass it through as an opaque reference - none of them call members on it - so a
// minimal marker interface is enough to keep those core project-model files compiling.
using System;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Refactoring
{
	public interface ISymbolSearch
	{
	}
}
