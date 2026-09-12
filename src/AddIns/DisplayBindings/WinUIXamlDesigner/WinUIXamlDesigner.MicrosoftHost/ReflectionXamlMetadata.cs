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

	/// <summary>Opt-in trace of every type/member the parser asks for, filtered to one type name
	/// (OD_XAMLMETA_TRACE=AnimatedIcon). The parser reports failures using the name as WRITTEN in
	/// the markup, so without this there is no way to see which lookups it actually made, in what
	/// order, or which IXamlType instance it got back - the questions that decide whether a
	/// "member not found" is a missing member or a type-identity mismatch.</summary>
	internal static readonly string? Trace = Environment.GetEnvironmentVariable("OD_XAMLMETA_TRACE");

	internal static void TraceLine(string message)
	{
		if (!string.IsNullOrEmpty(Trace)) Console.Error.WriteLine("design-host/meta: " + message);
	}

	static bool Traced(string name) => !string.IsNullOrEmpty(Trace) && name.Contains(Trace!, StringComparison.Ordinal);

	public IXamlType GetXamlType(Type type)
	{
		var resolved = Resolve(type);
		if (Traced(type.FullName ?? type.Name))
			TraceLine($"GetXamlType(Type {type.FullName}) -> instance #{resolved?.GetHashCode()}");
		return resolved!;
	}

	public IXamlType GetXamlType(string fullName)
	{
		lock (byName)
		{
			if (byName.TryGetValue(fullName, out var cached))
			{
				if (Traced(fullName)) TraceLine($"GetXamlType(\"{fullName}\") -> cached #{cached?.GetHashCode()}");
				return cached!;
			}
			var found = FindType(fullName);
			var resolved = Resolve(found);
			byName[fullName] = resolved;
			if (Traced(fullName))
				TraceLine($"GetXamlType(\"{fullName}\") -> {(found is null ? "NOT FOUND" : found.FullName)} instance #{resolved?.GetHashCode()}");
			// Logged once per name (the null is cached too). The parser turns an unresolved name into
			// "The type 'X' was not found" using the name as WRITTEN in the markup, which hides the
			// name it actually looked for - and those differ exactly when the failure is interesting:
			// an unprefixed attached property is qualified with whatever `using:` prefix happens to
			// be in scope, so `AnimatedIcon.State` is reported as 'AnimatedIcon' but searched for as
			// 'TheApp.Controls.AnimatedIcon'. Printing the searched name is what makes that visible.
			if (resolved is null)
				Console.Error.WriteLine($"design-host: could not resolve XAML type '{fullName}'.");
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

	/// <summary>CLR namespaces the DEFAULT XAML xmlns maps onto, controls first.</summary>
	static readonly string[] FrameworkNamespaces = [
		"Microsoft.UI.Xaml.Controls",
		"Microsoft.UI.Xaml.Controls.Primitives",
		"Microsoft.UI.Xaml",
		"Microsoft.UI.Xaml.Shapes",
		"Microsoft.UI.Xaml.Media",
		"Microsoft.UI.Xaml.Media.Animation",
		"Microsoft.UI.Xaml.Media.Imaging",
		"Microsoft.UI.Xaml.Documents",
		"Microsoft.UI.Xaml.Data",
		"Microsoft.UI.Xaml.Input",
		"Microsoft.UI.Xaml.Automation",
		"Microsoft.UI.Xaml.Navigation",
		"Microsoft.UI.Composition",
		"Microsoft.UI.Text",
		"Microsoft.UI",
	];


	static Type? FindType(string fullName)
	{
		if (SearchLoadedAssemblies(fullName) is { } exact)
		{
			return exact;
		}
		if (ResolveGenericName(fullName) is { } constructed)
		{
			return constructed;
		}
		if (FindByShortNameInFrameworkNamespaces(fullName) is { } relocated)
		{
			return relocated;
		}
		return null;
	}

	/// <summary>
	/// Constructs a closed generic from XAML's spelling of one, <c>Ns.Type`2&lt;A, B&gt;</c> (what
	/// <c>x:TypeArguments</c> produces). Reflection wants the CLR spelling
	/// (<c>Ns.Type`2[[A],[B]]</c>), so a plain <c>GetType</c> on the XAML form never matches.
	///
	/// Not a corner case: WinUI-Gallery's ControlExample uses CommunityToolkit's
    /// <c>Animation`2&lt;String, System.Numerics.Vector3&gt;</c>, and failing to resolve it makes
	/// the whole control uninstantiable - reported only as "Cannot create instance of type
	/// 'ControlExample'", with the generic name appearing nowhere in the error.
	/// </summary>
	static Type? ResolveGenericName(string fullName)
	{
		var open = fullName.IndexOf('<');
		if (open <= 0 || !fullName.EndsWith(">", StringComparison.Ordinal))
		{
			return null;
		}
		var definition = SearchLoadedAssemblies(fullName.Substring(0, open));
		if (definition is null || !definition.IsGenericTypeDefinition)
		{
			return null;
		}
		var arguments = SplitGenericArguments(fullName.Substring(open + 1, fullName.Length - open - 2));
		if (arguments.Count != definition.GetGenericArguments().Length)
		{
			return null;
		}
		var resolved = new Type[arguments.Count];
		for (var index = 0; index < arguments.Count; index++)
		{
			// Recurses so a nested generic argument resolves too, and falls back to the XAML
			// primitive names (String, Int32, ...) that carry no namespace.
			var argument = arguments[index].Trim();
			if ((FindType(argument) ?? ResolvePrimitive(argument)) is not { } type)
			{
				return null;
			}
			resolved[index] = type;
		}
		try
		{
			return definition.MakeGenericType(resolved);
		}
		catch (Exception)
		{
			// Constraint violation or an unsupported combination: treat as unresolved rather than
			// failing the whole parse.
			return null;
		}
	}

	/// <summary>Splits generic arguments on top-level commas, so a nested
	/// <c>Dictionary`2&lt;String, List`1&lt;Int32&gt;&gt;</c> is not cut inside its own brackets.</summary>
	static List<string> SplitGenericArguments(string text)
	{
		var arguments = new List<string>();
		var depth = 0;
		var start = 0;
		for (var index = 0; index < text.Length; index++)
		{
			switch (text[index])
			{
				case '<': depth++; break;
				case '>': depth--; break;
				case ',' when depth == 0:
					arguments.Add(text.Substring(start, index - start));
					start = index + 1;
					break;
			}
		}
		arguments.Add(text.Substring(start));
		return arguments;
	}

	/// <summary>The unqualified type names XAML accepts for the primitives.</summary>
	static Type? ResolvePrimitive(string name) => name switch {
		"String" => typeof(string),
		"Boolean" => typeof(bool),
		"Int32" => typeof(int),
		"Int64" => typeof(long),
		"Single" => typeof(float),
		"Double" => typeof(double),
		"Byte" => typeof(byte),
		"Char" => typeof(char),
		"Object" => typeof(object),
		_ => null,
	};

	/// <summary>
	/// Retries a miss under the framework namespaces the parser did not consider, keyed on the short
	/// name.
	///
	/// The parser qualifies a default-xmlns name with its own candidate list, and that list omits
	/// <c>Controls.Primitives</c> - so it asks for <c>Microsoft.UI.Xaml.Controls.PivotPanel</c> when
	/// the type is <c>Microsoft.UI.Xaml.Controls.Primitives.PivotPanel</c>. Those types are template
	/// PARTS of the framework's own default styles (Pivot, NavigationView, ComboBox,
	/// CommandBarFlyout), so returning null does not merely drop a style: once a page actually uses
	/// such a control, building its template fails and the child dies natively - no managed
	/// exception, just "The JSON-RPC connection with the remote party was lost".
	///
	/// This was written once before and reverted, because a correctly-qualified property-element
	/// lookup then failed ("The attachable property 'FallbackIconSource' was not found"). That
	/// turned out to be the ambiguous-prefix bug (see XamlPrefixNormalizer) and not this - the
	/// revert was drawn from a measurement with two variables in it. Returning the framework type
	/// under its OWN FullName, through the normal Resolve cache, is what keeps it honest: the alias
	/// experiment that reported a made-up FullName is the part that must not come back.
	/// </summary>
	static readonly bool NamespaceFallbackDisabled =
		Environment.GetEnvironmentVariable("OD_DESIGNHOST_NO_NS_FALLBACK") == "1";

	static Type? FindByShortNameInFrameworkNamespaces(string fullName)
	{
		if (NamespaceFallbackDisabled)
		{
			return null;
		}
		var separator = fullName.LastIndexOf('.');
		if (separator <= 0)
		{
			return null;
		}
		var shortName = fullName.Substring(separator + 1);
		foreach (var candidate in FrameworkNamespaces)
		{
			if (SearchLoadedAssemblies(candidate + "." + shortName) is not { } framework)
			{
				continue;
			}
			// A UIElement found this way is a framework-internal TEMPLATE PART - PivotPanel,
			// PivotHeaderItem, NavigationViewItemPresenter. Handing one back lets the control's
			// default template be built against this reflection metadata, and constructing it then
			// takes the whole child down natively (no managed exception, just "the JSON-RPC
			// connection with the remote party was lost"). Left unresolved, the theme builder drops
			// that one default Style and the control renders untemplated - visibly plain, but alive.
			// Measured: bare <Pivot> crashes with this allowed and renders without it.
			//
			// Everything else found here is a helper, converter or brush (ComboBoxHelper,
			// CornerRadiusFilterConverter, AcrylicBrush) that is never instantiated as a visual, and
            // those DO need resolving - ComboBoxPage fails to render without them.
			if (typeof(UIElement).IsAssignableFrom(framework))
			{
				Console.Error.WriteLine($"design-host: not substituting '{framework.FullName}'"
					+ $" for '{fullName}': constructing a framework template part crashes the host.");
				return null;
			}
			Console.Error.WriteLine($"design-host: '{fullName}' does not exist;"
				+ $" resolved it as '{framework.FullName}'.");
			return framework;
		}
		return null;
	}

	static Type? SearchLoadedAssemblies(string fullName)
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

	/// <summary>
	/// Parses an attribute's text into the member's type.
	///
	/// Two shapes need handling beyond <see cref="Convert.ChangeType(object, Type)"/>. A
	/// nullable-valued XAML property arrives as WinRT's <c>IReference&lt;T&gt;</c>, which projects
	/// to <c>Nullable&lt;T&gt;</c> - neither an enum nor convertible - so it is unwrapped first.
	/// And several types XAML routinely writes as text (TimeSpan above all, in every animation)
	/// implement no IConvertible, so Convert throws for them; those get parsed explicitly. Both
	/// surfaced the same way: "Failed to create a 'Windows.Foundation.IReference`1&lt;TimeSpan&gt;'
	/// from the text '0:0:0.4'".
	/// </summary>
	public object CreateFromString(string value)
	{
		var culture = System.Globalization.CultureInfo.InvariantCulture;
		var target = Nullable.GetUnderlyingType(type) ?? type;
		if (target.IsEnum) return Enum.Parse(target, value, ignoreCase: true);
		if (target == typeof(TimeSpan)) return TimeSpan.Parse(value, culture);
		if (target == typeof(Guid)) return Guid.Parse(value);
		if (target == typeof(Uri)) return new Uri(value, UriKind.RelativeOrAbsolute);
		if (target == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, culture);
		if (target == typeof(DateTime)) return DateTime.Parse(value, culture);
		if (target == typeof(Windows.UI.Color)) return ParseColor(value);
		return Convert.ChangeType(value, target, culture)!;
	}

	/// <summary>
	/// Parses XAML's two color spellings: <c>#AARRGGBB</c>/<c>#RRGGBB</c> hex, and named colors
	/// looked up the same way the framework's own resources do it - as public static properties on
	/// <see cref="Microsoft.UI.Colors"/> (<c>Colors.White</c>, <c>Colors.Red</c>, ...) - the
	/// WinAppSDK projection of this helper, not the <c>Windows.UI.Colors</c> UWP one.
	/// <c>Windows.UI.Color</c> has no <c>IConvertible</c> implementation, so
	/// <c>Convert.ChangeType</c> throws for it outright ("Failed to assign to property
	/// '...AcrylicBrush.TintColor'" was the symptom - WinUI-Gallery's default AcrylicBrush style
	/// sets TintColor from a hex string, and that failed the WHOLE page, not just the brush).
	/// </summary>
	static Windows.UI.Color ParseColor(string value)
	{
		var text = value.Trim();
		if (text.StartsWith("#", System.StringComparison.Ordinal))
		{
			var hex = text.Substring(1);
			byte a = 255, r, g, b;
			if (hex.Length == 6)
			{
				r = System.Convert.ToByte(hex.Substring(0, 2), 16);
				g = System.Convert.ToByte(hex.Substring(2, 2), 16);
				b = System.Convert.ToByte(hex.Substring(4, 2), 16);
			}
			else if (hex.Length == 8)
			{
				a = System.Convert.ToByte(hex.Substring(0, 2), 16);
				r = System.Convert.ToByte(hex.Substring(2, 2), 16);
				g = System.Convert.ToByte(hex.Substring(4, 2), 16);
				b = System.Convert.ToByte(hex.Substring(6, 2), 16);
			}
			else
			{
				throw new System.FormatException($"'{value}' is not a valid #RRGGBB or #AARRGGBB color.");
			}
			return Windows.UI.Color.FromArgb(a, r, g, b);
		}
		if (typeof(Microsoft.UI.Colors).GetProperty(text, BindingFlags.Public | BindingFlags.Static)
			?.GetValue(null) is Windows.UI.Color named)
		{
			return named;
		}
		throw new System.FormatException($"'{value}' is not a recognized color name or #RRGGBB/#AARRGGBB value.");
	}

	public IXamlMember GetMember(string name)
	{
		lock (members)
		{
			if (members.TryGetValue(name, out var cached)) return cached!;
			var member = Build(name);
			members[name] = member;
			if (ReflectionXamlMetadataProvider.Trace is { Length: > 0 } trace
				&& (type.Name.Contains(trace, StringComparison.Ordinal) || name.Contains(trace, StringComparison.Ordinal)))
			{
				ReflectionXamlMetadataProvider.TraceLine($"  {type.FullName}(instance #{GetHashCode()})"
					+ $".GetMember(\"{name}\") -> {(member is null ? "NULL" : $"attachable={member.IsAttachable} readonly={member.IsReadOnly}")}");
			}
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

	// Generic-only collections have to go through reflection: IsCollection accepts ICollection<T>
	// (WinRT projections and toolkit types commonly implement just that), but casting one to the
	// non-generic IList throws - reported as "Cannot add instance of type 'OffsetAnimation' to a
	// collection of type 'ImplicitAnimationSet'", which reads like a content-model rejection.
	public void AddToVector(object instance, object value)
	{
		if (instance is IList list)
		{
			list.Add(value);
			return;
		}
		InvokeCollectionMethod(instance, "Add", value);
	}

	public void AddToMap(object instance, object key, object value)
	{
		if (instance is IDictionary dictionary)
		{
			dictionary.Add(key, value);
			return;
		}
		InvokeCollectionMethod(instance, "Add", key, value);
	}

	static void InvokeCollectionMethod(object instance, string name, params object[] arguments)
	{
		var method = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
			.FirstOrDefault(candidate => candidate.Name == name
				&& candidate.GetParameters().Length == arguments.Length);
		if (method is null)
		{
			throw new InvalidOperationException(
				$"{instance.GetType().FullName} has no {name} accepting {arguments.Length} argument(s).");
		}
		method.Invoke(instance, arguments);
	}

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
