using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ICSharpCode.MewUIDesigner;

public sealed record MewUIElementNode(string Id, string Type, string Name, int Start, int Length,
	IReadOnlyDictionary<string, string> Properties, IReadOnlyList<MewUIElementNode> Children);

/// <summary>Roslyn backend for the strict, WinForms-style MewUI generated-code grammar.</summary>
public sealed class MewUIDocumentEditor
{
	static readonly HashSet<string> ControlTypes = new(StringComparer.Ordinal) { "StackPanel", "Grid", "DockPanel", "WrapPanel", "Canvas", "ScrollViewer", "Border", "GroupBox", "TabControl", "TabItem", "ContentControl", "Button", "Label", "TextBox", "CheckBox", "RadioButton", "Slider", "ProgressBar", "Image", "ComboBox", "ListBox", "Separator", "Menu", "MenuItem" };
	readonly List<string> undo = new(), redo = new();
	CompilationUnitSyntax root = SyntaxFactory.CompilationUnit();
	public string Text { get; private set; } = "";
	public string Error { get; private set; } = "";
	public bool CanUndo => undo.Count != 0;
	public bool CanRedo => redo.Count != 0;
	public IReadOnlyList<MewUIElementNode> Roots { get; private set; } = Array.Empty<MewUIElementNode>();
	public string WindowClassName { get; private set; } = "";
	public bool Reset(string text) { Text = text ?? ""; undo.Clear(); redo.Clear(); return Parse(); }

	bool Parse()
	{
		var tree = CSharpSyntaxTree.ParseText(Text); root = (CompilationUnitSyntax)tree.GetRoot();
		var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
		var method = InitializeMethod(); var owner = method?.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
		if (method?.Body == null || owner == null) { WindowClassName = ""; Roots = Array.Empty<MewUIElementNode>(); if (errors.Count == 0) errors.Add("A block-bodied InitializeComponent method is required."); Error = string.Join(Environment.NewLine, errors); return false; }
		WindowClassName = owner.Identifier.ValueText;
		var creations = Creations(method).ToDictionary(x => x.Name, StringComparer.Ordinal);
		var childMap = method.Body.Statements.OfType<ExpressionStatementSyntax>().Select(s => s.Expression).OfType<InvocationExpressionSyntax>()
			.Where(i => Member(i.Expression, out _, out var member) && member == "Children")
			.ToDictionary(i => ((MemberAccessExpressionSyntax)i.Expression).Expression.ToString(), i => i.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToArray(), StringComparer.Ordinal);
		var assignments = method.Body.Statements.OfType<ExpressionStatementSyntax>().Select(s => s.Expression).OfType<AssignmentExpressionSyntax>().ToArray();
		var content = assignments.FirstOrDefault(a => a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == "Content");
		var rootName = (content?.Right as IdentifierNameSyntax)?.Identifier.ValueText;
		if (rootName == null || !creations.ContainsKey(rootName)) errors.Add("Content must be assigned to a generated control field.");
		var props = assignments.Where(a => a.Left is IdentifierNameSyntax id && id.Identifier.ValueText != "Content" && !creations.ContainsKey(id.Identifier.ValueText))
			.ToDictionary(a => ((IdentifierNameSyntax)a.Left).Identifier.ValueText, a => LiteralText(a.Right), StringComparer.Ordinal);
		var children = rootName != null && creations.ContainsKey(rootName) ? new[] { BuildNode(rootName, creations, childMap, method) } : Array.Empty<MewUIElementNode>();
		Roots = new[] { new MewUIElementNode("$window", "Window", WindowClassName, owner.SpanStart, owner.Span.Length, props, children) };
		Error = string.Join(Environment.NewLine, errors); return errors.Count == 0;
	}

	static MewUIElementNode BuildNode(string name, IReadOnlyDictionary<string, Creation> creations, IReadOnlyDictionary<string, string[]> childMap, MethodDeclarationSyntax method)
	{
		var item = creations[name];
		var props = method.Body!.Statements.OfType<ExpressionStatementSyntax>().Select(s => s.Expression).OfType<AssignmentExpressionSyntax>()
			.Where(a => Member(a.Left, out var receiver, out _) && receiver == name)
			.ToDictionary(a => ((MemberAccessExpressionSyntax)a.Left).Name.Identifier.ValueText, a => LiteralText(a.Right), StringComparer.Ordinal);
		var children = childMap.TryGetValue(name, out var names) ? names.Where(creations.ContainsKey).Select(n => BuildNode(n, creations, childMap, method)).ToArray() : Array.Empty<MewUIElementNode>();
		return new(name, item.Type, name, item.Syntax.SpanStart, item.Syntax.Span.Length, props, children);
	}

	public bool SetProperty(string id, string property, string value)
	{
		if (id == "$window") return SetWindowProperty(property, value);
		var method = InitializeMethod(); if (method?.Body == null || !HasField(id)) return false;
		var old = method.Body.Statements.OfType<ExpressionStatementSyntax>().FirstOrDefault(s => s.Expression is AssignmentExpressionSyntax a && Member(a.Left, out var receiver, out var member) && receiver == id && member == property);
		if (old?.Expression is AssignmentExpressionSyntax assignment) return Commit(root.ReplaceNode(assignment.Right, Value(property, value)).NormalizeWhitespace().ToFullString());
		var statements = method.Body.Statements; var index = LastConfigurationIndex(statements, id);
		statements = statements.Insert(index + 1, Assign(Access(id, property), Value(property, value)));
		return Commit(root.ReplaceNode(method, method.WithBody(method.Body.WithStatements(statements))).NormalizeWhitespace().ToFullString());
	}

	public bool AddElement(string parentId, string type)
	{
		type = type?.Trim() ?? ""; var method = InitializeMethod();
		if (method?.Body == null || parentId == "$window" || !HasField(parentId) || !ControlTypes.Contains(type)) return false;
		if (!IsContainer(parentId)) return false;
		var name = UniqueName(type); var owner = method.Ancestors().OfType<ClassDeclarationSyntax>().First(); var methodIndex = owner.Members.IndexOf(method);
		var changedOwner = owner.WithMembers(owner.Members.Insert(methodIndex, Field(type, name)));
		var changedMethod = changedOwner.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == "InitializeComponent");
		var statements = changedMethod.Body!.Statements; var creationEnd = statements.TakeWhile(IsCreation).Count();
		statements = statements.Insert(creationEnd, Assign(SyntaxFactory.IdentifierName(name), New(type)));
		var defaultProperty = type is "Label" or "TextBox" ? "Text" : type is "Button" or "CheckBox" or "RadioButton" ? "Content" : null;
		if (defaultProperty != null) statements = statements.Insert(creationEnd + 1, Assign(Access(name, defaultProperty), Value(defaultProperty ?? "", type)));
		var relationship = statements.OfType<ExpressionStatementSyntax>().FirstOrDefault(s => s.Expression is InvocationExpressionSyntax i && Member(i.Expression, out var receiver, out var member) && receiver == parentId && member == "Children");
		if (relationship?.Expression is InvocationExpressionSyntax invocation) statements = statements.Replace(relationship, relationship.WithExpression(invocation.WithArgumentList(invocation.ArgumentList.AddArguments(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(name))))));
		else {
			var content = statements.OfType<ExpressionStatementSyntax>().FirstOrDefault(s => s.Expression is AssignmentExpressionSyntax a && a.Left.ToString() == "Content");
			if (content == null) return false;
			statements = statements.Insert(statements.IndexOf(content), Children(parentId, name));
		}
		changedMethod = changedMethod.WithBody(changedMethod.Body.WithStatements(statements));
		changedOwner = changedOwner.ReplaceNode(changedOwner.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == "InitializeComponent"), changedMethod);
		return Commit(root.ReplaceNode(owner, changedOwner).NormalizeWhitespace().ToFullString());
	}

	public bool Remove(string id)
	{
		if (id == "$window" || !HasField(id)) return false;
		var node = Flatten(Roots).FirstOrDefault(n => n.Id == id); if (node == null) return false;
		var names = Flatten(new[] { node }).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
		var owner = InitializeMethod()!.Ancestors().OfType<ClassDeclarationSyntax>().First();
		var changed = owner.RemoveNodes(owner.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Declaration.Variables.Any(v => names.Contains(v.Identifier.ValueText))), SyntaxRemoveOptions.KeepNoTrivia)!;
		var method = changed.Members.OfType<MethodDeclarationSyntax>().First(m => m.Identifier.ValueText == "InitializeComponent");
		var kept = new List<StatementSyntax>();
		foreach (var syntax in method.Body!.Statements) {
			if (syntax is not ExpressionStatementSyntax statement) { kept.Add(syntax); continue; }
			if (Owns(statement, names)) continue;
			if (statement.Expression is InvocationExpressionSyntax call && Member(call.Expression, out _, out var member) && member == "Children") {
				var args = call.ArgumentList.Arguments.Where(a => !names.Contains(a.Expression.ToString()));
				statement = statement.WithExpression(call.WithArgumentList(call.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(args))));
			}
			kept.Add(statement);
		}
		var statements = SyntaxFactory.List(kept);
		changed = changed.ReplaceNode(method, method.WithBody(method.Body.WithStatements(statements)));
		return Commit(root.ReplaceNode(owner, changed).NormalizeWhitespace().ToFullString());
	}

	public bool Rename(string id, string newName)
	{
		if (id == "$window" || !HasField(id) || !SyntaxFacts.IsValidIdentifier(newName)) return false; if (id == newName) return true;
		var owner = InitializeMethod()!.Ancestors().OfType<ClassDeclarationSyntax>().First(); if (owner.DescendantTokens().Any(t => t.ValueText == newName)) return false;
		var tokens = owner.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == id);
		return Commit(root.ReplaceNode(owner, owner.ReplaceTokens(tokens, (t, _) => SyntaxFactory.Identifier(t.LeadingTrivia, newName, t.TrailingTrivia))).NormalizeWhitespace().ToFullString());
	}

	public bool Undo() => Move(undo, redo); public bool Redo() => Move(redo, undo);
	bool Move(List<string> from, List<string> to) { if (from.Count == 0) return false; to.Add(Text); Text = from[^1]; from.RemoveAt(from.Count - 1); Parse(); return true; }
	bool Commit(string text) { undo.Add(Text); redo.Clear(); Text = text; return Parse(); }

	bool SetWindowProperty(string property, string value)
	{
		var method = InitializeMethod(); if (method?.Body == null) return false;
		var old = method.Body.Statements.OfType<ExpressionStatementSyntax>().FirstOrDefault(s => s.Expression is AssignmentExpressionSyntax a && a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == property);
		if (old?.Expression is AssignmentExpressionSyntax assignment) return Commit(root.ReplaceNode(assignment.Right, Value(property, value)).NormalizeWhitespace().ToFullString());
		var content = method.Body.Statements.OfType<ExpressionStatementSyntax>().FirstOrDefault(s => s.Expression is AssignmentExpressionSyntax a && a.Left.ToString() == "Content");
		if (content == null) return false; // no Content assignment to anchor against (document already flagged by Parse)
		var statements = method.Body.Statements.Insert(method.Body.Statements.IndexOf(content), Assign(SyntaxFactory.IdentifierName(property), Value(property, value)));
		var changed = method.WithBody(method.Body.WithStatements(statements));
		return Commit(root.ReplaceNode(method, changed).NormalizeWhitespace().ToFullString());
	}

	MethodDeclarationSyntax? InitializeMethod() => root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "InitializeComponent" && m.ParameterList.Parameters.Count == 0);
	static readonly HashSet<string> ContainerTypes = new(StringComparer.Ordinal) { "StackPanel", "Grid", "DockPanel", "WrapPanel", "Canvas", "ScrollViewer", "Border", "GroupBox", "TabControl", "TabItem", "ContentControl" };
	bool IsContainer(string name) { var creation = Creations(InitializeMethod()!).FirstOrDefault(c => c.Name == name); if (creation == null) return false; return ContainerTypes.Contains(creation.Type) || creation.Type == "Window"; }
	bool HasField(string name) => InitializeMethod()?.Ancestors().OfType<ClassDeclarationSyntax>().First().Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == name)) == true;
	string UniqueName(string type) { var prefix = char.ToLowerInvariant(type[0]) + type[1..]; var names = Flatten(Roots).Select(n => n.Name).ToHashSet(StringComparer.Ordinal); for (var i = 1; ; i++) if (!names.Contains(prefix + i)) return prefix + i; }
	static IEnumerable<Creation> Creations(MethodDeclarationSyntax method) => method.Body!.Statements.OfType<ExpressionStatementSyntax>().Select(s => s.Expression).OfType<AssignmentExpressionSyntax>().Where(a => a.Left is IdentifierNameSyntax && a.Right is ObjectCreationExpressionSyntax).Select(a => new Creation(((IdentifierNameSyntax)a.Left).Identifier.ValueText, ((ObjectCreationExpressionSyntax)a.Right).Type.ToString().Split('.').Last(), (ObjectCreationExpressionSyntax)a.Right)).Where(x => ControlTypes.Contains(x.Type));
	static bool IsCreation(StatementSyntax s) => s is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: IdentifierNameSyntax, Right: ObjectCreationExpressionSyntax } };
	static int LastConfigurationIndex(SyntaxList<StatementSyntax> statements, string name) { var result = -1; for (var i = 0; i < statements.Count; i++) if (statements[i] is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment } && assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) && (assignment.Left is IdentifierNameSyntax id && id.Identifier.ValueText == name || Member(assignment.Left, out var receiver, out _) && receiver == name)) result = i; return result; }
	static bool Owns(ExpressionStatementSyntax s, HashSet<string> names) => s.Expression switch { AssignmentExpressionSyntax a when a.Left is IdentifierNameSyntax id => names.Contains(id.Identifier.ValueText), AssignmentExpressionSyntax a when Member(a.Left, out var r, out _) => names.Contains(r), InvocationExpressionSyntax i when Member(i.Expression, out var r, out _) => names.Contains(r), _ => false };
	static FieldDeclarationSyntax Field(string type, string name) => SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(type), SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(name).WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))))))).AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));
	static ObjectCreationExpressionSyntax New(string type) => SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName(type)).WithArgumentList(SyntaxFactory.ArgumentList());
	static ExpressionStatementSyntax Assign(ExpressionSyntax left, ExpressionSyntax right) => SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, left, right));
	static ExpressionStatementSyntax Children(string parent, string child) => SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(Access(parent, "Children"), SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(child))))));
	static MemberAccessExpressionSyntax Access(string receiver, string member) => SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(receiver), SyntaxFactory.IdentifierName(member));
	static bool Member(ExpressionSyntax expression, out string receiver, out string member) { if (expression is MemberAccessExpressionSyntax a && a.Expression is IdentifierNameSyntax id) { receiver = id.Identifier.ValueText; member = a.Name.Identifier.ValueText; return true; } receiver = member = ""; return false; }
	static IEnumerable<MewUIElementNode> Flatten(IEnumerable<MewUIElementNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
	static readonly HashSet<string> NumericProperties = new(StringComparer.Ordinal) { "Width", "Height", "Spacing" };
	static ExpressionSyntax Value(string property, string value)
	{
		if (value != null && bool.TryParse(value, out var b)) return SyntaxFactory.LiteralExpression(b ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression);
		// Numbers are emitted as literals ONLY for known-numeric properties; a string property
		// like TextBox.Text must keep "123" as a string literal or the generated code stops
		// compiling (measured: Text = 123 -> CS0029).
		if (value != null && NumericProperties.Contains(property) && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
			return SyntaxFactory.ParseExpression(value);
		return SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value ?? ""));
	}

	static string LiteralText(ExpressionSyntax expression)
	{
		// LiteralExpression.Token.ValueText is the DECODED literal (escapes such as \t, \",
		// unicode escapes already applied). The previous hand-rolled Unquote only handled \" and
		// left every other escape sequence as literal backslash noise in the Properties pad.
		return expression is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax literal
			? literal.Token.ValueText
			: expression.ToString();
	}
	sealed record Creation(string Name, string Type, ObjectCreationExpressionSyntax Syntax);
}
