// See doc/technotes/csharp-roslyn.md, Phase 1.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.TypeSystem;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	sealed class RoslynUnresolvedFile : IUnresolvedFile
	{
		public readonly Microsoft.CodeAnalysis.Document RoslynDocument;
		readonly ICompilation compilation;

		public RoslynUnresolvedFile(Microsoft.CodeAnalysis.Document roslynDocument, ICompilation compilation)
		{
			this.RoslynDocument = roslynDocument;
			this.compilation = compilation;
		}

		public string FileName { get { return RoslynDocument.FilePath; } }
		public DateTime? LastWriteTime { get; set; }

		public IList<IUnresolvedTypeDefinition> TopLevelTypeDefinitions {
			get {
				var model = RoslynDocument.GetSemanticModelAsync().Result;
				var root = model.SyntaxTree.GetRoot();
				return root.DescendantNodes()
					.Select(n => model.GetDeclaredSymbol(n) as INamedTypeSymbol)
					.Where(s => s != null && s.ContainingType == null)
					.Distinct(SymbolEqualityComparer.Default)
					.Cast<INamedTypeSymbol>()
					.Select(s => (IUnresolvedTypeDefinition)new RoslynUnresolvedTypeDefinitionAdapter(s, compilation))
					.ToList();
			}
		}

		public IList<IUnresolvedAttribute> AssemblyAttributes { get { return new List<IUnresolvedAttribute>(); } }
		public IList<IUnresolvedAttribute> ModuleAttributes { get { return new List<IUnresolvedAttribute>(); } }

		public IUnresolvedTypeDefinition GetTopLevelTypeDefinition(TextLocation location)
		{
			return TopLevelTypeDefinitions.FirstOrDefault(t => t.Region.IsInside(location));
		}

		public IUnresolvedTypeDefinition GetInnermostTypeDefinition(TextLocation location)
		{
			var symbol = RoslynWorkspaceHelper.GetSymbolAt(RoslynDocument, location.ToAvalonEditLocation());
			var namedType = symbol as INamedTypeSymbol ?? (symbol != null ? symbol.ContainingType : null);
			return namedType != null ? new RoslynUnresolvedTypeDefinitionAdapter(namedType, compilation) : null;
		}

		public IUnresolvedMember GetMember(TextLocation location)
		{
			throw new NotImplementedException();
		}

		public IList<Error> Errors { get { return new List<Error>(); } }
	}
}
