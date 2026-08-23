using System;
using System.IO;
using System.Linq;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using ICSharpCode.SharpDevelop.Workbench;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ICSharpCode.MewUIDesigner;

public sealed class MewUIDesignerDisplayBinding : ISecondaryDisplayBinding
{
	public bool ReattachWhenParserServiceIsReady => true;
	public bool CanAttachTo(IViewContent content)
	{
		if (!content.PrimaryFileName.ToString().EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
		var path = content.PrimaryFileName.ToString();
		if (path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)) return false;
		var text = content.GetService<ITextEditor>()?.Document.Text;
		var designerPath = DesignerPath(path);
		if (string.IsNullOrEmpty(text) || !File.Exists(designerPath)) return false;
		try { return IsDesignable(text, File.ReadAllText(designerPath)); } catch (IOException) { return false; }
	}
	public IViewContent[] CreateSecondaryViewContent(IViewContent content) =>
		content.SecondaryViewContents.Any(v => v is MewUIDesignerViewContent)
			? Array.Empty<IViewContent>() : new IViewContent[] { new MewUIDesignerViewContent(content.PrimaryFile) };

	public static string DesignerPath(string primaryPath) => Path.Combine(Path.GetDirectoryName(primaryPath) ?? "",
		Path.GetFileNameWithoutExtension(primaryPath) + ".Designer.cs");
	public static bool IsDesignable(string primaryText, string designerText)
	{
		var primaryClasses = CSharpSyntaxTree.ParseText(primaryText).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>();
		var designerClasses = CSharpSyntaxTree.ParseText(designerText).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().ToArray();
		return primaryClasses.Any(c => c.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))
			&& c.BaseList?.Types.Any(t => t.Type.ToString().Split('.').Last() == "Window") == true
			&& c.Members.OfType<ConstructorDeclarationSyntax>().Any(ctor => ctor.Body?.DescendantNodes().OfType<InvocationExpressionSyntax>()
				.Any(i => i.Expression.ToString() == "InitializeComponent") == true)
			&& designerClasses.Any(d => d.Identifier.ValueText == c.Identifier.ValueText
				&& d.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))
				&& d.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "InitializeComponent")));
	}
}
