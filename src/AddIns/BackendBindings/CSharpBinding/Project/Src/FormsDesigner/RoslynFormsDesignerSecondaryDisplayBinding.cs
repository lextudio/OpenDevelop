// A Roslyn-based replacement for the excluded (NRefactory-based) FormsDesignerSecondaryDisplayBinding
// - see RoslynFormsDesignerLoaderProvider.cs's own doc comment for why this exists as a new class.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.FormsDesigner;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Workbench;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpBinding.FormsDesigner
{
	public class RoslynFormsDesignerSecondaryDisplayBinding : ISecondaryDisplayBinding
	{
		public bool ReattachWhenParserServiceIsReady => true;

		public bool CanAttachTo(IViewContent viewContent)
		{
			var textEditor = viewContent.GetService<ITextEditor>();
			if (textEditor == null)
				return false;
			var fileName = viewContent.PrimaryFileName;
			if (fileName == null || !fileName.ToString().EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				return false;

			// Syntactic-only check (no semantic model/compilation needed just to decide whether to
			// attach). The classic split convention puts the base type in Foo.cs but
			// InitializeComponent in Foo.Designer.cs - a single partial DECLARATION rarely has
			// both, so gather class declarations (grouped by name) across BOTH files before
			// deciding, the same "which of the two parts has what" resolution
			// RoslynDesignerLoader.ParseDesignerFile does for real when actually loading.
			var texts = new List<string> { textEditor.Document.Text };
			var designerPath = Path.Combine(
				Path.GetDirectoryName(fileName.ToString()) ?? "",
				Path.GetFileNameWithoutExtension(fileName.ToString()) + ".Designer.cs");
			if (File.Exists(designerPath)) {
				try {
					texts.Add(File.ReadAllText(designerPath));
				} catch (IOException) {
					// Fall through with just the primary file's text.
				}
			}
			return IsDesignableAcrossParts(texts);
		}

		static bool IsDesignableAcrossParts(IReadOnlyList<string> sourceTexts)
		{
			var classDeclsByName = sourceTexts
				.SelectMany(text => CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
				.GroupBy(c => c.Identifier.Text);

			foreach (var parts in classDeclsByName) {
				bool hasInitializeComponent = parts.Any(c => c.Members.OfType<MethodDeclarationSyntax>()
					.Any(m => m.Identifier.Text == "InitializeComponent" && m.ParameterList.Parameters.Count == 0));
				bool baseIsFormOrControl = parts.Any(c => c.BaseList?.Types
					.Any(t => t.Type.ToString().EndsWith("Form", StringComparison.Ordinal)
					       || t.Type.ToString().EndsWith("UserControl", StringComparison.Ordinal)
					       || t.Type.ToString().EndsWith("Component", StringComparison.Ordinal)) ?? false);
				if (hasInitializeComponent && baseIsFormOrControl)
					return true;
			}
			return false;
		}

		public IViewContent[] CreateSecondaryViewContent(IViewContent viewContent)
		{
			if (viewContent.SecondaryViewContents.Any(c => c is FormsDesignerViewContent))
				return Array.Empty<IViewContent>();

			return new IViewContent[] {
				new FormsDesignerViewContent(viewContent, new RoslynFormsDesignerLoaderProvider())
			};
		}
	}
}
