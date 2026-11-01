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

	/// <summary>
	/// Wires one signal of a composite template in the template's own class, because a template
	/// must not carry a &lt;signal&gt; entry: GTK's template builder resolves the handler as a native
	/// symbol and aborts (Gir.Core sets no managed BuilderScope). Adds, when missing:
	/// <c>[Gtk.Connect("id")] private Gtk.X _id;</c>, the handler method, and
	/// <c>_id.OnEvent += (_, args) =&gt; handler(_id, args);</c> in the class's <c>Initialize()</c>.
	/// Returns the 1-based line of the handler method, or null when the class file is not found.
	/// </summary>
	public static (string Path, int Line)? WireTemplateSignal(string uiPath, string className, string objectId, string objectType,
		string eventName, string handler, string signature, string defaultBody)
	{
		var codePath = TemplateClassFile(uiPath, className);
		if (codePath == null) return null;
		var source = ReadText(codePath);
		if (source == null) return null;
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var classDecl = FindClass(root, className);
		if (classDecl == null) return null;
		if (!classDecl.Modifiers.Any(SyntaxKind.PartialKeyword)) {
			source = source.Insert(classDecl.Keyword.SpanStart, "partial ");
			root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
			classDecl = FindClass(root, className);
			if (classDecl == null) return null;
		}

		var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		var classIndent = IndentOf(source, classDecl.SpanStart);
		var unit = source.Contains("\n\t", StringComparison.Ordinal) || classIndent.Contains('\t') ? "\t" : "    ";
		var memberIndent = classIndent + unit;
		var type = "global::" + objectType;
		var edits = new List<(int Position, string Text)>();

		var connectField = classDecl.Members.OfType<FieldDeclarationSyntax>().FirstOrDefault(field =>
			field.AttributeLists.SelectMany(list => list.Attributes).Any(attribute =>
				attribute.Name.ToString().EndsWith("Connect", StringComparison.Ordinal)
				&& attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
				&& literal.Token.ValueText == objectId));
		// Reuse the field the class already declares for this object, whatever it is called.
		var local = connectField?.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? TemplateLocal(objectId);
		if (connectField == null) {
			var field = $"{newline}{memberIndent}[global::Gtk.Connect(\"{objectId}\")]{newline}{memberIndent}private {type} {local};{newline}";
			edits.Add((classDecl.OpenBraceToken.Span.End, field));
		}

		var hasHandler = classDecl.Members.OfType<MethodDeclarationSyntax>().Any(method => method.Identifier.Text == handler);
		if (!hasHandler) {
			var text = new StringBuilder();
			if (classDecl.Members.Count > 0) text.Append(newline);
			text.Append(memberIndent).Append(signature).Append(newline)
				.Append(memberIndent).Append('{').Append(newline);
			if (defaultBody.Length > 0) text.Append(memberIndent).Append(unit).Append(defaultBody).Append(newline);
			text.Append(memberIndent).Append('}').Append(newline);
			edits.Add((ClassCloseInsertPosition(source, classDecl), text.ToString()));
		}

		var subscription = $"{memberIndent}{unit}{local}.{eventName} += (_, args) => {handler}({local}, args);{newline}";
		var initialize = classDecl.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(method => method.Identifier.Text == "Initialize");
		if (initialize?.Body != null) {
			// The event may already have another subscriber (the user's own); only skip when this
			// handler is the one already wired.
			if (!initialize.Body.ToString().Contains($"=> {handler}({local}", StringComparison.Ordinal))
				edits.Add((LineStartOf(source, initialize.Body.CloseBraceToken.SpanStart), subscription));
		} else {
			var text = new StringBuilder();
			if (classDecl.Members.Count > 0) text.Append(newline);
			text.Append(memberIndent).Append("partial void Initialize()").Append(newline)
				.Append(memberIndent).Append('{').Append(newline)
				.Append(subscription)
				.Append(memberIndent).Append('}').Append(newline);
			edits.Add((ClassCloseInsertPosition(source, classDecl), text.ToString()));
		}

		foreach (var edit in edits.OrderByDescending(e => e.Position))
			source = source.Insert(edit.Position, edit.Text);
		ApplyText(codePath, source);
		var signatureAt = source.IndexOf(signature, StringComparison.Ordinal);
		return signatureAt < 0 ? null : (codePath, source.Substring(0, signatureAt).Count(c => c == '\n') + 1);
	}

	/// <summary>
	/// Removes the event subscription <see cref="WireTemplateSignal"/> generated (<c>_id.OnEvent += ...;</c>)
	/// from the template class's <c>Initialize()</c>, the one whose handler is <paramref name="handler"/> (what
	/// the Events pad shows, read back by <see cref="TemplateSignals"/>). The <c>[Gtk.Connect]</c> field and
	/// the handler method stay: they are user-owned code and other members may use them. Returns false when the class was not
	/// found; true when the subscription is gone (including when there was none).
	/// </summary>
	public static bool UnwireTemplateSignal(string uiPath, string className, string objectId, string eventName, string handler)
	{
		// Without the handler the Events pad shows, nothing identifies which subscriber is ours.
		if (string.IsNullOrEmpty(handler)) return true;
		var codePath = TemplateClassFile(uiPath, className);
		if (codePath == null) return false;
		var source = ReadText(codePath);
		if (source == null) return false;
		var classDecl = FindClass(CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot(), className);
		if (classDecl == null) return false;

		var removals = new List<(int Start, int End)>();
		foreach (var found in GeneratedSubscriptions(classDecl, ConnectedLocals(classDecl))) {
			if (found.ObjectId != objectId || found.EventName != eventName || found.Handler != handler) continue;
			var statement = found.Statement;
			var start = LineStartOf(source, statement.SpanStart);
			var end = statement.Span.End;
			var newline = source.IndexOf('\n', end);
			if (start != statement.SpanStart && source.Substring(end, (newline < 0 ? source.Length : newline) - end).Trim().Length == 0)
				end = newline < 0 ? source.Length : newline + 1;
			removals.Add((start, end));
		}
		if (removals.Count == 0) return true;
		foreach (var removal in removals.OrderByDescending(r => r.Start))
			source = source.Remove(removal.Start, removal.End - removal.Start);
		ApplyText(codePath, source);
		return true;
	}

	/// <summary>The designer-shaped subscriptions of a template class, in one place so that what the Events pad
	/// shows (<see cref="TemplateSignals"/>) is exactly what a clear removes (<see cref="UnwireTemplateSignal"/>):
	/// the top-level statements of <c>Initialize()</c>, where <see cref="WireTemplateSignal"/> writes them.
	/// <paramref name="locals"/> maps a <c>[Gtk.Connect]</c> field name to its object id.</summary>
	static IEnumerable<(ExpressionStatementSyntax Statement, string ObjectId, string EventName, string Handler)> GeneratedSubscriptions(
		ClassDeclarationSyntax classDecl, IReadOnlyDictionary<string, string> locals)
	{
		foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == "Initialize" && m.Body != null)) {
			foreach (var statement in method.Body!.Statements.OfType<ExpressionStatementSyntax>()) {
				if (statement.Expression is AssignmentExpressionSyntax assignment
					&& assignment.IsKind(SyntaxKind.AddAssignmentExpression)
					&& assignment.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target } member
					&& member.Name.Identifier.ValueText.StartsWith("On", StringComparison.Ordinal)
					&& locals.TryGetValue(target.Identifier.ValueText, out var id)
					&& GeneratedHandler(assignment.Right, target.Identifier.ValueText) is { } handler)
					yield return (statement, id, member.Name.Identifier.ValueText, handler);
			}
		}
	}

	/// <summary>Reads the shape <see cref="WireTemplateSignal"/> writes, <c>(_, args) =&gt; handler(local, args)</c>,
	/// and returns its handler. Any other subscriber (another callee argument list, a one-parameter
	/// lambda, a method group) is the user's own and is neither shown in the Events pad nor removed.</summary>
	static string? GeneratedHandler(ExpressionSyntax subscriber, string local)
	{
		if (subscriber is not ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters: { Count: 2 } parameters, Body: InvocationExpressionSyntax call })
			return null;
		return call.Expression is IdentifierNameSyntax callee
			&& call.ArgumentList.Arguments is { Count: 2 } arguments
			&& arguments[0].Expression is IdentifierNameSyntax first && first.Identifier.ValueText == local
			&& arguments[1].Expression is IdentifierNameSyntax second && second.Identifier.ValueText == parameters[1].Identifier.ValueText
			? callee.Identifier.ValueText : null;
	}

	/// <summary>The template class's wired signals: (object id, Gir.Core event name) -&gt; handler.
	/// Read back from the class because a template document has no &lt;signal&gt; entries to read.</summary>
	public static IReadOnlyDictionary<(string ObjectId, string EventName), string> TemplateSignals(string uiPath, string className)
	{
		var result = new Dictionary<(string, string), string>();
		var codePath = TemplateClassFile(uiPath, className);
		if (codePath == null) return result;
		var source = ReadText(codePath);
		if (source == null) return result;
		var classDecl = FindClass(CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot(), className);
		if (classDecl == null) return result;
		foreach (var found in GeneratedSubscriptions(classDecl, ConnectedLocals(classDecl)))
			result[(found.ObjectId, found.EventName)] = found.Handler;
		return result;
	}

	/// <summary>The class's <c>[Gtk.Connect("id")]</c> fields: field name -&gt; object id.</summary>
	static Dictionary<string, string> ConnectedLocals(ClassDeclarationSyntax classDecl)
	{
		var locals = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
			foreach (var attribute in field.AttributeLists.SelectMany(list => list.Attributes))
				if (attribute.Name.ToString().EndsWith("Connect", StringComparison.Ordinal)
					&& attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal)
					foreach (var variable in field.Declaration.Variables)
						locals[variable.Identifier.ValueText] = literal.Token.ValueText;
		return locals;
	}

	/// <summary>The class file for a template: a sibling X.cs first, then any project file declaring
	/// <c>class X</c> (the template's C# class is often beside the project, not the .ui).</summary>
	static string? TemplateClassFile(string uiPath, string className)
	{
		var sibling = BehaviorPath(uiPath);
		if (File.Exists(sibling) || IsOpen(sibling)) {
			var text = ReadText(sibling);
			if (text != null && text.Contains("class " + className, StringComparison.Ordinal)) return sibling;
		}
		try {
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(uiPath));
			var directory = project?.Directory?.ToString();
			if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
			foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)) {
				var relative = file.Substring(directory.Length).TrimStart(Path.DirectorySeparatorChar, '/');
				if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
					|| relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
				var text = ReadText(file);
				if (text != null && text.Contains("class " + className, StringComparison.Ordinal)) return file;
			}
		} catch (Exception ex) {
			LoggingService.Warn("GTK designer: could not locate the template class " + className + ": " + ex.Message);
		}
		return null;
	}

	/// <summary>"runButton" -&gt; "_runButton"; never starts with a digit.</summary>
	static string TemplateLocal(string objectId)
	{
		var name = new string(objectId.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
		if (name.Length == 0 || char.IsDigit(name[0])) name = "_" + name;
		return "_" + name;
	}

	static int ClassCloseInsertPosition(string source, ClassDeclarationSyntax classDecl)
	{
		var close = classDecl.CloseBraceToken.SpanStart;
		var lineStart = source.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
		return source.Substring(lineStart, close - lineStart).Trim().Length == 0 ? lineStart : close;
	}

	static int LineStartOf(string source, int position)
	{
		var lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1)) + 1;
		return source.Substring(lineStart, position - lineStart).Trim().Length == 0 ? lineStart : position;
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
