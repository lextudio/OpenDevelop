using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace ICSharpCode.WinUIXamlDesigner.MicrosoftHost;

/// <summary>
/// A reflection-backed <see cref="IXamlMetadataProvider"/> for the design host.
///
/// WinUI's runtime XamlReader does not resolve types by itself - it asks the current Application,
/// which in a normal app is the compiler-generated XamlTypeInfo covering exactly the types that
/// app's markup uses. A design host compiles no XAML, so it ships no such provider, and without
/// one XamlReader can only see the small core the framework registers itself: Grid, TextBlock,
/// Button and friends resolve, while SplitButton, InfoBar, MicaBackdrop and every app-defined
/// control come back as "The type 'X' was not found."
///
/// That is not a theoretical gap. Running the designer over WinUI-Gallery's 187 pages rendered
/// only 9; virtually every failure was a missing type. This provider closes it by resolving
/// against the assemblies actually loaded into the child - the WinUI framework plus whatever
/// HostBootstrap preloaded from the designed app's output.
/// </summary>
sealed class ReflectionXamlMetadataProvider : IXamlMetadataProvider
{
	readonly Dictionary<string, IXamlType?> byName = new(StringComparer.Ordinal);
	readonly Dictionary<Type, IXamlType> byType = new();

	public IXamlType GetXamlType(Type type) => Resolve(type)!;

	public IXamlType GetXamlType(string fullName)
	{
		lock (byName)
		{
			if (byName.TryGetValue(fullName, out var cached)) return cached!;
			var resolved = Resolve(FindType(fullName));
			byName[fullName] = resolved;
			return resolved!;
		}
	}

	// The parser resolves clr namespaces from the markup's own `using:` declarations, so there are
	// no assembly-level xmlns mappings to contribute here.
	public XmlnsDefinition[] GetXmlnsDefinitions() => Array.Empty<XmlnsDefinition>();

	internal IXamlType? Resolve(Type? type)
	{
		if (type is null) return null;
		lock (byType)
		{
			if (byType.TryGetValue(type, out var cached)) return cached;
			// Insert before populating: a type's own members can reference the type itself
			// (ContentProperty, ItemType), and re-entering here would otherwise recurse forever.
			var xamlType = new ReflectionXamlType(this, type);
			byType[type] = xamlType;
			return xamlType;
		}
	}

	static Type? FindType(string fullName)
	{
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			Type? found = null;
			try { found = assembly.GetType(fullName, throwOnError: false); }
			catch { /* a broken or partially-loaded assembly must not fail the whole lookup */ }
			if (found != null) return found;
		}
		return null;
	}
}

sealed class ReflectionXamlType(ReflectionXamlMetadataProvider provider, Type type) : IXamlType
{
	readonly Dictionary<string, IXamlMember?> members = new(StringComparer.Ordinal);

	public Type UnderlyingType => type;
	public string FullName => type.FullName ?? type.Name;
	public IXamlType BaseType => provider.Resolve(type.BaseType)!;
	public bool IsArray => type.IsArray;
	public bool IsMarkupExtension => typeof(MarkupExtension).IsAssignableFrom(type);
	public bool IsBindable => true;
	public bool IsConstructible => !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null;
	public bool IsDictionary => typeof(IDictionary).IsAssignableFrom(type) || Implements(type, typeof(IDictionary<,>));
	public bool IsCollection => !IsDictionary && (typeof(IList).IsAssignableFrom(type) || Implements(type, typeof(ICollection<>)));
	public IXamlType ItemType => provider.Resolve(ElementOf(type, IsDictionary ? 1 : 0))!;
	public IXamlType KeyType => provider.Resolve(IsDictionary ? ElementOf(type, 0) : null)!;
	public IXamlType BoxedType => null!;

	public IXamlMember ContentProperty
	{
		get
		{
			// ContentPropertyAttribute is declared on the type or inherited; the parser needs it to
			// know where a child element goes when the markup names no property.
			for (var current = type; current != null; current = current.BaseType)
			{
				if (FindContentPropertyName(current) is { Length: > 0 } name) return GetMember(name);
			}
			return null!;
		}
	}

	/// <summary>
	/// Reads a type's declared content-property name, tolerating EITHER
	/// <c>Microsoft.UI.Xaml.Markup.ContentPropertyAttribute</c> or the legacy
	/// <c>Windows.UI.Xaml.Markup.ContentPropertyAttribute</c> it is a near-identical duplicate of.
	///
	/// This is not a hypothetical: CsWinRT's WinUI 3 projection of framework controls attaches the
	/// OLD, UWP-era attribute type to the generated wrapper class, not the new one - a strongly
	/// typed <c>GetCustomAttribute&lt;Microsoft.UI.Xaml.Markup.ContentPropertyAttribute&gt;()</c>
	/// finds nothing on, say, <c>AnimatedIcon</c>, so its content property comes back null and the
	/// XAML parser has no way to know an unnamed child (`&lt;AnimatedIcon&gt;&lt;SomeVisualSource
	/// /&gt;&lt;/AnimatedIcon&gt;`, exactly how the WinUI framework's own default styles write it)
	/// belongs on `Source` - it surfaces as "AnimatedIcon does not support X as content", which
	/// reads like a content-model incompatibility but is really a missed attribute lookup. Reading
	/// by attribute TYPE NAME instead of a fixed CLR type covers both namespaces without needing to
	/// know which one a given projected type carries.
	/// </summary>
	static string? FindContentPropertyName(Type type)
	{
		foreach (var data in type.GetCustomAttributesData())
		{
			if (data.AttributeType.Name != nameof(ContentPropertyAttribute)) continue;
			var named = data.NamedArguments.FirstOrDefault(a => a.MemberName == "Name");
			if (named.TypedValue.Value is string fromProperty) return fromProperty;
			var positional = data.ConstructorArguments.FirstOrDefault();
			if (positional.Value is string fromConstructor) return fromConstructor;
		}
		return null;
	}

	public object ActivateInstance() => Activator.CreateInstance(type)!;

	public object CreateFromString(string value)
	{
		if (type.IsEnum) return Enum.Parse(type, value, ignoreCase: true);
		return Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture)!;
	}

	public IXamlMember GetMember(string name)
	{
		lock (members)
		{
			if (members.TryGetValue(name, out var cached)) return cached!;
			var member = Build(name);
			members[name] = member;
			return member!;
		}
	}

	IXamlMember? Build(string name)
	{
		var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
		if (property != null) return new ReflectionXamlMember(provider, this, property);

		// Attached properties arrive as a plain member name on the DECLARING type (Grid.Row is
		// resolved as GetXamlType("...Grid").GetMember("Row")), and are implemented as a static
		// Get/Set pair rather than an instance property.
		var getter = type.GetMethod("Get" + name, BindingFlags.Public | BindingFlags.Static);
		var setter = type.GetMethod("Set" + name, BindingFlags.Public | BindingFlags.Static);
		return getter != null || setter != null
			? new ReflectionXamlMember(provider, this, name, getter, setter)
			: null;
	}

	public void AddToVector(object instance, object value) => ((IList)instance).Add(value);
	public void AddToMap(object instance, object key, object value) => ((IDictionary)instance).Add(key, value);

	// Nothing to prime: instances are created through ActivateInstance, not a static initializer.
	public void RunInitializer() { }

	static bool Implements(Type type, Type openGeneric)
		=> type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == openGeneric);

	static Type? ElementOf(Type type, int argument)
	{
		if (type.IsArray) return type.GetElementType();
		var generic = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType
			&& (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(ICollection<>)));
		var arguments = generic?.GetGenericArguments();
		return arguments != null && argument < arguments.Length ? arguments[argument] : typeof(object);
	}
}

sealed class ReflectionXamlMember : IXamlMember
{
	readonly ReflectionXamlMetadataProvider provider;
	readonly PropertyInfo? property;
	readonly MethodInfo? attachedGetter;
	readonly MethodInfo? attachedSetter;
	readonly Type memberType;

	public ReflectionXamlMember(ReflectionXamlMetadataProvider provider, IXamlType target, PropertyInfo property)
	{
		this.provider = provider;
		this.property = property;
		TargetType = target;
		Name = property.Name;
		memberType = property.PropertyType;
		IsReadOnly = !property.CanWrite;
	}

	public ReflectionXamlMember(ReflectionXamlMetadataProvider provider, IXamlType target, string name, MethodInfo? getter, MethodInfo? setter)
	{
		this.provider = provider;
		attachedGetter = getter;
		attachedSetter = setter;
		TargetType = target;
		Name = name;
		IsAttachable = true;
		IsReadOnly = setter is null;
		memberType = getter?.ReturnType ?? setter?.GetParameters().Last().ParameterType ?? typeof(object);
	}

	public string Name { get; }
	public bool IsAttachable { get; }
	public bool IsReadOnly { get; }
	public IXamlType TargetType { get; }
	public IXamlType Type => provider.Resolve(memberType)!;

	// Reporting false keeps the parser on the plain get/set path, which reflection handles for
	// dependency properties just as well as for ordinary ones.
	public bool IsDependencyProperty => false;

	public object GetValue(object instance)
		=> IsAttachable ? attachedGetter!.Invoke(null, new[] { instance })! : property!.GetValue(instance)!;

	public void SetValue(object instance, object value)
	{
		if (IsAttachable) attachedSetter!.Invoke(null, new[] { instance, value });
		else property!.SetValue(instance, value);
	}
}
