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
using System.Reflection;

namespace ICSharpCode.FormsDesigner.Host;

/// <summary>First child-owned Roslyn load path; intentionally source-in/memory-only.
/// Handles both the C# and the Visual Basic designer-file dialects.</summary>
sealed class SnapshotDesignerLoader : BasicDesignerLoader
{
	readonly DesignerDocumentSnapshot snapshot;
	readonly Func<string, Type?> projectTypeResolver;
	readonly Dictionary<string, IComponent> components = new(StringComparer.Ordinal);
	/// <summary>InitializeComponent's local variables (Visual Studio declares each TreeNode as one,
	/// e.g. <c>TreeNode treeNode1 = new TreeNode("Leaf");</c>), resolved like fields.</summary>
	readonly Dictionary<string, object> locals = new(StringComparer.Ordinal);
	readonly Dictionary<string, ResourceValue> resources = new(StringComparer.Ordinal);
	readonly Dictionary<IComponent, Dictionary<string, ResourceOrigin>> resourceOrigins = new();

	record ResourceValue(object Value, string FileName);
	record ResourceOrigin(string FileName, string Key);

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
		if (statement is LocalDeclarationStatementSyntax local) {
			foreach (var variable in local.Declaration.Variables)
				if (variable.Initializer != null && Evaluate(variable.Initializer.Value) is { } value)
					locals[variable.Identifier.ValueText] = value;
			return;
		}
		if (statement is not ExpressionStatementSyntax expression) return;
		if (expression.Expression is AssignmentExpressionSyntax assignment) {
			var target = StripThis(assignment.Left.ToString());
			if (!target.Contains('.') && assignment.Right is ObjectCreationExpressionSyntax creation) {
				var type = ResolveType(creation.Type.ToString());
				if (type != null && typeof(IComponent).IsAssignableFrom(type)) {
					components[target] = LoaderHost.CreateComponent(type, target);
					return;
				}
			}
			var separator = target.LastIndexOf('.');
			var owner = separator < 0 ? components["this"] : ResolveObject(target[..separator]);
			var name = separator < 0 ? target : target[(separator + 1)..];
			var property = owner == null ? null : TypeDescriptor.GetProperties(owner)[name];
			if (property != null && !property.IsReadOnly) {
				var value = Evaluate(assignment.Right);
				if (value != null)
					property.SetValue(owner, ConvertValue(value, property.PropertyType));
				if (owner is IComponent component) {
					if (TryGetResourceOrigin(assignment.Right, out var origin))
						SetResourceOrigin(component, name, origin);
					else ClearResourceOrigin(component, name);
				}
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
		if (expression.Expression is InvocationExpressionSyntax placement
			&& placement.Expression is MemberAccessExpressionSyntax placementMember
			&& TableLayoutPlacementMethods.Contains(placementMember.Name.Identifier.ValueText)) {
			ApplyTableLayoutPlacement(Evaluate(placementMember.Expression), placementMember.Name.Identifier.ValueText,
				placement.ArgumentList.Arguments.Select(argument => Evaluate(argument.Expression)).ToArray());
			return;
		}
		if (expression.Expression is InvocationExpressionSyntax invocationAdd
			&& invocationAdd.Expression is MemberAccessExpressionSyntax memberAdd
			&& (memberAdd.Name.Identifier.ValueText == "Add" || memberAdd.Name.Identifier.ValueText == "AddRange")) {
			if (memberAdd.Name.Identifier.ValueText == "Add" && invocationAdd.ArgumentList.Arguments.Count == 3
				&& AddToTableLayout(ResolveCollection(memberAdd.Expression),
					invocationAdd.ArgumentList.Arguments.Select(argument => Evaluate(argument.Expression)).ToArray()))
				return;
			AddToCollection(ResolveCollection(memberAdd.Expression),
				memberAdd.Name.Identifier.ValueText == "AddRange", invocationAdd.ArgumentList.Arguments[0].Expression, Evaluate);
		}
	}

	void ExecuteVisualBasic(VbSyntax.StatementSyntax statement)
	{
		if (statement is VbSyntax.LocalDeclarationStatementSyntax vbLocal) {
			foreach (var declarator in vbLocal.Declarators) {
				VbSyntax.ExpressionSyntax? initial = declarator.Initializer?.Value
					?? (declarator.AsClause as VbSyntax.AsNewClauseSyntax)?.NewExpression;
				if (initial != null && declarator.Names.Count == 1 && EvaluateVisualBasic(initial) is { } value)
					locals[declarator.Names[0].Identifier.ValueText] = value;
			}
			return;
		}
		if (statement is VbSyntax.AssignmentStatementSyntax assignment) {
			var target = StripMe(assignment.Left.ToString());
			if (!target.Contains('.') && assignment.Right is VbSyntax.ObjectCreationExpressionSyntax creation) {
				var type = ResolveType(creation.Type.ToString());
				if (type != null && typeof(IComponent).IsAssignableFrom(type)) {
					components[target] = LoaderHost.CreateComponent(type, target);
					return;
				}
			}
			var separator = target.LastIndexOf('.');
			var owner = separator < 0 ? components["this"] : ResolveObject(target[..separator]);
			var name = separator < 0 ? target : target[(separator + 1)..];
			var property = owner == null ? null : TypeDescriptor.GetProperties(owner)[name];
			if (property != null && !property.IsReadOnly) {
				var value = EvaluateVisualBasic(assignment.Right);
				if (value != null)
					property.SetValue(owner, ConvertValue(value, property.PropertyType));
				if (owner is IComponent component) {
					if (TryGetResourceOrigin(assignment.Right, out var origin))
						SetResourceOrigin(component, name, origin);
					else ClearResourceOrigin(component, name);
				}
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
			if (TableLayoutPlacementMethods.Contains(member.Name.Identifier.ValueText) && member.Expression != null) {
				ApplyTableLayoutPlacement(EvaluateVisualBasic(member.Expression), member.Name.Identifier.ValueText,
					Enumerable.Range(0, invocation.ArgumentList?.Arguments.Count ?? 0).Select(index => EvaluateVisualBasic(VbArgument(invocation, index))).ToArray());
				return;
			}
			if (member.Name.Identifier.ValueText == "Add" && invocation.ArgumentList?.Arguments.Count == 3
				&& AddToTableLayout(ResolveCollectionVisualBasic(member.Expression),
					Enumerable.Range(0, 3).Select(index => EvaluateVisualBasic(VbArgument(invocation, index))).ToArray()))
				return;
			if (member.Name.Identifier.ValueText == "Add" || member.Name.Identifier.ValueText == "AddRange") {
				AddToCollectionVisualBasic(ResolveCollectionVisualBasic(member.Expression),
					member.Name.Identifier.ValueText == "AddRange", VbArgument(invocation, 0));
			}
		}
	}

	static readonly HashSet<string> TableLayoutPlacementMethods = new(StringComparer.Ordinal) {
		"SetColumn", "SetRow", "SetCellPosition", "SetColumnSpan", "SetRowSpan"
	};

	/// <summary><c>table.Controls.Add(control, column, row)</c>: the TableLayoutPanel overload the
	/// WinForms code generator writes for a placed child. Adding through the single-argument Add
	/// would let the control auto-flow into the next free cell instead of its authored one.</summary>
	static bool AddToTableLayout(object? collection, object?[] arguments)
	{
		if (collection is not TableLayoutControlCollection controls || arguments is not [Control control, int column, int row])
			return false;
		controls.Add(control, column, row);
		return true;
	}

	/// <summary>The table's own placement calls (<c>SetColumn/SetRow/SetCellPosition/
	/// SetColumnSpan/SetRowSpan(control, value)</c>). Anything else is left to the generic path,
	/// which ignores it, as before.</summary>
	static void ApplyTableLayoutPlacement(object? owner, string method, object?[] arguments)
	{
		if (owner is not TableLayoutPanel table)
			return;
		switch (method, arguments) {
			case ("SetColumn", [Control control, int column]): table.SetColumn(control, column); break;
			case ("SetRow", [Control control, int row]): table.SetRow(control, row); break;
			case ("SetCellPosition", [Control control, TableLayoutPanelCellPosition position]): table.SetCellPosition(control, position); break;
			case ("SetColumnSpan", [Control control, int span]): table.SetColumnSpan(control, span); break;
			case ("SetRowSpan", [Control control, int span]): table.SetRowSpan(control, span); break;
		}
	}

	void LoadResources()
	{
		foreach (var file in snapshot.Files.Where(item => item.Kind.Equals("Resource", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrEmpty(item.Base64))) {
			try {
				using var stream = new MemoryStream(Convert.FromBase64String(file.Base64));
				var document = XDocument.Load(stream);
				foreach (var data in document.Root?.Elements("data") ?? []) {
					// A .resx can contain obsolete or framework-specific entries beside resources the
					// current document actually uses. One malformed image must not hide every valid
					// entry after it, or an otherwise editable resource property loses its capability.
					try {
						var name = (string?)data.Attribute("name");
						var value = data.Element("value")?.Value;
						if (name == null || value == null) continue;
						var mimeType = (string?)data.Attribute("mimetype") ?? "";
						var typeName = (string?)data.Attribute("type") ?? "";
						if (mimeType.Contains("base64", StringComparison.OrdinalIgnoreCase)
							&& (typeName.Contains("Image", StringComparison.OrdinalIgnoreCase)
								|| typeName.Contains("Bitmap", StringComparison.OrdinalIgnoreCase)
								|| typeName.Contains("Icon", StringComparison.OrdinalIgnoreCase))) {
							var imageStream = new MemoryStream(Convert.FromBase64String(value));
							resources[name] = new ResourceValue(typeName.Contains("Icon", StringComparison.OrdinalIgnoreCase)
								? new Icon(imageStream) : Image.FromStream(imageStream), file.FileName);
						} else resources[name] = new ResourceValue(value, file.FileName);
					} catch {
						// Invalid/unsupported individual resource: retain all independently valid entries.
					}
				}
			} catch { }
		}
	}

	void ApplyResources(IComponent component, string key)
	{
		foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component)) {
			if (property.IsReadOnly || !resources.TryGetValue(key + "." + property.Name, out var resource)) continue;
			try {
				if (property.PropertyType.IsInstanceOfType(resource.Value)) property.SetValue(component, resource.Value);
				else if (resource.Value is string text && property.Converter.CanConvertFrom(typeof(string))) property.SetValue(component, property.Converter.ConvertFromInvariantString(text));
				else continue;
				SetResourceOrigin(component, property.Name, new ResourceOrigin(resource.FileName, key + "." + property.Name));
			} catch { }
		}
	}

	void SetResourceOrigin(IComponent component, string propertyName, ResourceOrigin origin)
	{
		if (!resourceOrigins.TryGetValue(component, out var properties))
			resourceOrigins[component] = properties = new Dictionary<string, ResourceOrigin>(StringComparer.Ordinal);
		properties[propertyName] = origin;
	}

	public void ClearResourceOrigin(IComponent component, string propertyName)
	{
		if (resourceOrigins.TryGetValue(component, out var properties))
			properties.Remove(propertyName);
	}

	public bool TryGetResourceOrigin(IComponent component, string propertyName, out string fileName, out string key)
	{
		if (resourceOrigins.TryGetValue(component, out var properties)
			&& properties.TryGetValue(propertyName, out var origin)) {
			fileName = origin.FileName;
			key = origin.Key;
			return true;
		}
		fileName = key = "";
		return false;
	}

	bool TryGetResourceOrigin(ExpressionSyntax expression, out ResourceOrigin origin)
	{
		if (expression is CastExpressionSyntax cast)
			return TryGetResourceOrigin(cast.Expression, out origin);
		if (expression is InvocationExpressionSyntax invocation
			&& invocation.Expression is MemberAccessExpressionSyntax member
			&& member.Name.Identifier.ValueText == "GetObject"
			&& invocation.ArgumentList.Arguments.Count == 1
			&& Evaluate(invocation.ArgumentList.Arguments[0].Expression) is string key
			&& resources.TryGetValue(key, out var resource)) {
			origin = new ResourceOrigin(resource.FileName, key);
			return true;
		}
		origin = null!;
		return false;
	}

	bool TryGetResourceOrigin(VbSyntax.ExpressionSyntax expression, out ResourceOrigin origin)
	{
		if (expression is VbSyntax.CTypeExpressionSyntax cast)
			return TryGetResourceOrigin(cast.Expression, out origin);
		if (expression is VbSyntax.InvocationExpressionSyntax invocation
			&& invocation.Expression is VbSyntax.MemberAccessExpressionSyntax member
			&& member.Name.Identifier.ValueText == "GetObject"
			&& invocation.ArgumentList.Arguments.Count == 1
			&& EvaluateVisualBasic(VbArgument(invocation, 0)) is string key
			&& resources.TryGetValue(key, out var resource)) {
			origin = new ResourceOrigin(resource.FileName, key);
			return true;
		}
		origin = null!;
		return false;
	}

	object? Evaluate(ExpressionSyntax expression) => expression switch {
		LiteralExpressionSyntax literal => literal.Token.Value,
		IdentifierNameSyntax identifier => ResolveObject(identifier.Identifier.ValueText),
		ThisExpressionSyntax => components["this"],
		MemberAccessExpressionSyntax access when access.Expression is ThisExpressionSyntax => ResolveObject(access.Name.Identifier.ValueText),
		MemberAccessExpressionSyntax access => EvaluateMember(access),
		ObjectCreationExpressionSyntax creation => CreateValue(creation),
		CastExpressionSyntax cast => Evaluate(cast.Expression),
		TypeOfExpressionSyntax typeOf => ResolveType(typeOf.Type.ToString()),
		// "new string[] { ... }" (TextBox.Lines); other element types are not designer values here.
		ArrayCreationExpressionSyntax { Type.ElementType: PredefinedTypeSyntax { Keyword.ValueText: "string" } or IdentifierNameSyntax { Identifier.ValueText: "String" } or QualifiedNameSyntax { Right.Identifier.ValueText: "String" } } array
			=> array.Initializer?.Expressions.Select(item => Evaluate(item) as string ?? "").ToArray() ?? Array.Empty<string>(),
		// Any other element type that resolves (new TreeNode[] { ... } nested in a TreeNode constructor).
		ArrayCreationExpressionSyntax { Initializer: { } initializer } array when ResolveType(array.Type.ElementType.ToString()) is { } elementType
			=> TypedArray(elementType, initializer.Expressions.Select(Evaluate).ToList()),
		InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax member
			&& member.Name.Identifier.ValueText == "GetObject" && invocation.ArgumentList.Arguments.Count == 1
			=> Evaluate(invocation.ArgumentList.Arguments[0].Expression) is string key && resources.TryGetValue(key, out var resource) ? resource.Value : null,
		// Color.FromArgb(a, r, g, b) / (r, g, b) - how the designer writes a non-named colour.
		InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "FromArgb" } fromArgb
			&& fromArgb.Expression.ToString().EndsWith("Color", StringComparison.Ordinal)
			=> FromArgb(invocation.ArgumentList.Arguments.Select(argument => Evaluate(argument.Expression)).ToArray()),
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
		VbSyntax.ArrayCreationExpressionSyntax array when array.Type.ToString() is "String" or "System.String"
			=> array.Initializer?.Initializers.Select(item => EvaluateVisualBasic(item) as string ?? "").ToArray() ?? Array.Empty<string>(),
		VbSyntax.ArrayCreationExpressionSyntax { Initializer: { } vbInitializer } vbArray when ResolveType(vbArray.Type.ToString()) is { } vbElementType
			=> TypedArray(vbElementType, vbInitializer.Initializers.Select(EvaluateVisualBasic).ToList()),
		VbSyntax.GetTypeExpressionSyntax getType => ResolveType(getType.Type.ToString()),
		VbSyntax.InvocationExpressionSyntax invocation when invocation.Expression is VbSyntax.MemberAccessExpressionSyntax member
			&& member.Name.Identifier.ValueText == "GetObject" && invocation.ArgumentList.Arguments.Count == 1
			=> EvaluateVisualBasic(VbArgument(invocation, 0)) is string key && resources.TryGetValue(key, out var resource) ? resource.Value : null,
		VbSyntax.InvocationExpressionSyntax invocation when invocation.Expression is VbSyntax.MemberAccessExpressionSyntax { Name.Identifier.ValueText: "FromArgb" } fromArgb
			&& fromArgb.Expression != null && fromArgb.Expression.ToString().EndsWith("Color", StringComparison.Ordinal) && invocation.ArgumentList != null
			=> FromArgb(invocation.ArgumentList.Arguments.Select(argument => EvaluateVisualBasic(((VbSyntax.SimpleArgumentSyntax)argument).Expression)).ToArray()),
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

	// access.Expression is often a fully-qualified static type name rather than a value-producing
	// expression - e.g. "System.Drawing.FontStyle" in "System.Drawing.FontStyle.Bold". Evaluate()
	// cannot resolve that on its own: it only recognizes a MemberAccessExpressionSyntax chain as a
	// type once it bottoms out at an owner that is already a Type, but each intermediate segment
	// ("System", "System.Drawing") is not itself a type name and evaluates to null, so the whole
	// chain silently evaluates to null instead of the enum value. A null argument in an enum
	// constructor-parameter slot doesn't just fail - it makes Activator.CreateInstance's binder
	// unable to pick between same-arity overloads that differ only in that parameter's enum type
	// (e.g. Font(string, float, FontStyle) vs Font(string, float, GraphicsUnit)), throwing
	// AmbiguousMatchException. Try the qualified name as a type first; only fall back to evaluating
	// it as a value when it isn't one (e.g. chained instance property access).
	object? EvaluateMember(MemberAccessExpressionSyntax access)
	{
		var owner = ResolveType(access.Expression.ToString()) ?? Evaluate(access.Expression);
		if (owner is Type type)
			return type.IsEnum ? Enum.Parse(type, access.Name.Identifier.ValueText) : type.GetField(access.Name.Identifier.ValueText)?.GetValue(null);
		return owner == null ? null : TypeDescriptor.GetProperties(owner)[access.Name.Identifier.ValueText]?.GetValue(owner);
	}

	object? EvaluateMemberVisualBasic(VbSyntax.MemberAccessExpressionSyntax access)
	{
		var owner = ResolveType(access.Expression.ToString()) ?? EvaluateVisualBasic(access.Expression);
		if (owner is Type type)
			return type.IsEnum ? Enum.Parse(type, access.Name.Identifier.ValueText) : type.GetField(access.Name.Identifier.ValueText)?.GetValue(null);
		return owner == null ? null : TypeDescriptor.GetProperties(owner)[access.Name.Identifier.ValueText]?.GetValue(owner);
	}

	/// <summary>Resolves the left-hand side of an Add/AddRange call - "menuStrip1.Items",
	/// "this.Controls", or a bare "Controls" (root-level statements the generator emits without a
	/// "this."/"Me." prefix, e.g. plain "Controls.Add(statusStrip1);") - as the actual mutable
	/// collection object via property access. Evaluate() cannot be reused directly here: its
	/// ThisExpressionSyntax shortcut treats "this.Foo" as "the sited component named Foo" (the
	/// common case, e.g. this.button1), which is the wrong lookup for a property access like
	/// this.Controls/this.Items.</summary>
	object? ResolveCollection(ExpressionSyntax expression)
	{
		if (expression is IdentifierNameSyntax identifier)
			return TypeDescriptor.GetProperties(components["this"])[identifier.Identifier.ValueText]?.GetValue(components["this"]);
		if (expression is not MemberAccessExpressionSyntax member) return null;
		var owner = member.Expression is ThisExpressionSyntax ? components.GetValueOrDefault("this") : Evaluate(member.Expression);
		return owner == null ? null : TypeDescriptor.GetProperties(owner)[member.Name.Identifier.ValueText]?.GetValue(owner);
	}

	object? ResolveCollectionVisualBasic(VbSyntax.ExpressionSyntax expression)
	{
		if (expression is VbSyntax.IdentifierNameSyntax identifier)
			return TypeDescriptor.GetProperties(components["this"])[identifier.Identifier.ValueText]?.GetValue(components["this"]);
		if (expression is not VbSyntax.MemberAccessExpressionSyntax member) return null;
		var owner = member.Expression is VbSyntax.MeExpressionSyntax ? components.GetValueOrDefault("this") : EvaluateVisualBasic(member.Expression);
		return owner == null ? null : TypeDescriptor.GetProperties(owner)[member.Name.Identifier.ValueText]?.GetValue(owner);
	}

	/// <summary>Adds each evaluated argument to a resolved collection property via reflection, so
	/// this works uniformly for Control.ControlCollection.Add(Control) and
	/// ToolStripItemCollection.Add(ToolStripItem)/DropDownItems alike. AddRange(new T[] { a, b, c })
	/// is unwrapped to its array-initializer elements and each is added individually - neither
	/// WinForms collection type implements a single AddRange(IEnumerable) overload that reflection
	/// could call directly, and both Control.Controls.AddRange and ToolStripItemCollection.AddRange
	/// only accept a strongly-typed array, not IEnumerable, so a generic reflective call would need
	/// to materialize that array anyway.</summary>
	void AddToCollection(object? collection, bool isRange, ExpressionSyntax argument, Func<ExpressionSyntax, object?> evaluate)
	{
		if (collection == null) return;
		// GetMethod("Add", flags) throws AmbiguousMatchException whenever the collection type has
		// more than one public single-parameter Add overload (e.g.
		// TableLayoutColumnStyleCollection.Add(ColumnStyle) alongside its IList.Add(object)
		// explicit-interface implementation) - pick the candidate whose parameter type actually
		// matches each value instead of asking reflection to name-resolve a single method.
		var addMethods = collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
			.Where(m => m.Name == "Add" && m.GetParameters().Length == 1).ToArray();
		if (addMethods.Length == 0) return;
		var values = isRange && argument is ArrayCreationExpressionSyntax arrayCreation
			? arrayCreation.Initializer?.Expressions.Select(evaluate) ?? []
			: [evaluate(argument)];
		foreach (var value in values) {
			if (value == null) continue;
			var method = addMethods.FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsInstanceOfType(value)) ?? addMethods[0];
			try { method.Invoke(collection, [value]); } catch { }
		}
	}

	void AddToCollectionVisualBasic(object? collection, bool isRange, VbSyntax.ExpressionSyntax argument)
	{
		if (collection == null) return;
		var addMethods = collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
			.Where(m => m.Name == "Add" && m.GetParameters().Length == 1).ToArray();
		if (addMethods.Length == 0) return;
		var values = isRange && argument is VbSyntax.ArrayCreationExpressionSyntax arrayCreation
			? (arrayCreation.Initializer as VbSyntax.CollectionInitializerSyntax)?.Initializers.Select(EvaluateVisualBasic) ?? []
			: [EvaluateVisualBasic(argument)];
		foreach (var value in values) {
			if (value == null) continue;
			var method = addMethods.FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsInstanceOfType(value)) ?? addMethods[0];
			try { method.Invoke(collection, [value]); } catch { }
		}
	}

	/// <summary>An array of <paramref name="elementType"/>; values that are not instances of it
	/// (unresolved expressions) are skipped rather than failing the whole load.</summary>
	static Array TypedArray(Type elementType, List<object?> values)
	{
		var kept = values.Where(elementType.IsInstanceOfType).ToList();
		var array = Array.CreateInstance(elementType, kept.Count);
		for (var i = 0; i < kept.Count; i++)
			array.SetValue(kept[i], i);
		return array;
	}

	static object? FromArgb(object?[] values)
	{
		if (values.Any(value => value is not int)) return null;
		var ints = values.Cast<int>().ToArray();
		return ints.Length switch {
			4 => System.Drawing.Color.FromArgb(ints[0], ints[1], ints[2], ints[3]),
			3 => System.Drawing.Color.FromArgb(ints[0], ints[1], ints[2]),
			1 => System.Drawing.Color.FromArgb(ints[0]),
			_ => null
		};
	}

	object? CreateValue(ObjectCreationExpressionSyntax creation)
	{
		var type = ResolveType(creation.Type.ToString());
		if (type == null) return null;
		var args = creation.ArgumentList?.Arguments.Select(a => Evaluate(a.Expression)).ToArray() ?? [];
		var instance = CreateInstance(type, args);
		// Object initializer ("new TreeNode("Leaf") { Name = "Node1" }"): simple property assignments.
		if (instance != null && creation.Initializer is { } initializer)
			foreach (var assignment in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
				if (assignment.Left is IdentifierNameSyntax member)
					SetInitializedProperty(instance, member.Identifier.ValueText, Evaluate(assignment.Right));
		return instance;
	}

	static void SetInitializedProperty(object instance, string name, object? value)
	{
		var property = TypeDescriptor.GetProperties(instance)[name];
		if (property != null && !property.IsReadOnly && value != null)
			try { property.SetValue(instance, ConvertValue(value, property.PropertyType)); } catch { }
	}

	object? CreateValueVisualBasic(VbSyntax.ObjectCreationExpressionSyntax creation)
	{
		var type = ResolveType(creation.Type.ToString());
		if (type == null) return null;
		var args = creation.ArgumentList?.Arguments.Select(a => EvaluateVisualBasic(((VbSyntax.SimpleArgumentSyntax)a).Expression)).ToArray() ?? [];
		var instance = CreateInstance(type, args);
		// "New TreeNode("Leaf") With {.Name = "Node1"}".
		if (instance != null && creation.Initializer is VbSyntax.ObjectMemberInitializerSyntax initializer)
			foreach (var field in initializer.Initializers.OfType<VbSyntax.NamedFieldInitializerSyntax>())
				SetInitializedProperty(instance, field.Name.Identifier.ValueText, EvaluateVisualBasic(field.Expression));
		return instance;
	}

	/// <summary>
	/// Activator.CreateInstance(type, args) picks an overload by exact/coercible argument Type
	/// identity, which breaks when the host process has two independently loaded assemblies each
	/// defining a type with the same full name (the duplicate-System.Drawing-facade hazard;
	/// ProGPU.Wpf.Sdk.targets' _ProGpuWpfSdkRemoveNetCoreSystemDrawingFacade documents the identical
	/// problem elsewhere in this codebase): a resolved enum value's Type may not be reference-equal
	/// to the target constructor parameter's Type even though both are "the same" enum by name -
	/// e.g. resolving System.Drawing.FontStyle.Bold can hand back a FontStyle from a different
	/// loaded System.Drawing than the one Font's own constructor parameter expects, throwing
	/// MissingMethodException. Pick the constructor ourselves - matching each parameter by simple
	/// type name once exact instance matching fails - and convert every argument to that
	/// constructor's own parameter type via ConvertValue (Enum.ToObject converts across
	/// distinct-but-same-named enum Types through their shared underlying integral value).
	/// </summary>
	static object? CreateInstance(Type type, object?[] args)
	{
		var ctor = FindConstructor(type, args);
		if (ctor != null) return Invoke(ctor, args);

		// System.Drawing.Font's well-known 3-arg convenience overload - new Font(familyName,
		// emSize, style), exactly what VS/OpenDevelop's own designer-generated code and
		// hand-written .Designer.cs files commonly emit for a non-default font - is implemented by
		// real System.Drawing.Common as the 4-arg form with GraphicsUnit.Point. The portable
		// reimplementation this host runs against when the project resolves to LibreWinForms
		// (ProGPU.System.Drawing.Common) does not ship that convenience overload at all, only the
		// explicit-unit ones, so widen once with that same default unit before giving up - the GDI+
		// GraphicsUnit enum's values (World=0 ... Point=3) are stable and documented, so this does
		// not depend on which assembly's copy of the enum type is loaded.
		if (type.FullName == "System.Drawing.Font" && args.Length == 3) {
			var widerCtor = type.GetConstructors().FirstOrDefault(c => {
				var parameters = c.GetParameters();
				return parameters.Length == 4 && parameters[3].ParameterType.Name == "GraphicsUnit";
			});
			if (widerCtor != null) {
				var unit = Enum.ToObject(widerCtor.GetParameters()[3].ParameterType, 3); // GraphicsUnit.Point
				return Invoke(widerCtor, [.. args, unit]);
			}
		}
		return Activator.CreateInstance(type, args);
	}

	static ConstructorInfo? FindConstructor(Type type, object?[] args) =>
		type.GetConstructors().Where(c => c.GetParameters().Length == args.Length)
			.FirstOrDefault(c => c.GetParameters().Select((p, i) => (p, i)).All(pair =>
				args[pair.i] == null || pair.p.ParameterType.IsInstanceOfType(args[pair.i])
					|| pair.p.ParameterType.Name == args[pair.i]!.GetType().Name));

	static object Invoke(ConstructorInfo ctor, object?[] args)
	{
		var parameters = ctor.GetParameters();
		var converted = new object?[args.Length];
		for (var i = 0; i < args.Length; i++)
			converted[i] = args[i] == null ? null : ConvertValue(args[i]!, parameters[i].ParameterType);
		return ctor.Invoke(converted);
	}

	object? ResolveObject(string name) => components.TryGetValue(StripMe(name), out var component) ? component
		: locals.TryGetValue(name, out var local) ? local : ResolveType(name);

	internal Type? ResolveType(string name)
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

	// Enum.ToObject(Type, object) only accepts the underlying integral primitive (Int32, Byte, ...);
	// a boxed value that is itself an Enum of a different (but same-named) Type - the
	// duplicate-System.Drawing-facade hazard described on CreateInstance - is not one of those
	// primitives and throws ArgumentException, even though converting it is exactly the point.
	// Unwrap through its underlying integral value first so cross-assembly enum-to-enum conversion
	// works the same as int-to-enum.
	static object? ConvertValue(object value, Type target) => target.IsInstanceOfType(value) ? value
		: target.IsEnum ? Enum.ToObject(target, value is Enum enumValue ? Convert.ToInt64(enumValue, CultureInfo.InvariantCulture) : value)
		: Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
	static object? Negate(object? value) => value switch { int n => -n, long n => -n, float n => -n, double n => -n, _ => value };
	static string StripThis(string value) => value.StartsWith("this.", StringComparison.Ordinal) ? value[5..] : value;
	static string StripMe(string value) => value.StartsWith("Me.", StringComparison.Ordinal) ? value[3..]
		: value.StartsWith("this.", StringComparison.Ordinal) ? value[5..] : value;
	protected override void PerformFlush(IDesignerSerializationManager serializationManager) { }
}
