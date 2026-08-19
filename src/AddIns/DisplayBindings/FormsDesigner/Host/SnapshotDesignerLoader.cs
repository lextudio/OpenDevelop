using ICSharpCode.SharpDevelop.Designer.Remote;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using Vb = Microsoft.CodeAnalysis.VisualBasic;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.Windows.Forms;
using System.Xml.Linq;
using System.Drawing;

namespace ICSharpCode.FormsDesigner.Host;

/// <summary>First child-owned Roslyn load path; intentionally source-in/memory-only.
/// Handles both the C# and the Visual Basic designer-file dialects.</summary>
sealed class SnapshotDesignerLoader : BasicDesignerLoader
{
	readonly DesignerDocumentSnapshot snapshot;
	readonly Func<string, Type?> projectTypeResolver;
	readonly Dictionary<string, IComponent> components = new(StringComparer.Ordinal);
	readonly Dictionary<string, object> resources = new(StringComparer.Ordinal);

	public SnapshotDesignerLoader(DesignerDocumentSnapshot snapshot, Func<string, Type?> projectTypeResolver)
	{
		this.snapshot = snapshot;
		this.projectTypeResolver = projectTypeResolver;
	}

	public bool IsVisualBasic => snapshot.Language.Equals("VisualBasic", StringComparison.OrdinalIgnoreCase)
		|| snapshot.DesignerFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)
		|| snapshot.PrimaryFileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase);

	protected override void PerformLoad(IDesignerSerializationManager serializationManager)
	{
		LoadResources();
		var source = snapshot.Files.FirstOrDefault(f => f.Kind.Equals("Designer", StringComparison.OrdinalIgnoreCase))?.Text
			?? snapshot.Files.First().Text;
		if (IsVisualBasic) {
			PerformLoadVisualBasic(source);
			return;
		}
		var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
		var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
			.FirstOrDefault(m => m.Identifier.ValueText == "InitializeComponent")
			?? throw new InvalidOperationException("InitializeComponent was not found.");
		var className = method.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText;
		SetBaseComponentClassName(className);
		var form = LoaderHost.CreateComponent(typeof(Form), className);
		components["this"] = form;
		components[className] = form;
		foreach (var statement in method.Body?.Statements ?? default)
			Execute(statement);
	}

	void PerformLoadVisualBasic(string source)
	{
		var root = (VbSyntax.CompilationUnitSyntax)Vb.VisualBasicSyntaxTree.ParseText(source).GetRoot();
		var method = root.DescendantNodes().OfType<VbSyntax.MethodBlockSyntax>()
			.FirstOrDefault(m => m.BlockStatement is VbSyntax.MethodStatementSyntax ms
				&& ms.DeclarationKeyword.IsKind(Vb.SyntaxKind.SubKeyword)
				&& ms.Identifier.ValueText == "InitializeComponent")
			?? throw new InvalidOperationException("InitializeComponent was not found.");
		var className = method.Ancestors().OfType<VbSyntax.ClassBlockSyntax>().First().BlockStatement.Identifier.ValueText;
		SetBaseComponentClassName(className);
		var form = LoaderHost.CreateComponent(typeof(Form), className);
		components["this"] = form;
		components[className] = form;
		foreach (var statement in method.Statements)
			ExecuteVisualBasic(statement);
	}

	void Execute(StatementSyntax statement)
	{
		if (statement is not ExpressionStatementSyntax expression) return;
		if (expression.Expression is AssignmentExpressionSyntax assignment) {
			var target = StripThis(assignment.Left.ToString());
			if (!target.Contains('.') && assignment.Right is ObjectCreationExpressionSyntax creation) {
				var type = ResolveType(creation.Type.ToString());
				if (type != null && typeof(IComponent).IsAssignableFrom(type))
					components[target] = LoaderHost.CreateComponent(type, target);
				return;
			}
			var separator = target.LastIndexOf('.');
			var owner = separator < 0 ? components["this"] : ResolveObject(target[..separator]);
			var name = separator < 0 ? target : target[(separator + 1)..];
			var property = owner == null ? null : TypeDescriptor.GetProperties(owner)[name];
			if (property != null && !property.IsReadOnly) {
				var value = Evaluate(assignment.Right);
				if (value != null) property.SetValue(owner, ConvertValue(value, property.PropertyType));
			}
			return;
		}
		if (expression.Expression is InvocationExpressionSyntax invocation
			&& invocation.Expression is MemberAccessExpressionSyntax member
			&& member.Name.Identifier.ValueText == "ApplyResources") {
			var target = Evaluate(invocation.ArgumentList.Arguments[0].Expression) as IComponent;
			var key = Evaluate(invocation.ArgumentList.Arguments[1].Expression) as string;
			if (target != null && key != null) ApplyResources(target, key);
			return;
		}
		if (expression.Expression is InvocationExpressionSyntax invocationAdd
			&& invocationAdd.Expression is MemberAccessExpressionSyntax memberAdd
			&& memberAdd.Name.Identifier.ValueText == "Add") {
			var target = StripThis(memberAdd.Expression.ToString());
			if (!target.EndsWith("Controls", StringComparison.Ordinal)) return;
			var parentName = target == "Controls" ? "this" : target[..^".Controls".Length];
			var parent = ResolveObject(parentName) as Control;
			var child = Evaluate(invocationAdd.ArgumentList.Arguments[0].Expression) as Control;
			if (parent != null && child != null) parent.Controls.Add(child);
		}
	}

	void ExecuteVisualBasic(VbSyntax.StatementSyntax statement)
	{
		if (statement is VbSyntax.AssignmentStatementSyntax assignment) {
			var target = StripMe(assignment.Left.ToString());
			if (!target.Contains('.') && assignment.Right is VbSyntax.ObjectCreationExpressionSyntax creation) {
				var type = ResolveType(creation.Type.ToString());
				if (type != null && typeof(IComponent).IsAssignableFrom(type))
					components[target] = LoaderHost.CreateComponent(type, target);
				return;
			}
			var separator = target.LastIndexOf('.');
			var owner = separator < 0 ? components["this"] : ResolveObject(target[..separator]);
			var name = separator < 0 ? target : target[(separator + 1)..];
			var property = owner == null ? null : TypeDescriptor.GetProperties(owner)[name];
			if (property != null && !property.IsReadOnly) {
				var value = EvaluateVisualBasic(assignment.Right);
				if (value != null) property.SetValue(owner, ConvertValue(value, property.PropertyType));
			}
			return;
		}
		if (statement is not VbSyntax.ExpressionStatementSyntax expression) return;
		if (expression.Expression is VbSyntax.InvocationExpressionSyntax invocation
			&& invocation.Expression is VbSyntax.MemberAccessExpressionSyntax member) {
			if (member.Name.Identifier.ValueText == "ApplyResources") {
				var target = EvaluateVisualBasic(VbArgument(invocation, 0)) as IComponent;
				var key = EvaluateVisualBasic(VbArgument(invocation, 1)) as string;
				if (target != null && key != null) ApplyResources(target, key);
				return;
			}
			if (member.Name.Identifier.ValueText == "Add") {
				var target = StripMe(member.Expression.ToString());
				if (!target.EndsWith("Controls", StringComparison.Ordinal)) return;
				var parentName = target == "Controls" ? "this" : target[..^".Controls".Length];
				var parent = ResolveObject(parentName) as Control;
				var child = EvaluateVisualBasic(VbArgument(invocation, 0)) as Control;
				if (parent != null && child != null) parent.Controls.Add(child);
			}
		}
	}

	void LoadResources()
	{
		foreach (var file in snapshot.Files.Where(item => item.Kind.Equals("Resource", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrEmpty(item.Base64))) {
			try {
				using var stream = new MemoryStream(Convert.FromBase64String(file.Base64));
				var document = XDocument.Load(stream);
				foreach (var data in document.Root?.Elements("data") ?? []) {
					var name = (string?)data.Attribute("name");
					var value = data.Element("value")?.Value;
					if (name == null || value == null) continue;
					var mimeType = (string?)data.Attribute("mimetype") ?? "";
					var typeName = (string?)data.Attribute("type") ?? "";
					if (mimeType.Contains("base64", StringComparison.OrdinalIgnoreCase)
						&& (typeName.Contains("Image", StringComparison.OrdinalIgnoreCase)
							|| typeName.Contains("Bitmap", StringComparison.OrdinalIgnoreCase))) {
						var imageStream = new MemoryStream(Convert.FromBase64String(value));
						resources[name] = Image.FromStream(imageStream);
					} else resources[name] = value;
				}
			} catch { }
		}
	}

	void ApplyResources(IComponent component, string key)
	{
		foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component)) {
			if (property.IsReadOnly || !resources.TryGetValue(key + "." + property.Name, out var value)) continue;
			try {
				if (property.PropertyType.IsInstanceOfType(value)) property.SetValue(component, value);
				else if (value is string text && property.Converter.CanConvertFrom(typeof(string))) property.SetValue(component, property.Converter.ConvertFromInvariantString(text));
			} catch { }
		}
	}

	object? Evaluate(ExpressionSyntax expression) => expression switch {
		LiteralExpressionSyntax literal => literal.Token.Value,
		IdentifierNameSyntax identifier => ResolveObject(identifier.Identifier.ValueText),
		ThisExpressionSyntax => components["this"],
		MemberAccessExpressionSyntax access when access.Expression is ThisExpressionSyntax => ResolveObject(access.Name.Identifier.ValueText),
		MemberAccessExpressionSyntax access => EvaluateMember(access),
		ObjectCreationExpressionSyntax creation => CreateValue(creation),
		CastExpressionSyntax cast => Evaluate(cast.Expression),
		InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax member
			&& member.Name.Identifier.ValueText == "GetObject" && invocation.ArgumentList.Arguments.Count == 1
			=> Evaluate(invocation.ArgumentList.Arguments[0].Expression) is string key && resources.TryGetValue(key, out var resource) ? resource : null,
		InvocationExpressionSyntax invocation when invocation.Expression is IdentifierNameSyntax identifier
			&& identifier.Identifier.ValueText == "nameof" && invocation.ArgumentList.Arguments.Count == 1
			=> NameOf(invocation.ArgumentList.Arguments[0].Expression),
		PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.UnaryMinusExpression) => Negate(Evaluate(unary.Operand)),
		_ => null
	};

	object? EvaluateVisualBasic(VbSyntax.ExpressionSyntax expression) => expression switch {
		VbSyntax.LiteralExpressionSyntax literal => literal.Token.Value,
		VbSyntax.IdentifierNameSyntax identifier => ResolveObject(identifier.Identifier.ValueText),
		VbSyntax.MeExpressionSyntax => components["this"],
		VbSyntax.MemberAccessExpressionSyntax access when access.Expression is VbSyntax.MeExpressionSyntax => ResolveObject(access.Name.Identifier.ValueText),
		VbSyntax.MemberAccessExpressionSyntax access => EvaluateMemberVisualBasic(access),
		VbSyntax.ObjectCreationExpressionSyntax creation => CreateValueVisualBasic(creation),
		VbSyntax.CTypeExpressionSyntax cast => EvaluateVisualBasic(cast.Expression),
		VbSyntax.GetTypeExpressionSyntax getType => ResolveType(getType.Type.ToString()),
		VbSyntax.InvocationExpressionSyntax invocation when invocation.Expression is VbSyntax.MemberAccessExpressionSyntax member
			&& member.Name.Identifier.ValueText == "GetObject" && invocation.ArgumentList.Arguments.Count == 1
			=> EvaluateVisualBasic(VbArgument(invocation, 0)) is string key && resources.TryGetValue(key, out var resource) ? resource : null,
		VbSyntax.InvocationExpressionSyntax invocation when invocation.Expression is VbSyntax.IdentifierNameSyntax identifier
			&& identifier.Identifier.ValueText.Equals("NameOf", StringComparison.OrdinalIgnoreCase) && invocation.ArgumentList.Arguments.Count == 1
			=> NameOfVisualBasic(VbArgument(invocation, 0)),
		VbSyntax.UnaryExpressionSyntax unary when unary.IsKind(Vb.SyntaxKind.UnaryMinusExpression) => Negate(EvaluateVisualBasic(unary.Operand)),
		_ => null
	};

	/// <summary>VB argument lists are typed as the base ArgumentSyntax; unwraps the first-class
	/// SimpleArgumentSyntax that carries the expression.</summary>
	static VbSyntax.ExpressionSyntax VbArgument(VbSyntax.ArgumentListSyntax list, int index)
		=> ((VbSyntax.SimpleArgumentSyntax)list.Arguments[index]).Expression;
	static VbSyntax.ExpressionSyntax VbArgument(VbSyntax.InvocationExpressionSyntax invocation, int index)
		=> VbArgument(invocation.ArgumentList, index);

	static string NameOfVisualBasic(VbSyntax.ExpressionSyntax expression) => expression switch {
		VbSyntax.IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		VbSyntax.MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
		_ => expression.ToString().Split('.').Last()
	};

	static string NameOf(ExpressionSyntax expression) => expression switch {
		IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
		MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
		_ => expression.ToString().Split('.').Last()
	};

	object? EvaluateMember(MemberAccessExpressionSyntax access)
	{
		var owner = Evaluate(access.Expression);
		if (owner is Type type)
			return type.IsEnum ? Enum.Parse(type, access.Name.Identifier.ValueText) : type.GetField(access.Name.Identifier.ValueText)?.GetValue(null);
		return owner == null ? null : TypeDescriptor.GetProperties(owner)[access.Name.Identifier.ValueText]?.GetValue(owner);
	}

	object? EvaluateMemberVisualBasic(VbSyntax.MemberAccessExpressionSyntax access)
	{
		var owner = EvaluateVisualBasic(access.Expression);
		if (owner is Type type)
			return type.IsEnum ? Enum.Parse(type, access.Name.Identifier.ValueText) : type.GetField(access.Name.Identifier.ValueText)?.GetValue(null);
		return owner == null ? null : TypeDescriptor.GetProperties(owner)[access.Name.Identifier.ValueText]?.GetValue(owner);
	}

	object? CreateValue(ObjectCreationExpressionSyntax creation)
	{
		var type = ResolveType(creation.Type.ToString());
		if (type == null) return null;
		var args = creation.ArgumentList?.Arguments.Select(a => Evaluate(a.Expression)).ToArray() ?? [];
		return Activator.CreateInstance(type, args);
	}

	object? CreateValueVisualBasic(VbSyntax.ObjectCreationExpressionSyntax creation)
	{
		var type = ResolveType(creation.Type.ToString());
		if (type == null) return null;
		var args = creation.ArgumentList?.Arguments.Select(a => EvaluateVisualBasic(((VbSyntax.SimpleArgumentSyntax)a).Expression)).ToArray() ?? [];
		return Activator.CreateInstance(type, args);
	}

	object? ResolveObject(string name) => components.TryGetValue(StripMe(name), out var component) ? component : ResolveType(name);

	Type? ResolveType(string name)
	{
		name = name.Replace("global::", "", StringComparison.Ordinal);
		var aliases = new Dictionary<string, Type>(StringComparer.Ordinal) {
			["Form"] = typeof(Form), ["Button"] = typeof(Button), ["Label"] = typeof(Label),
			["Panel"] = typeof(Panel), ["TextBox"] = typeof(TextBox),
			["System.Drawing.Point"] = typeof(System.Drawing.Point), ["Point"] = typeof(System.Drawing.Point),
			["System.Drawing.Size"] = typeof(System.Drawing.Size), ["Size"] = typeof(System.Drawing.Size),
			["System.Drawing.SizeF"] = typeof(System.Drawing.SizeF), ["SizeF"] = typeof(System.Drawing.SizeF),
			["System.Drawing.Font"] = typeof(System.Drawing.Font), ["Font"] = typeof(System.Drawing.Font),
			["System.Windows.Forms.Padding"] = typeof(System.Windows.Forms.Padding), ["Padding"] = typeof(System.Windows.Forms.Padding)
		};
		if (aliases.TryGetValue(name, out var alias)) return alias;
		if (!name.Contains('.')) name = "System.Windows.Forms." + name;
		return projectTypeResolver(name) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name, false)).FirstOrDefault(t => t != null);
	}

	static object? ConvertValue(object value, Type target) => target.IsInstanceOfType(value) ? value
		: target.IsEnum ? Enum.ToObject(target, value) : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
	static object? Negate(object? value) => value switch { int n => -n, long n => -n, float n => -n, double n => -n, _ => value };
	static string StripThis(string value) => value.StartsWith("this.", StringComparison.Ordinal) ? value[5..] : value;
	static string StripMe(string value) => value.StartsWith("Me.", StringComparison.Ordinal) ? value[3..]
		: value.StartsWith("this.", StringComparison.Ordinal) ? value[5..] : value;
	protected override void PerformFlush(IDesignerSerializationManager serializationManager) { }
}
