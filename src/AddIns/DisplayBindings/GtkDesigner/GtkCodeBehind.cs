// The GTK designer's two C# files beside a .ui (doc/technotes/gtk-designer.md, "File convention"):
//   X.cs     user-owned behavior: a partial class holding the signal handler methods;
//   X.ui.cs  designer-owned companion: BuildUi(path), which loads X.ui and connects its <signal>s
//            to those methods (see GtkSignalWiring for why GtkBuilder cannot do that itself).
// Handler methods are located and inserted with Roslyn so the edit lands in the right class
// whatever the file's formatting; the inserted text follows the file's own indentation. A file
// that is open in the IDE is edited through its editor buffer, which stays authoritative.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ICSharpCode.GtkDesigner;

static class GtkCodeBehind
{
	public static string BehaviorPath(string uiPath) => Path.ChangeExtension(uiPath, ".cs");
	public static string CompanionPath(string uiPath) => uiPath + ".cs";

	/// <summary>The behavior class for <paramref name="uiPath"/>, creating X.cs (an empty partial
	/// class named after the file, in the project's root namespace) when there is none, and making
	/// an existing class partial when it is not. Null when no class can be found or created.</summary>
	public static (string? Namespace, string ClassName)? EnsureBehaviorClass(string uiPath)
	{
		var codePath = BehaviorPath(uiPath);
		var className = Path.GetFileNameWithoutExtension(uiPath);
		if (!GtkSignalWiring.IsIdentifier(className))
			return null;
		if (!File.Exists(codePath) && !IsOpen(codePath)) {
			var ns = RootNamespace(uiPath);
			var text = new StringBuilder();
			if (!string.IsNullOrEmpty(ns)) text.Append("namespace ").Append(ns).Append(";\n\n");
			text.Append("/// <summary>Behavior for ").Append(Path.GetFileName(uiPath))
				.Append(": the handlers for its signals. The designer connects them in ")
				.Append(Path.GetFileName(CompanionPath(uiPath))).Append(" (BuildUi).</summary>\n")
				.Append("public partial class ").Append(className).Append("\n{\n}\n");
			ApplyText(codePath, text.ToString());
			return (ns, className);
		}
		var source = ReadText(codePath);
		if (source == null) return null;
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var classDecl = FindClass(root, className);
		if (classDecl == null) return null;
		if (!classDecl.Modifiers.Any(SyntaxKind.PartialKeyword)) {
			// The companion is the other half of the class: it must be partial.
			source = source.Insert(classDecl.Keyword.SpanStart, "partial ");
			ApplyText(codePath, source);
		}
		var namespaceName = classDecl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
		return (namespaceName, classDecl.Identifier.Text);
	}

	/// <summary>Adds each supported handler the behavior class does not yet have, with its typed
	/// signature and an empty body. Returns the 1-based line of the first method added, or null.</summary>
	public static int? AddMissingHandlers(string uiPath, string className, IEnumerable<GtkSignalHandlerInfo> handlers)
	{
		var codePath = BehaviorPath(uiPath);
		var source = ReadText(codePath);
		if (source == null) return null;
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var classDecl = FindClass(root, className);
		if (classDecl == null) return null;
		var existing = new HashSet<string>(classDecl.Members.OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text), StringComparer.Ordinal);
		var missing = handlers.Where(h => h.Supported && existing.Add(h.Handler)).ToList();
		if (missing.Count == 0) return null;

		var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var classIndent = IndentOf(source, classDecl.SpanStart);
		var unit = source.Contains("\n\t", StringComparison.Ordinal) || classIndent.Contains('\t') ? "\t" : "    ";
		var memberIndent = classIndent + unit;
		var text = new StringBuilder();
		var leadingBlank = classDecl.Members.Count > 0;
		foreach (var h in missing) {
			// A blank line separates methods (and the first one from existing members).
			if (leadingBlank || text.Length > 0) text.Append(newline);
			// The signature carries the return type: "bool window_close_request(Gtk.Window sender, System.EventArgs args)".
			text.Append(memberIndent).Append(h.Signature).Append(newline)
				.Append(memberIndent).Append('{').Append(newline);
			if (h.DefaultBody.Length > 0) text.Append(memberIndent).Append(unit).Append(h.DefaultBody).Append(newline);
			text.Append(memberIndent).Append('}').Append(newline);
		}
		// Insert at the start of the line holding the class's closing brace.
		var close = classDecl.CloseBraceToken.SpanStart;
		var lineStart = source.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
		var insertAt = source.Substring(lineStart, close - lineStart).Trim().Length == 0 ? lineStart : close;
		var updated = source.Insert(insertAt, text.ToString());
		ApplyText(codePath, updated);
		// The first method's signature line: the insertion line, after the separating blank line.
		return source.Substring(0, insertAt).Count(c => c == '\n') + 1 + (leadingBlank ? 1 : 0);
	}

	/// <summary>Writes the companion X.ui.cs when its content changed.</summary>
	public static void WriteCompanion(string uiPath, string source)
	{
		var path = CompanionPath(uiPath);
		if (ReadText(path) == source) return;
		ApplyText(path, source);
	}

	static ClassDeclarationSyntax? FindClass(CompilationUnitSyntax root, string className)
		=> root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className)
			?? root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();

	static string IndentOf(string text, int position)
	{
		var lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
		var end = lineStart; while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
		return text.Substring(lineStart, end - lineStart);
	}

	static string? RootNamespace(string uiPath)
	{
		try {
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(uiPath));
			var ns = project?.RootNamespace;
			return string.IsNullOrWhiteSpace(ns) ? null : ns;
		} catch (Exception ex) {
			LoggingService.Warn("GTK designer: could not determine the root namespace for " + uiPath + ": " + ex.Message);
			return null;
		}
	}

	static bool IsOpen(string path) => SD.FileService.IsOpen(FileName.Create(path));

	static string? ReadText(string path)
	{
		var fileName = FileName.Create(path);
		if (SD.FileService.IsOpen(fileName) && SD.FileService.GetOpenFile(fileName)?.GetService(typeof(ITextEditor)) is ITextEditor editor)
			return editor.Document.Text;
		try { return File.Exists(path) ? File.ReadAllText(path) : null; }
		catch (Exception ex) { LoggingService.Warn("GTK designer: could not read " + path + ": " + ex.Message); return null; }
	}

	static void ApplyText(string path, string text)
	{
		var fileName = FileName.Create(path);
		if (SD.FileService.IsOpen(fileName) && SD.FileService.GetOpenFile(fileName)?.GetService(typeof(ITextEditor)) is ITextEditor editor) {
			editor.Document.Text = text;
			return;
		}
		File.WriteAllText(path, text);
	}
}
