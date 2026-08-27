using System.Activities;
using System.Activities.Statements;
using System.Activities.XamlIntegration;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Xaml;
using System.Xml;

namespace ICSharpCode.WorkflowDesigner.Host;

/// <summary>Owns one loaded .xaml activity tree and its edit/round-trip operations. Load and
/// Save go through <c>ActivityXamlServices.CreateBuilderReader/Writer</c> (the same mechanism
/// the classic WorkflowDesigner control uses internally) so the object graph is the real,
/// mutable <see cref="ActivityBuilder"/>/<see cref="Activity"/> tree, not a re-parsed copy - and
/// the round-trip preserves everything CoreWF itself understands about the file, the same
/// "run the real runtime object" rule the other four OOP backends already follow
/// (designer-common.md).</summary>
sealed class WorkflowDocument
{
	static readonly BindingFlags DeclaredPublicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

	public Activity? Root { get; private set; }
	public ActivityBuilder? Builder { get; private set; }
	public bool LastParseSucceeded { get; private set; }
	public string Error { get; private set; } = "";

	public void Reset(string xamlText)
	{
		try {
			using var stringReader = new StringReader(xamlText);
			using var xmlReader = XmlReader.Create(stringReader);
			var builderReader = ActivityXamlServices.CreateBuilderReader(new XamlXmlReader(xmlReader));
			Builder = (ActivityBuilder)XamlServices.Load(builderReader);
			Root = Builder.Implementation;
			LastParseSucceeded = true;
			Error = "";
		} catch (Exception ex) {
			Builder = null;
			Root = null;
			LastParseSucceeded = false;
			Error = ex.Message;
		}
	}

	public string ToXaml()
	{
		var builder = Builder ?? new ActivityBuilder { Implementation = Root };
		var stringWriter = new StringWriter();
		using (var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { Indent = true })) {
			var builderWriter = ActivityXamlServices.CreateBuilderWriter(new XamlXmlWriter(xmlWriter, new XamlSchemaContext()));
			XamlServices.Save(builderWriter, builder);
		}
		return stringWriter.ToString();
	}

	/// <summary>Projects workflow-level ActivityBuilder arguments without exposing CoreWF types
	/// outside the host process. The editor protocol can build its tabular Arguments surface from
	/// this stable shape.</summary>
	public IEnumerable<(string Name, string TypeName, string DefaultValue)> GetArguments()
	{
		if (Builder == null) yield break;
		foreach (var property in Builder.Properties)
			yield return (property.Name, property.Type?.Name ?? "Object", GetLiteralValue(property.Value));
	}

	public bool AddArgument(string name, string typeName, string defaultValue)
	{
		if (Builder == null || string.IsNullOrWhiteSpace(name) || Builder.Properties.Any(property => property.Name == name)) return false;
		var type = ResolveArgumentType(typeName);
		var propertyType = Builder.Properties.GetType().BaseType?.GetGenericArguments().LastOrDefault();
		if (propertyType == null) return false;
		var property = Activator.CreateInstance(propertyType);
		if (property == null) return false;
		propertyType.GetProperty("Name")?.SetValue(property, name);
		propertyType.GetProperty("Type")?.SetValue(property, type);
		if (!TrySetArgumentDefault(property, type, defaultValue)) return false;
		var add = Builder.Properties.GetType().GetMethod("Add", new[] { propertyType });
		if (add == null) return false;
		add.Invoke(Builder.Properties, new[] { property });
		return true;
	}

	public bool RemoveArgument(string name)
	{
		if (Builder == null || string.IsNullOrWhiteSpace(name)) return false;
		var property = Builder.Properties.FirstOrDefault(item => item.Name == name);
		if (property == null) return false;
		Builder.Properties.Remove(property);
		return true;
	}

	public bool UpdateArgument(string oldName, string newName, string typeName, string defaultValue)
	{
		if (Builder == null || string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
		var property = Builder.Properties.FirstOrDefault(item => item.Name == oldName);
		if (property == null || (oldName != newName && Builder.Properties.Any(item => item.Name == newName))) return false;
		Builder.Properties.Remove(property);
		var runtimeType = property.GetType();
		runtimeType.GetProperty("Name")?.SetValue(property, newName);
		var type = ResolveArgumentType(typeName);
		runtimeType.GetProperty("Type")?.SetValue(property, type);
		if (!TrySetArgumentDefault(property, type, defaultValue)) return false;
		var add = Builder.Properties.GetType().GetMethods().FirstOrDefault(method => method.Name == "Add" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(runtimeType));
		if (add == null) return false;
		add.Invoke(Builder.Properties, new[] { property });
		return true;
	}

	static bool TrySetArgumentDefault(object property, Type type, string value)
	{
		var valueProperty = property.GetType().GetProperty("Value");
		if (valueProperty == null) return false;
		if (string.IsNullOrWhiteSpace(value)) { valueProperty.SetValue(property, null); return true; }
		try {
			var literal = ConvertLiteral(value, type);
			var argumentType = typeof(InArgument<>).MakeGenericType(type);
			valueProperty.SetValue(property, Activator.CreateInstance(argumentType, literal));
			return true;
		} catch (Exception) { return false; }
	}

	static string GetLiteralValue(object? value)
	{
		var expression = value?.GetType().GetProperty("Expression")?.GetValue(value);
		return expression?.GetType().GetProperty("Value")?.GetValue(expression)?.ToString() ?? "";
	}

	static object ConvertLiteral(string value, Type type)
	{
		if (type == typeof(string)) return value;
		if (type == typeof(bool)) return bool.Parse(value);
		if (type.IsEnum) return Enum.Parse(type, value, ignoreCase: true);
		return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture)!;
	}

	public IEnumerable<(string Name, string TypeName, string Scope)> GetVariables()
	{
		var variables = Root?.GetType().GetProperty("Variables")?.GetValue(Root) as System.Collections.IEnumerable;
		if (variables == null) yield break;
		foreach (var variable in variables.Cast<object>())
			yield return ((string?)variable.GetType().GetProperty("Name")?.GetValue(variable) ?? "", (variable.GetType().GetProperty("Type")?.GetValue(variable) as Type)?.Name ?? "Object", Root!.GetType().Name);
	}

	public bool AddVariable(string name, string typeName)
	{
		if (Root == null || string.IsNullOrWhiteSpace(name)) return false;
		var variables = Root.GetType().GetProperty("Variables")?.GetValue(Root);
		if (variables is not System.Collections.IEnumerable existing || existing.Cast<object>().Any(variable => (string?)variable.GetType().GetProperty("Name")?.GetValue(variable) == name)) return false;
		var variableType = typeof(Variable<>).MakeGenericType(ResolveArgumentType(typeName));
		var variable = Activator.CreateInstance(variableType);
		if (variable == null) return false;
		variableType.GetProperty("Name")?.SetValue(variable, name);
		var add = variables.GetType().GetMethods().FirstOrDefault(method => method.Name == "Add" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(variableType));
		if (add == null) return false;
		add.Invoke(variables, new[] { variable });
		return true;
	}

	public bool RemoveVariable(string name)
	{
		if (Root == null || string.IsNullOrWhiteSpace(name)) return false;
		var variables = Root.GetType().GetProperty("Variables")?.GetValue(Root);
		if (variables is not System.Collections.IEnumerable existing) return false;
		var variable = existing.Cast<object>().FirstOrDefault(item => (string?)item.GetType().GetProperty("Name")?.GetValue(item) == name);
		if (variable == null) return false;
		var remove = variables.GetType().GetMethods().FirstOrDefault(method => method.Name == "Remove" && method.GetParameters() is { Length: 1 } parameters && parameters[0].ParameterType.IsAssignableFrom(variable.GetType()));
		if (remove == null) return false;
		remove.Invoke(variables, new[] { variable });
		return true;
	}

	static Type ResolveArgumentType(string name) => name.Trim().ToLowerInvariant() switch {
		"string" => typeof(string), "int" or "int32" => typeof(int), "bool" or "boolean" => typeof(bool),
		"double" => typeof(double), "decimal" => typeof(decimal), _ => Type.GetType(name) ?? typeof(object)
	};

	/// <summary>Finds an activity by its structural path id ("" for the root, "0.2.1" for the
	/// second child of the third child of the root - dot-joined <see cref="WorkflowInspectionServices"/>
	/// child indices). Activities have no required design-time name (unlike WinForms/WPF/MewUI
	/// elements, which always get one), so the path is the only stable-enough identity a fresh
	/// tree walk can reproduce.</summary>
	public Activity? Find(string id)
	{
		if (Root == null) return null;
		if (id.Length == 0) return Root;
		var current = Root;
		foreach (var part in id.Split('.')) {
			if (!int.TryParse(part, out var index)) return null;
			var children = WorkflowInspectionServices.GetActivities(current).ToArray();
			if (index < 0 || index >= children.Length) return null;
			current = children[index];
		}
		return current;
	}

	public bool SetProperty(string id, string propertyName, string value)
	{
		var activity = Find(id);
		if (activity == null) return false;
		if (propertyName == "$displayName") { activity.DisplayName = value; return true; }
		var property = activity.GetType().GetProperty(propertyName);
		if (property == null || !property.CanWrite) return false;
		var converted = ConvertToPropertyType(property.PropertyType, value);
		if (converted == null && property.PropertyType.IsValueType) return false;
		property.SetValue(activity, converted);
		return true;
	}

	/// <summary>Adds a new activity of <paramref name="typeName"/> (resolved against
	/// <c>System.Activities.Statements</c> when unqualified, matching the toolbox catalog the
	/// host reports) as a child of <paramref name="parentId"/>'s first collection-of-Activity
	/// property (e.g. <see cref="Sequence.Activities"/>). Returns the new child's path id, or
	/// null when the parent has no such collection or the type can't be resolved/constructed.</summary>
	public string? AddChild(string parentId, string typeName)
	{
		var parent = Find(parentId);
		if (parent == null) return null;
		var childType = ResolveActivityType(typeName);
		if (childType == null) return null;
		if (Activator.CreateInstance(childType) is not Activity child) return null;
		var collectionProperty = FindActivityCollectionProperty(parent.GetType());
		if (collectionProperty != null) {
			var collection = collectionProperty.GetValue(parent);
			var addMethod = collectionProperty.PropertyType.GetMethod("Add", new[] { collectionProperty.PropertyType.GetGenericArguments()[0] });
			addMethod?.Invoke(collection, new object[] { child });
		} else {
			// Structured activities use writable Activity slots instead of a collection: If.Then/
			// Else, While.Body and similar CoreWF shapes. Fill the first empty slot in declaration
			// order, which mirrors the designer's natural "Then before Else" insertion behavior.
			var slot = parent.GetType().GetProperties()
				.FirstOrDefault(property => property.CanWrite && typeof(Activity).IsAssignableFrom(property.PropertyType)
					&& property.GetValue(parent) == null);
			if (slot == null) return null;
			slot.SetValue(parent, child);
		}
		var index = WorkflowInspectionServices.GetActivities(parent).ToList().IndexOf(child);
		if (index < 0) return null;
		return parentId.Length == 0 ? index.ToString() : parentId + "." + index;
	}

	public bool Remove(string id)
	{
		if (id.Length == 0) return false;
		var lastDot = id.LastIndexOf('.');
		var parentId = lastDot < 0 ? "" : id[..lastDot];
		var index = int.Parse(lastDot < 0 ? id : id[(lastDot + 1)..]);
		var parent = Find(parentId);
		var target = Find(id);
		if (parent == null || target == null) return false;
		var collectionProperty = FindActivityCollectionProperty(parent.GetType());
		if (collectionProperty == null) return false;
		var collection = collectionProperty.GetValue(parent);
		var removeMethod = collectionProperty.PropertyType.GetMethod("Remove", new[] { collectionProperty.PropertyType.GetGenericArguments()[0] });
		var removed = removeMethod?.Invoke(collection, new object[] { target });
		return removed is true;
	}

	static PropertyInfo? FindActivityCollectionProperty(Type activityType) =>
		activityType.GetProperties()
			.FirstOrDefault(p => p.PropertyType.IsGenericType
				&& p.PropertyType.GetGenericTypeDefinition() == typeof(Collection<>)
				&& typeof(Activity).IsAssignableFrom(p.PropertyType.GetGenericArguments()[0]));

	static Type? ResolveActivityType(string typeName)
	{
		if (typeName.Contains('.')) return Type.GetType(typeName);
		return typeof(Sequence).Assembly.GetType("System.Activities.Statements." + typeName);
	}

	/// <summary>Reads/writes the "designable" properties of an activity for the Properties pad:
	/// simple CLR values directly, and <c>InArgument{T}</c>/<c>OutArgument{T}</c>/
	/// <c>InOutArgument{T}</c> whose current expression is a plain <c>Literal{T}</c> by their
	/// literal value (the common case for a hand-authored workflow; a non-literal expression -
	/// a VisualBasic/C# expression bound to a variable - is reported read-only rather than
	/// silently discarded on save).</summary>
	public IEnumerable<(string Name, string Value, string TypeName, bool IsReadOnly)> GetProperties(Activity activity)
	{
		foreach (var property in activity.GetType().GetProperties()) {
			if (property.DeclaringType == typeof(Activity) || property.DeclaringType == typeof(object)) continue;
			if (!property.CanRead) continue;
			if (typeof(Activity).IsAssignableFrom(property.PropertyType)) continue;
			if (IsActivityCollection(property.PropertyType)) continue;
			var argumentElementType = GetArgumentElementType(property.PropertyType);
			if (argumentElementType != null) {
				var (value, isLiteral) = ReadArgumentLiteral(property.GetValue(activity), argumentElementType);
				yield return (property.Name, value, argumentElementType.Name, !isLiteral);
				continue;
			}
			if (!IsSimpleType(property.PropertyType)) continue;
			var raw = property.GetValue(activity);
			yield return (property.Name, raw?.ToString() ?? "", property.PropertyType.Name, !property.CanWrite);
		}
	}

	static bool IsActivityCollection(Type type) =>
		type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Collection<>)
			&& typeof(Activity).IsAssignableFrom(type.GetGenericArguments()[0]);

	static bool IsSimpleType(Type type) =>
		type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
			|| type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid);

	static Type? GetArgumentElementType(Type type)
	{
		if (!type.IsGenericType) return null;
		var definition = type.GetGenericTypeDefinition();
		if (definition == typeof(InArgument<>) || definition == typeof(OutArgument<>) || definition == typeof(InOutArgument<>))
			return type.GetGenericArguments()[0];
		return null;
	}

	static (string Value, bool IsLiteral) ReadArgumentLiteral(object? argument, Type elementType)
	{
		if (argument == null) return ("", true);
		var expressionProperty = argument.GetType().GetProperty("Expression", DeclaredPublicInstance);
		var expression = expressionProperty?.GetValue(argument);
		if (expression == null) return ("", true);
		var literalType = typeof(System.Activities.Expressions.Literal<>).MakeGenericType(elementType);
		if (!literalType.IsInstanceOfType(expression)) return ("<expression>", false);
		var valueProperty = literalType.GetProperty("Value", DeclaredPublicInstance);
		return (valueProperty?.GetValue(expression)?.ToString() ?? "", true);
	}

	static object? ConvertToPropertyType(Type propertyType, string value)
	{
		var argumentElementType = GetArgumentElementType(propertyType);
		if (argumentElementType != null) {
			var converted = ConvertSimple(argumentElementType, value);
			var constructor = propertyType.GetConstructor(new[] { argumentElementType });
			return constructor?.Invoke(new[] { converted });
		}
		return ConvertSimple(propertyType, value);
	}

	static object? ConvertSimple(Type type, string value)
	{
		if (type == typeof(string)) return value;
		if (type.IsEnum) return Enum.Parse(type, value, ignoreCase: true);
		try { return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture); }
		catch { return null; }
	}
}
