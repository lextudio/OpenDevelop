// The first real (non-mock) IParser for .cs files. See doc/technotes/csharp-roslyn.md, Phase 1.
// Registered at /SharpDevelop/Parser via AvalonEdit.AddIn.addin so SD.ParserService.Resolve(...)/
// GetCompilationForFile(...) return real data everywhere that already consumes ResolveResult/
// IEntity/IType (GoToDefinition, DefinitionViewPad, DeclaringTypeSubMenuBuilder,
// SymbolTypeAtCaretConditionEvaluator, EditorRefactoringContext, ...), instead of always null.

using System;
using System.Collections.Generic;
using System.Threading;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Editor.Search;
using ICSharpCode.SharpDevelop.Parser;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Refactoring;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.TypeSystem;
using Microsoft.CodeAnalysis;
using TextLocation = ICSharpCode.AvalonEdit.Document.TextLocation;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public class RoslynParser : IParser
	{
		public IReadOnlyList<string> TaskListTokens { get; set; }

		public bool CanParse(string fileName)
		{
			return fileName != null && fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
		}

		public ITextSource GetFileContent(FileName fileName)
		{
			return new StringTextSource(System.IO.File.ReadAllText(fileName));
		}

		public ParseInformation Parse(FileName fileName, ITextSource fileContent, bool fullParseInformationRequested,
		                               IProject parentProject, CancellationToken cancellationToken)
		{
			var document = RoslynWorkspaceHelper.FindDocument(fileName, fileContent != null ? fileContent.Text : null);
			if (document == null)
				return null;
			var compilation = new RoslynCompilation(document.Project.GetCompilationAsync(cancellationToken).Result);
			var unresolvedFile = new RoslynUnresolvedFile(document, compilation);
			return new ParseInformation(unresolvedFile, fileContent != null ? fileContent.Version : null, fullParseInformationRequested);
		}

		public ResolveResult Resolve(ParseInformation parseInfo, TextLocation location, ICompilation compilation, CancellationToken cancellationToken)
		{
			var unresolvedFile = parseInfo.UnresolvedFile as RoslynUnresolvedFile;
			if (unresolvedFile == null)
				return ErrorResolveResult.UnknownError;
			var symbol = RoslynWorkspaceHelper.GetSymbolAt(unresolvedFile.RoslynDocument, location);
			return ToResolveResult(symbol, compilation);
		}

		static ResolveResult ToResolveResult(Microsoft.CodeAnalysis.ISymbol symbol, ICompilation compilation)
		{
			if (symbol == null)
				return ErrorResolveResult.UnknownError;

			var namedType = symbol as INamedTypeSymbol;
			if (namedType != null)
				return new TypeResolveResult((ITypeDefinition)RoslynEntityFactory.Create(namedType, compilation));

			if (symbol is IMethodSymbol || symbol is IFieldSymbol || symbol is IPropertySymbol || symbol is IEventSymbol) {
				var member = (IMember)RoslynEntityFactory.Create(symbol, compilation);
				return new MemberResolveResult(member.DeclaringType, member);
			}

			if (symbol is INamespaceSymbol) {
				// No INamespace adapter yet (see doc/technotes/csharp-roslyn.md) - report as an
				// error rather than crash; namespace go-to-definition isn't a supported flow yet.
				return ErrorResolveResult.UnknownError;
			}

			// Locals/parameters: no IVariable adapter yet either.
			return ErrorResolveResult.UnknownError;
		}

		public ICodeContext ResolveContext(ParseInformation parseInfo, TextLocation location, ICompilation compilation, CancellationToken cancellationToken)
		{
			return null;
		}

		public ResolveResult ResolveSnippet(ParseInformation parseInfo, TextLocation location, string codeSnippet, ICompilation compilation, CancellationToken cancellationToken)
		{
			return ErrorResolveResult.UnknownError;
		}

		public void FindLocalReferences(ParseInformation parseInfo, ITextSource fileContent, IVariable variable, ICompilation compilation, Action<SearchResultMatch> callback, CancellationToken cancellationToken)
		{
		}

		public ICompilation CreateCompilationForSingleFile(FileName fileName, IUnresolvedFile unresolvedFile)
		{
			var roslynFile = unresolvedFile as RoslynUnresolvedFile;
			if (roslynFile == null)
				return null;
			return new RoslynCompilation(roslynFile.RoslynDocument.Project.GetCompilationAsync().Result);
		}
	}
}
