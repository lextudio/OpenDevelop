// See doc/technotes/csharp-roslyn.md, Phase 1.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.TypeSystem;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	sealed class RoslynCompilation : ICompilation
	{
		readonly Compilation compilation;
		readonly IAssembly mainAssembly;

		public RoslynCompilation(Compilation compilation)
		{
			this.compilation = compilation;
			this.mainAssembly = new RoslynAssemblyAdapter(compilation.Assembly, this);
		}

		public Compilation RoslynCompilationInstance { get { return compilation; } }

		public IAssembly MainAssembly { get { return mainAssembly; } }
		public ITypeResolveContext TypeResolveContext { get { return new SimpleTypeResolveContext(this); } }
		public IList<IAssembly> Assemblies { get { return new List<IAssembly> { mainAssembly }; } }
		public IList<IAssembly> ReferencedAssemblies { get { return new List<IAssembly>(); } }
		public INamespace RootNamespace { get { throw new NotImplementedException(); } }
		public INamespace GetNamespaceForExternAlias(string alias) { return null; }
		public IType FindType(KnownTypeCode typeCode) { throw new NotImplementedException(); }
		public StringComparer NameComparer { get { return StringComparer.Ordinal; } }
		public ISolutionSnapshot SolutionSnapshot { get { throw new NotImplementedException(); } }
		public CacheManager CacheManager { get { return new CacheManager(); } }

		/// <summary>
		/// Looks up a type definition by its Roslyn symbol's containing compilation, or by
		/// resolving the type's metadata name against this compilation if it came from elsewhere
		/// (e.g. a derived type found via SymbolFinder against a different project's compilation).
		/// </summary>
		public ITypeDefinition Import(INamedTypeSymbol symbol)
		{
			if (symbol == null)
				return null;
			if (compilation.Assembly.Equals(symbol.ContainingAssembly, SymbolEqualityComparer.Default))
				return new RoslynTypeDefinitionAdapter(symbol, this);
			var imported = compilation.GetTypeByMetadataName(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).TrimStart(':'));
			return new RoslynTypeDefinitionAdapter(imported ?? symbol, this);
		}
	}
}
