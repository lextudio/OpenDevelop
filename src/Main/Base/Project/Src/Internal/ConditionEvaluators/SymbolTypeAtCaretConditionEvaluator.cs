// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

// If `parameter` is already a resolved Roslyn symbol (e.g. DeclaringTypeSubMenuBuilder passes a
// live INamedTypeSymbol straight through to /SharpDevelop/EntityContextMenu), checking its runtime
// type here is unavoidable - that producer is itself Roslyn-coupled and out of scope for this pass
// (see doc/technotes/csharp-vb-binding.md). What used to also be Roslyn-specific debt was the
// *editor-caret fallback*, which resolved via RoslynWorkspaceHelper.GetSymbolAtCaret directly; that
// part now goes through the shared ILanguageService contract instead, so it works for any
// registered language, not just Roslyn-backed ones. The IEntityModel path is untouched -
// IEntityModel/Dom.* is SharpDevelop's separate background project-content model (used by
// EntityBookmark/GotoDialog), not part of the ParserService/IParser resolve flow this rewrite targets.

using System.Threading;
using ICSharpCode.Core;
using ICSharpCode.TypeSystem;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.LanguageServices;
using RoslynSymbol = Microsoft.CodeAnalysis.ISymbol;

namespace ICSharpCode.SharpDevelop.Internal.ConditionEvaluators
{
	/// <summary>
	/// Condition evaluator checking the type of the symbol under the caret (if there is one).
	/// </summary>
	public class SymbolTypeAtCaretConditionEvaluator : IConditionEvaluator
	{
		public bool IsValid(object parameter, Condition condition)
		{
			if (parameter is IEntityModel) {
				return IsValidEntityModel((IEntityModel)parameter, condition);
			}

			var entity = parameter as IEntity;
			if (entity != null) {
				if (condition.Properties["projectonly"] == "true" && entity.Region.IsEmpty)
					return false;
				return MatchesRequestedType(condition,
					isMember: entity is IMember, isType: entity is ITypeDefinition, isNamespace: false, isLocal: false);
			}

			var symbol = parameter as RoslynSymbol;
			if (symbol != null) {
				bool hasSourceLocation = !symbol.Locations.IsEmpty && symbol.Locations[0].IsInSource;
				if (condition.Properties["projectonly"] == "true" && !hasSourceLocation)
					return false;

				return MatchesRequestedType(condition,
					isMember: symbol is Microsoft.CodeAnalysis.IMethodSymbol || symbol is Microsoft.CodeAnalysis.IFieldSymbol
						|| symbol is Microsoft.CodeAnalysis.IPropertySymbol || symbol is Microsoft.CodeAnalysis.IEventSymbol,
					isType: symbol is Microsoft.CodeAnalysis.INamedTypeSymbol,
					isNamespace: symbol is Microsoft.CodeAnalysis.INamespaceSymbol,
					isLocal: symbol is Microsoft.CodeAnalysis.ILocalSymbol || symbol is Microsoft.CodeAnalysis.IParameterSymbol);
			}

			var kind = GetSymbolKindAtCaret(parameter);
			if (kind == null)
				return false;

			if (condition.Properties["projectonly"] == "true" && !kind.HasSourceLocation)
				return false;

			return MatchesRequestedType(condition, isMember: kind.IsMember, isType: kind.IsType, isNamespace: kind.IsNamespace, isLocal: kind.IsLocal);
		}

		static SymbolKindInfo GetSymbolKindAtCaret(object parameter)
		{
			var editor = parameter as ITextEditor ?? SD.GetActiveViewContentService<ITextEditor>();
			if (editor == null || editor.FileName == null)
				return null;

			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(editor.FileName, out var service))
				return null;

			try {
				var id = new DocumentId(editor.FileName.ToString());
				service.UpsertDocumentAsync(id, editor.Document.Text, CancellationToken.None).GetAwaiter().GetResult();
				return service.GetSymbolKindAsync(id, editor.Caret.Offset, CancellationToken.None).GetAwaiter().GetResult();
			} catch {
				return null;
			}
		}

		static bool IsValidEntityModel(IEntityModel entityModel, Condition condition)
		{
			IEntity entity = entityModel.Resolve();
			if (entity == null)
				return false;
			if (condition.Properties["projectonly"] == "true" && entity.Region.IsEmpty)
				return false;
			return MatchesRequestedType(condition,
				isMember: entity is IMember,
				isType: entity is ITypeDefinition,
				isNamespace: false,
				isLocal: false);
		}

		static bool MatchesRequestedType(Condition condition, bool isMember, bool isType, bool isNamespace, bool isLocal)
		{
			string typesList = condition.Properties["type"];
			if (typesList == null)
				return false;
			foreach (string type in typesList.Split(',')) {
				switch (type.Trim()) {
					case "*":
						return true;
					case "member":
						if (isMember) return true;
						break;
					case "type":
						if (isType) return true;
						break;
					case "namespace":
						if (isNamespace) return true;
						break;
					case "local":
						if (isLocal) return true;
						break;
				}
			}
			return false;
		}
	}
}
