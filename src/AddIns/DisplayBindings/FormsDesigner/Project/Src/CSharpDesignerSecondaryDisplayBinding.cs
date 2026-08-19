// Roslyn-based WinForms designer detection for C#. In the out-of-process architecture (the only
// one since 2026-08, see doc/technotes/winforms-designer.md) the parent only needs to (a) decide
// whether a .cs file is designable and (b) locate its .Designer.cs companion; the child-side
// SnapshotDesignerLoader/DesignerHostService do all real C# parsing, component loading and
// source rewriting. This is the C# counterpart of VbDesignerSecondaryDisplayBinding.

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
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ICSharpCode.FormsDesigner
{
	public class CSharpDesignerSecondaryDisplayBinding : ISecondaryDisplayBinding
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

			// The design view attaches to the primary partial (Foo.cs); the generated
			// Foo.Designer.cs is a companion that should stay a plain source view - otherwise
			// opening the .Designer.cs file from the project browser spawns a second design
			// view over the same form.
			if (fileName.ToString().EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
				return false;

			// Syntactic-only check (no semantic model/compilation needed just to decide whether
			// to attach). The classic split convention puts the base type in Foo.cs but
			// InitializeComponent in Foo.Designer.cs - a single partial DECLARATION rarely has
			// both, so gather class declarations (grouped by name) across BOTH files before
			// deciding, mirroring VbDesignerSecondaryDisplayBinding's resolution.
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
				.SelectMany(text => CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
				.GroupBy(c => c.Identifier.ValueText);

			foreach (var parts in classDeclsByName) {
				bool hasInitializeComponent = parts.Any(c => c.Members.OfType<MethodDeclarationSyntax>()
					.Any(m => m.Identifier.ValueText == "InitializeComponent"
						&& m.ParameterList.Parameters.Count == 0));
				bool baseIsFormOrControl = parts.Any(c => c.BaseList?.Types
					.Any(t => t.ToString().EndsWith("Form", StringComparison.Ordinal)
					       || t.ToString().EndsWith("UserControl", StringComparison.Ordinal)
					       || t.ToString().EndsWith("Component", StringComparison.Ordinal)) == true);
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
				new FormsDesignerViewContent(viewContent, new CSharpDesignerLoaderProvider())
			};
		}
	}

	public class CSharpDesignerLoaderProvider : IDesignerLoaderProvider
	{
		public DesignerLoader CreateLoader(FormsDesignerViewContent viewContent)
		{
			// No in-process C# loader exists anymore; the C# backend is child-owned only.
			throw new NotSupportedException(
				"The C# WinForms designer requires the out-of-process host.");
		}

		/// <summary>
		/// The classic WinForms convention: "Foo.cs" designs alongside a co-located
		/// "Foo.Designer.cs" holding the generated field declarations and InitializeComponent.
		/// </summary>
		public IReadOnlyList<OpenedFile> GetSourceFiles(FormsDesignerViewContent viewContent, out OpenedFile designerCodeFile)
		{
			var primaryFileName = viewContent.PrimaryFileName;
			var files = new List<OpenedFile> { viewContent.PrimaryFile };

			var designerPath = FileName.Create(Path.Combine(
				Path.GetDirectoryName(primaryFileName) ?? "",
				Path.GetFileNameWithoutExtension(primaryFileName) + ".Designer.cs"));

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
