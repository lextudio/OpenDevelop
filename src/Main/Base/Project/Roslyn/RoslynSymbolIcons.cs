// Maps Roslyn symbol kinds to SD's existing icon resources, for context-action popups that
// operate directly on Microsoft.CodeAnalysis.ISymbol (see doc/technotes/csharp-roslyn.md).

using System.Windows.Media;

using ICSharpCode.SharpDevelop;
using Microsoft.CodeAnalysis;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public static class RoslynSymbolIcons
	{
		public static ImageSource GetImage(ISymbol symbol)
		{
			string resourceName = GetResourceName(symbol);
			return resourceName != null ? SD.ResourceService.GetImage(resourceName).ImageSource : null;
		}

		static string GetResourceName(ISymbol symbol)
		{
			switch (symbol.Kind) {
				case SymbolKind.NamedType:
					var namedType = (INamedTypeSymbol)symbol;
					switch (namedType.TypeKind) {
						case TypeKind.Interface:
							return "Icons.16x16.Interface";
						case TypeKind.Struct:
							return "Icons.16x16.Struct";
						case TypeKind.Enum:
							return "Icons.16x16.Enum";
						case TypeKind.Delegate:
							return "Icons.16x16.Delegate";
						default:
							return "Icons.16x16.Class";
					}
				case SymbolKind.Method:
					var method = (IMethodSymbol)symbol;
					if (method.MethodKind == MethodKind.Constructor || method.MethodKind == MethodKind.Destructor)
						return "Icons.16x16.Method";
					return method.MethodKind == MethodKind.UserDefinedOperator ? "Icons.16x16.Operator" : "Icons.16x16.Method";
				case SymbolKind.Field:
					return "Icons.16x16.Field";
				case SymbolKind.Property:
					return "Icons.16x16.Property";
				case SymbolKind.Event:
					return "Icons.16x16.Event";
				case SymbolKind.Parameter:
				case SymbolKind.Local:
					return "Icons.16x16.Local";
				case SymbolKind.Namespace:
					return "Icons.16x16.NameSpace";
				default:
					return null;
			}
		}
	}
}
