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

		/// <summary>
		/// The inverse of <see cref="AddHandler"/> for a handler that is still empty: removes the method
		/// and returns the new source, or null when it is missing or the user has since put anything
		/// (a statement or even a comment) in it. Code the user wrote is never deleted.
		/// </summary>
		public static string? RemoveEmptyHandler(string source, string? className, string handlerName)
		{
			var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
			var classes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
			var target = classes.FirstOrDefault(c => c.Identifier.Text == className) ?? classes.FirstOrDefault();
			var method = target?.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == handlerName);
			if (method?.Body == null || method.Body.Statements.Count > 0
				|| method.Body.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)))
				return null;
			// The generated member brought one blank separator line with it; take it back too so an
			// undo leaves the file as it was rather than a line longer.
			return root.RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
		}

		/// <summary>VB counterpart of <see cref="RemoveEmptyHandler"/>.</summary>
		public static string? RemoveEmptyVisualBasicHandler(string source, string? className, string handlerName)
		{
			var root = (VBSyntax.CompilationUnitSyntax)VisualBasicSyntaxTree.ParseText(source).GetRoot();
			var classes = root.DescendantNodes().OfType<VBSyntax.ClassBlockSyntax>().ToList();
			var target = classes.FirstOrDefault(c => c.ClassStatement.Identifier.ValueText == className) ?? classes.FirstOrDefault();
			var method = target?.Members.OfType<VBSyntax.MethodBlockSyntax>().FirstOrDefault(m => m.SubOrFunctionStatement.Identifier.ValueText == handlerName);
			if (method == null || method.Statements.Count > 0
				|| method.DescendantTrivia().Any(t => t.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.CommentTrivia)))
				return null;
			return root.RemoveNode(method, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString();
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
		/// stack); otherwise the file on disk is updated. No code-behind file means nothing to do and
		/// null is returned. When this call created the method, the returned
		/// <see cref="GeneratedHandler"/> can take that creation back and redo it.
		/// </summary>
		public static HandlerResult? EnsureAndNavigate(string xamlPath, string? xamlText, string handlerName, string argsType)
		{
			var codePath = File.Exists(xamlPath + ".cs") ? xamlPath + ".cs"
				: File.Exists(xamlPath + ".vb") ? xamlPath + ".vb" : null;
			if (codePath == null)
				return null;
			try {
				var className = ClassNameOf(xamlText);
				var before = ReadSource(codePath, out var hasBom);
				var updated = AddHandlerTo(codePath, before, className, handlerName, argsType, out var line);
				if (line == 0)
					return null;
				GeneratedHandler? generated = null;
				if (updated != null) {
					WriteSource(codePath, updated, hasBom);
					generated = new GeneratedHandler(codePath, className, handlerName, argsType, before, updated);
				}
				var view = SD.FileService.OpenFile(FileName.Create(codePath));
				(view?.GetService(typeof(ITextEditor)) as ITextEditor)?.JumpTo(line, 1);
				return new HandlerResult(generated);
			} catch (Exception exception) {
				LoggingService.Warn("WPF designer: could not create event handler " + handlerName + " in " + codePath + ": " + exception.Message);
				return null;
			}
		}

		static string? AddHandlerTo(string codePath, string source, string? className, string handlerName, string argsType, out int line)
			=> codePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
				? AddVisualBasicHandler(source, className, handlerName, argsType, out line)
				: AddHandler(source, className, handlerName, argsType, out line);

		static ITextEditor? FindOpenEditor(string codePath)
		{
			var fileName = FileName.Create(codePath);
			return SD.FileService.IsOpen(fileName)
				? SD.FileService.GetOpenFile(fileName)?.GetService(typeof(ITextEditor)) as ITextEditor
				: null;
		}

		/// <summary>The current source: the open editor's buffer, else the file (keeping its UTF-8 BOM).</summary>
		static string ReadSource(string codePath, out bool hasBom)
		{
			hasBom = false;
			if (FindOpenEditor(codePath) is { } editor)
				return editor.Document.Text;
			var bytes = File.ReadAllBytes(codePath);
			hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
			return new System.Text.UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
		}

		static void WriteSource(string codePath, string text, bool hasBom)
		{
			if (FindOpenEditor(codePath) is { } editor)
				editor.Document.Text = text;
			else
				File.WriteAllText(codePath, text, new System.Text.UTF8Encoding(hasBom));
		}

		/// <summary>What <see cref="EnsureAndNavigate"/> did: <see cref="Generated"/> is set only when
		/// it created the method, not when the method already existed.</summary>
		public sealed record HandlerResult(GeneratedHandler? Generated);

		/// <summary>
		/// A code-behind method this designer created, with the means to take that back and redo it
		/// together with the XAML binding that named it, so the designer's undo is one step across both
		/// files. Reversal is exact while the file is untouched since generation; otherwise it removes the
		/// method only if it is still empty. A handler the user has started to write is left alone, which
		/// leaves an unreferenced method rather than losing code.
		/// </summary>
		public sealed class GeneratedHandler
		{
			readonly string codePath;
			readonly string? className;
			readonly string argsType;
			readonly string before;
			readonly string after;

			internal GeneratedHandler(string codePath, string? className, string handlerName, string argsType, string before, string after)
			{
				this.codePath = codePath;
				this.className = className;
				HandlerName = handlerName;
				this.argsType = argsType;
				this.before = before;
				this.after = after;
			}

			public string HandlerName { get; }

			/// <summary>Removes the method; false when it was kept because the user has written in it.</summary>
			public bool Undo() => Apply(current => current == after
				? before
				: codePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
					? RemoveEmptyVisualBasicHandler(current, className, HandlerName)
					: RemoveEmptyHandler(current, className, HandlerName));

			/// <summary>Adds the method back; a method that is already there is not duplicated.</summary>
			public bool Redo() => Apply(current => current == before
				? after
				: AddHandlerTo(codePath, current, className, HandlerName, argsType, out _));

			bool Apply(Func<string, string?> transform)
			{
				try {
					var current = ReadSource(codePath, out var hasBom);
					var updated = transform(current);
					if (updated == null)
						return false;
					WriteSource(codePath, updated, hasBom);
					return true;
				} catch (Exception exception) {
					LoggingService.Warn("WPF designer: could not update event handler " + HandlerName + " in " + codePath + ": " + exception.Message);
					return false;
				}
			}
		}
	}
}
