#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using VisualBasicSyntaxTree = Microsoft.CodeAnalysis.VisualBasic.VisualBasicSyntaxTree;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Creates (or finds) the code-behind method a WPF event binding names - the in-process
	/// designer's double-click behavior, done by the IDE on the IDE-owned code-behind file. The
	/// isolated design host only writes the XAML attribute and never touches project code.
	/// </summary>
	public static class WpfCodeBehindHandlers
	{
		static readonly Regex ClassDirective = new("\\bx:Class\\s*=\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);

		/// <summary>The x:Class simple name declared by <paramref name="xamlText"/>, or null.</summary>
		public static string? ClassNameOf(string? xamlText)
		{
			var match = xamlText == null ? Match.Empty : ClassDirective.Match(xamlText);
			return match.Success ? match.Groups[1].Value.Split('.').Last() : null;
		}

		/// <summary>
		/// Adds <c>private void handlerName(object sender, argsType e) { }</c> to the class named
		/// <paramref name="className"/> (else the file's first class) unless a method with that name
		/// already exists. Returns the new source (null when nothing was added) and the 1-based line of
		/// the handler, whether added or found; line is 0 when there is no class.
		/// </summary>
		public static string? AddHandler(string source, string? className, string handlerName, string argsType, out int line)
		{
			line = 0;
			var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
			var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
			var target = classes.FirstOrDefault(c => c.Identifier.Text == className) ?? classes.FirstOrDefault();
			if (target == null)
				return null;
			var existing = target.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == handlerName);
			if (existing != null)
			{
				line = existing.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
				return null;
			}
			var method = SyntaxFactory.MethodDeclaration(
					SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
					SyntaxFactory.Identifier(handlerName))
				.AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword))
				.AddParameterListParameters(
					SyntaxFactory.Parameter(SyntaxFactory.Identifier("sender")).WithType(SyntaxFactory.ParseTypeName("object")),
					SyntaxFactory.Parameter(SyntaxFactory.Identifier("e")).WithType(SyntaxFactory.ParseTypeName(argsType)))
				.WithBody(SyntaxFactory.Block())
				.WithAdditionalAnnotations(Formatter.Annotation);
			var updated = root.ReplaceNode(target, target.AddMembers(method));
			// Format only the inserted method, so the rest of the user's file keeps its layout.
			var formatted = Formatter.Format(updated, Formatter.Annotation, new AdhocWorkspace());
			var inserted = formatted.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == handlerName);
			line = inserted != null ? inserted.GetLocation().GetLineSpan().StartLinePosition.Line + 1 : 0;
			return formatted.ToFullString();
		}

		/// <summary>VB counterpart of <see cref="AddHandler"/>. The IDE owns this source edit just
		/// as it owns the C# edit; the isolated designer still only supplies the XAML attribute.</summary>
		public static string? AddVisualBasicHandler(string source, string? className, string handlerName, string argsType, out int line)
		{
			line = 0;
			var root = (VBSyntax.CompilationUnitSyntax)VisualBasicSyntaxTree.ParseText(source).GetRoot();
			var classes = root.DescendantNodes().OfType<VBSyntax.ClassBlockSyntax>().ToList();
			var target = classes.FirstOrDefault(c => c.ClassStatement.Identifier.ValueText == className) ?? classes.FirstOrDefault();
			if (target == null)
				return null;
			var existing = target.Members.OfType<VBSyntax.MethodBlockSyntax>().FirstOrDefault(m => m.SubOrFunctionStatement.Identifier.ValueText == handlerName);
			if (existing != null) {
				line = existing.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
				return null;
			}
			var typeName = ToVisualBasicTypeName(argsType);
			var member = VisualBasicSyntaxTree.ParseText("Class Placeholder\n    Private Sub " + handlerName + "(sender As Object, e As " + typeName + ")\n    End Sub\nEnd Class")
				.GetRoot().DescendantNodes().OfType<VBSyntax.ClassBlockSyntax>().Single().Members.Single()
				.WithAdditionalAnnotations(Formatter.Annotation);
			var updated = root.ReplaceNode(target, target.WithMembers(target.Members.Add(member)));
			var formatted = Formatter.Format(updated, Formatter.Annotation, new AdhocWorkspace());
			var inserted = formatted.DescendantNodes().OfType<VBSyntax.MethodBlockSyntax>().FirstOrDefault(m => m.SubOrFunctionStatement.Identifier.ValueText == handlerName);
			line = inserted != null ? inserted.GetLocation().GetLineSpan().StartLinePosition.Line + 1 : 0;
			return formatted.ToFullString();
		}

		static string ToVisualBasicTypeName(string typeName)
		{
			typeName = typeName.Replace("global::", "Global.", StringComparison.Ordinal);
			var open = typeName.IndexOf('<');
			if (open < 0 || !typeName.EndsWith('>'))
				return typeName;
			return typeName[..open] + "(Of " + typeName[(open + 1)..^1] + ")";
		}

		/// <summary>
		/// Ensures the handler exists in <c>xamlPath + ".cs"</c> and opens it at the method. The open
		/// editor's buffer is authoritative when the file is already open (the edit lands on its undo
		/// stack); otherwise the file on disk is updated. No code-behind file means nothing to do.
		/// </summary>
		public static bool EnsureAndNavigate(string xamlPath, string? xamlText, string handlerName, string argsType)
		{
			var codePath = File.Exists(xamlPath + ".cs") ? xamlPath + ".cs"
				: File.Exists(xamlPath + ".vb") ? xamlPath + ".vb" : null;
			if (codePath == null)
				return false;
			try {
				var fileName = FileName.Create(codePath);
				var openEditor = SD.FileService.IsOpen(fileName)
					? SD.FileService.GetOpenFile(fileName)?.GetService(typeof(ITextEditor)) as ITextEditor
					: null;
				// A closed file is rewritten in place: keep its UTF-8 byte order mark as found.
				var bytes = openEditor == null ? File.ReadAllBytes(codePath) : Array.Empty<byte>();
				var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
				var source = openEditor?.Document.Text ?? new System.Text.UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
				var updated = codePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
					? AddVisualBasicHandler(source, ClassNameOf(xamlText), handlerName, argsType, out var line)
					: AddHandler(source, ClassNameOf(xamlText), handlerName, argsType, out line);
				if (line == 0)
					return false;
				if (updated != null) {
					if (openEditor != null)
						openEditor.Document.Text = updated;
					else
						File.WriteAllText(codePath, updated, new System.Text.UTF8Encoding(hasBom));
				}
				var view = SD.FileService.OpenFile(fileName);
				(view?.GetService(typeof(ITextEditor)) as ITextEditor)?.JumpTo(line, 1);
				return true;
			} catch (Exception exception) {
				LoggingService.Warn("WPF designer: could not create event handler " + handlerName + " in " + codePath + ": " + exception.Message);
				return false;
			}
		}
	}
}
