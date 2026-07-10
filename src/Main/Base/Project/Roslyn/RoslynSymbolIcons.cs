// Maps Roslyn symbol kinds to SD's existing icon resources, for context-action popups that
// operate directly on Microsoft.CodeAnalysis.ISymbol (see doc/technotes/csharp-roslyn.md).
//
// Shares its base icons and accessibility/static overlay compositing with CompletionImage
// (the icon service used by the AvalonEdit gutter/bookmarks) so that both the quick-class-browser
// dropdown and the editor margin render the same icon for the same symbol.

using System.Windows.Media;

using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor.CodeCompletion;
using Microsoft.CodeAnalysis;

using SDAccessibility = ICSharpCode.TypeSystem.Accessibility;
using RoslynAccessibility = Microsoft.CodeAnalysis.Accessibility;

namespace ICSharpCode.SharpDevelop.Roslyn
{
	public static class RoslynSymbolIcons
	{
		public static ImageSource GetImage(ISymbol symbol)
		{
			if (symbol.Kind == SymbolKind.Namespace)
				return CompletionImage.NamespaceImage;

			CompletionImage image = GetCompletionImage(symbol);
			if (image != null)
				return image.GetImage(GetAccessibility(symbol), symbol.IsStatic);

			// Kinds with no CompletionImage equivalent fall back to the old resource lookup.
			string resourceName = GetFallbackResourceName(symbol);
			return resourceName != null ? SD.ResourceService.GetImage(resourceName).ImageSource : null;
		}

		static CompletionImage GetCompletionImage(ISymbol symbol)
		{
			switch (symbol.Kind) {
				case SymbolKind.NamedType:
					var namedType = (INamedTypeSymbol)symbol;
					switch (namedType.TypeKind) {
						case TypeKind.Interface:
							return CompletionImage.Interface;
						case TypeKind.Struct:
							return CompletionImage.Struct;
						case TypeKind.Enum:
							return CompletionImage.Enum;
						case TypeKind.Delegate:
							return CompletionImage.Delegate;
						case TypeKind.Module:
							return CompletionImage.StaticClass;
						default:
							return symbol.IsStatic ? CompletionImage.StaticClass : CompletionImage.Class;
					}
				case SymbolKind.Method:
					var method = (IMethodSymbol)symbol;
					if (method.MethodKind == MethodKind.Constructor)
						return CompletionImage.Constructor;
					if (method.MethodKind == MethodKind.UserDefinedOperator || method.MethodKind == MethodKind.Destructor)
						return CompletionImage.Operator;
					return CompletionImage.Method;
				case SymbolKind.Field:
					var field = (IFieldSymbol)symbol;
					if (field.IsConst)
						return field.ContainingType != null && field.ContainingType.TypeKind == TypeKind.Enum
							? CompletionImage.EnumValue : CompletionImage.Literal;
					return field.IsReadOnly ? CompletionImage.ReadOnlyField : CompletionImage.Field;
				case SymbolKind.Property:
					var property = (IPropertySymbol)symbol;
					return property.IsIndexer ? CompletionImage.Indexer : CompletionImage.Property;
				case SymbolKind.Event:
					return CompletionImage.Event;
				default:
					return null;
			}
		}

		static string GetFallbackResourceName(ISymbol symbol)
		{
			switch (symbol.Kind) {
				case SymbolKind.Parameter:
				case SymbolKind.Local:
					return "Icons.16x16.Local";
				default:
					return null;
			}
		}

		static SDAccessibility GetAccessibility(ISymbol symbol)
		{
			switch (symbol.DeclaredAccessibility) {
				case RoslynAccessibility.Private:
					return SDAccessibility.Private;
				case RoslynAccessibility.Protected:
					return SDAccessibility.Protected;
				case RoslynAccessibility.Internal:
					return SDAccessibility.Internal;
				case RoslynAccessibility.ProtectedOrInternal:
					return SDAccessibility.ProtectedOrInternal;
				case RoslynAccessibility.ProtectedAndInternal:
					return SDAccessibility.ProtectedAndInternal;
				default:
					return SDAccessibility.Public;
			}
		}
	}
}
