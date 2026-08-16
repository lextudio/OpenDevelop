// Roslyn-based WinForms designer detection for Visual Basic - the VB counterpart of
// CSharpBinding's RoslynFormsDesignerSecondaryDisplayBinding. In the out-of-process
// architecture (the default since 2026-08) the parent only needs to (a) decide whether a
// .vb file is designable and (b) locate its .Designer.vb companion; the child-side
// SnapshotDesignerLoader/DesignerHostService do all real VB parsing, component loading
// and source rewriting.

using System;
using System.Collections.Generic;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Workbench;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.FormsDesigner
{
	public class VbDesignerSecondaryDisplayBinding : ISecondaryDisplayBinding
	{
		public bool ReattachWhenParserServiceIsReady => true;

		public bool CanAttachTo(IViewContent viewContent)
		{
			var textEditor = viewContent.GetService<ITextEditor>();
			if (textEditor == null)
				return false;
			var fileName = viewContent.PrimaryFileName;
			if (fileName == null || !fileName.ToString().EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
				return false;

			// Syntactic-only check (no semantic model/compilation needed just to decide whether
			// to attach). The classic split convention puts the base type in Foo.vb but
			// InitializeComponent in Foo.Designer.vb - a single partial DECLARATION rarely has
			// both, so gather class declarations (grouped by name) across BOTH files before
			// deciding, mirroring RoslynFormsDesignerSecondaryDisplayBinding's resolution.
			var texts = new List<string> { textEditor.Document.Text };
			var designerPath = Path.Combine(
				Path.GetDirectoryName(fileName.ToString()) ?? "",
				Path.GetFileNameWithoutExtension(fileName.ToString()) + ".Designer.vb");
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
				.SelectMany(text => VisualBasicSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<ClassBlockSyntax>())
				.GroupBy(c => c.BlockStatement.Identifier.ValueText);

			foreach (var parts in classDeclsByName) {
				bool hasInitializeComponent = parts.Any(c => c.Members.OfType<MethodBlockSyntax>()
					.Any(m => m.BlockStatement is MethodStatementSyntax method
						&& method.DeclarationKeyword.IsKind(SyntaxKind.SubKeyword)
						&& method.Identifier.ValueText == "InitializeComponent"
						&& method.ParameterList.Parameters.Count == 0));
				bool baseIsFormOrControl = parts.Any(c => c.Inherits
					.SelectMany(inherits => inherits.Types)
					.Any(t => t.ToString().EndsWith("Form", StringComparison.Ordinal)
					       || t.ToString().EndsWith("UserControl", StringComparison.Ordinal)
					       || t.ToString().EndsWith("Component", StringComparison.Ordinal)));
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
				new FormsDesignerViewContent(viewContent, new VbDesignerLoaderProvider())
			};
		}
	}

	public class VbDesignerLoaderProvider : IDesignerLoaderProvider
	{
		public DesignerLoader CreateLoader(FormsDesignerViewContent viewContent)
		{
			// No in-process VB loader exists; the VB backend is child-owned only
			// (OPENDEVELOP_WINFORMS_OOP=0 falls back to a C#-only in-process loader).
			throw new NotSupportedException(
				"The Visual Basic WinForms designer requires the out-of-process host (OPENDEVELOP_WINFORMS_OOP=0 is not supported for .vb files).");
		}

		/// <summary>
		/// The classic WinForms convention: "Foo.vb" designs alongside a co-located
		/// "Foo.Designer.vb" holding the generated field declarations and InitializeComponent.
		/// </summary>
		public IReadOnlyList<OpenedFile> GetSourceFiles(FormsDesignerViewContent viewContent, out OpenedFile designerCodeFile)
		{
			var primaryFileName = viewContent.PrimaryFileName;
			var files = new List<OpenedFile> { viewContent.PrimaryFile };

			var designerPath = FileName.Create(Path.Combine(
				Path.GetDirectoryName(primaryFileName) ?? "",
				Path.GetFileNameWithoutExtension(primaryFileName) + ".Designer.vb"));

			OpenedFile designerFile = null;
			if (File.Exists(designerPath)) {
				designerFile = SD.FileService.GetOrCreateOpenedFile(designerPath);
				files.Add(designerFile);
			}

			designerCodeFile = designerFile ?? viewContent.PrimaryFile;
			return files;
		}
	}
}
