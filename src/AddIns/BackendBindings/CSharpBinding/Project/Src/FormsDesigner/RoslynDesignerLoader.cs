// A Roslyn-based replacement for the excluded (NRefactory-based) CSharpDesignerLoader - see
// RoslynFormsDesignerLoaderProvider.cs's own doc comment for why this exists as a new file
// rather than un-excluding the old one.
//
// Scope: this translates only the small subset of C# statement/expression shapes that
// WinForms-designer-generated InitializeComponent methods actually use (field/property
// assignment, object creation, method invocation, event +=, primitive literals, enum member
// access) - not general C#. Anything else throws FormsDesignerLoadException with the
// unsupported construct, rather than silently producing wrong CodeDom.

using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using ICSharpCode.Core;
using ICSharpCode.FormsDesigner;
using ICSharpCode.SharpDevelop;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CSharp;

namespace CSharpBinding.FormsDesigner
{
	public class RoslynDesignerLoader : AbstractCodeDomDesignerLoader
	{
		readonly FormsDesignerViewContent viewContent;
		readonly CodeDomProvider codeDomProvider = new CSharpCodeProvider();

		string designerClassName;
		SyntaxTree lastParsedTree;

		public RoslynDesignerLoader(FormsDesignerViewContent viewContent)
		{
			this.viewContent = viewContent;
		}

		protected override CodeDomProvider CodeDomProvider => codeDomProvider;

		protected override bool IsReloadNeeded()
		{
			return base.IsReloadNeeded();
		}

		(SyntaxTree tree, SemanticModel model, ClassDeclarationSyntax designerClass, MethodDeclarationSyntax initializeComponent) ParseDesignerFile()
		{
			var path = viewContent.DesignerCodeFile.FileName;
			var text = viewContent.DesignerCodeFileDocument.Text;
			var tree = CSharpSyntaxTree.ParseText(text, path: path);
			var root = tree.GetCompilationUnitRoot();

			// The InitializeComponent method can be declared in either partial-class part -
			// search this file first (the common case, when it's the Designer.cs part), and if
			// not found there, fall back to the primary file (matches a Form with no separate
			// Designer.cs at all, like this project's own minimal sample).
			var classDecl = FindClassWithMethod(root, "InitializeComponent", out var method);
			if (classDecl == null && viewContent.DesignerCodeFile != viewContent.PrimaryFile) {
				var primaryTree = CSharpSyntaxTree.ParseText(viewContent.PrimaryFileContent.Text, path: viewContent.PrimaryFileName);
				classDecl = FindClassWithMethod(primaryTree.GetCompilationUnitRoot(), "InitializeComponent", out method);
				tree = primaryTree;
				root = primaryTree.GetCompilationUnitRoot();
			}
			if (classDecl == null)
				throw new FormsDesignerLoadException("The InitializeComponent method was not found. Designer cannot be loaded.");

			var compilation = CSharpCompilation.Create("DesignerParse")
				.AddReferences(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
				.AddSyntaxTrees(tree);
			var model = compilation.GetSemanticModel(tree);

			designerClassName = classDecl.Identifier.Text;
			return (tree, model, classDecl, method);
		}

		static ClassDeclarationSyntax FindClassWithMethod(SyntaxNode root, string methodName, out MethodDeclarationSyntax method)
		{
			foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>()) {
				var m = classDecl.Members.OfType<MethodDeclarationSyntax>()
					.FirstOrDefault(md => md.Identifier.Text == methodName && md.ParameterList.Parameters.Count == 0);
				if (m != null) {
					method = m;
					return classDecl;
				}
			}
			method = null;
			return null;
		}

		protected override CodeCompileUnit Parse()
		{
			SD.Log.Debug("RoslynDesignerLoader.Parse()");

			var (tree, model, classDecl, initializeComponent) = ParseDesignerFile();
			lastParsedTree = tree;

			var namespaceName = GetNamespace(classDecl);
			var codeClass = new CodeTypeDeclaration(classDecl.Identifier.Text) { Attributes = MemberAttributes.Public };

			// The classic split convention declares the base type in Foo.cs but
			// InitializeComponent in Foo.Designer.cs - classDecl (found via ParseDesignerFile,
			// which prefers the file that actually HAS InitializeComponent) may be the part with
			// no BaseList at all. Fall back to the primary file's matching partial part.
			var baseListSource = classDecl;
			var baseListModel = model;
			if (classDecl.BaseList == null && viewContent.DesignerCodeFile != viewContent.PrimaryFile) {
				var primaryTree = CSharpSyntaxTree.ParseText(viewContent.PrimaryFileContent.Text, path: viewContent.PrimaryFileName);
				var primaryClassDecl = primaryTree.GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
					.FirstOrDefault(c => c.Identifier.Text == classDecl.Identifier.Text && c.BaseList != null);
				if (primaryClassDecl != null) {
					var primaryCompilation = CSharpCompilation.Create("DesignerParseBase")
						.AddReferences(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
						.AddSyntaxTrees(primaryTree);
					baseListSource = primaryClassDecl;
					baseListModel = primaryCompilation.GetSemanticModel(primaryTree);
				}
			}
			foreach (var baseType in baseListSource.BaseList?.Types ?? Enumerable.Empty<BaseTypeSyntax>()) {
				var symbol = baseListModel.GetTypeInfo(baseType.Type).Type;
				codeClass.BaseTypes.Add(new CodeTypeReference(symbol?.ToDisplayString() ?? baseType.Type.ToString()));
			}

			foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>()) {
				var typeName = model.GetTypeInfo(field.Declaration.Type).Type?.ToDisplayString()
					?? field.Declaration.Type.ToString();
				foreach (var variable in field.Declaration.Variables) {
					var codeField = new CodeMemberField(typeName, variable.Identifier.Text) {
						Attributes = GetAccessibility(field.Modifiers)
					};
					codeClass.Members.Add(codeField);
				}
			}

			var codeMethod = new CodeMemberMethod { Name = "InitializeComponent", Attributes = MemberAttributes.Private };
			var translator = new RoslynToCodeDomTranslator(model);
			foreach (var statement in initializeComponent.Body.Statements) {
				foreach (var codeStatement in translator.TranslateStatement(statement))
					codeMethod.Statements.Add(codeStatement);
			}
			codeClass.Members.Add(codeMethod);

			var codeNamespace = new CodeNamespace(namespaceName);
			codeNamespace.Types.Add(codeClass);
			var unit = new CodeCompileUnit();
			unit.Namespaces.Add(codeNamespace);

			LoggingService.Debug("RoslynDesignerLoader.Parse() finished");
			return unit;
		}

		static string GetNamespace(SyntaxNode node)
		{
			for (var current = node.Parent; current != null; current = current.Parent) {
				if (current is BaseNamespaceDeclarationSyntax ns)
					return ns.Name.ToString();
			}
			return string.Empty;
		}

		static MemberAttributes GetAccessibility(SyntaxTokenList modifiers)
		{
			if (modifiers.Any(SyntaxKind.PublicKeyword)) return MemberAttributes.Public;
			if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return MemberAttributes.Family;
			if (modifiers.Any(SyntaxKind.InternalKeyword)) return MemberAttributes.Assembly;
			return MemberAttributes.Private;
		}

		protected override void Write(CodeCompileUnit unit)
		{
			LoggingService.Info("RoslynDesignerLoader.Write called");
			try {
				RewriteDesignerFile(unit);
			} catch (Exception ex) {
				SD.AnalyticsMonitor.TrackException(ex);
				MessageService.ShowException(ex);
			}
		}

		void RewriteDesignerFile(CodeCompileUnit unit)
		{
			var codeClass = unit.Namespaces[0].Types[0];
			var codeMethod = codeClass.Members.OfType<CodeMemberMethod>().Single(m => m.Name == "InitializeComponent");
			var codeFields = codeClass.Members.OfType<CodeMemberField>().ToList();

			string newMethodBody;
			using (var writer = new StringWriter()) {
				CodeDomProvider.GenerateCodeFromMember(codeMethod, writer, new CodeGeneratorOptions { IndentString = "\t\t\t", BracingStyle = "C" });
				newMethodBody = writer.ToString();
			}

			var (tree, _, classDecl, initializeComponent) = ParseDesignerFile();
			var root = tree.GetCompilationUnitRoot();

			var newMethodNode = SyntaxFactory.ParseMemberDeclaration(newMethodBody);
			if (newMethodNode == null)
				throw new FormsDesignerLoadException("Failed to regenerate InitializeComponent source from the designer's CodeDom.");

			var editedRoot = root.ReplaceNode(initializeComponent, newMethodNode.WithLeadingTrivia(initializeComponent.GetLeadingTrivia()).WithTrailingTrivia(initializeComponent.GetTrailingTrivia()));

			// Re-anchor field insertion against the edited tree's own copy of classDecl (still
			// valid: ReplaceNode only touched the method, not the class's other descendants).
			var editedClassDecl = editedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
				.First(c => c.Identifier.Text == classDecl.Identifier.Text);
			var existingFieldNames = editedClassDecl.Members.OfType<FieldDeclarationSyntax>()
				.SelectMany(f => f.Declaration.Variables)
				.Select(v => v.Identifier.Text)
				.ToHashSet();

			var newFieldNodes = codeFields
				.Where(f => !existingFieldNames.Contains(f.Name))
				.Select(f => SyntaxFactory.ParseMemberDeclaration($"private {f.Type.BaseType} {f.Name};\n"))
				.Where(n => n != null)
				.ToArray();

			if (newFieldNodes.Length > 0) {
				var finalClassDecl = editedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>()
					.First(c => c.Identifier.Text == classDecl.Identifier.Text);
				var withNewFields = finalClassDecl.AddMembers(newFieldNodes);
				editedRoot = editedRoot.ReplaceNode(finalClassDecl, withNewFields);
			}

			var newText = editedRoot.NormalizeWhitespace().ToFullString();
			File.WriteAllText(viewContent.DesignerCodeFile.FileName, newText);
			viewContent.DesignerCodeFileContent = newText;
		}

		// GetCurrentLocalizationModelFromDesignedFile: not overridden - the base class's own
		// default (CodeDomLocalizationModel.None) is exactly what this translator would return
		// anyway, and referencing that type here for no reason kept hitting resolution issues
		// specific to this project - see git history if this ever needs a real implementation.
	}
}
