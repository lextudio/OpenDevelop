using System.Reflection;

namespace WinUIHotReload;

/// <summary>
/// Reaches WinUI through the application's own projection (WinRT.Runtime and Microsoft.WinUI,
/// already loaded in this process) by reflection, so the agent never pins a Windows App SDK
/// version. Everything here must run on the UI thread except <see cref="FromAbi"/>.
/// </summary>
internal static class WinRTBridge
{
	static MethodInfo? fromAbi;
	static MethodInfo? xamlLoad;

	/// <summary>An IInspectable pointer as the projected .NET object (the same instance the app sees).</summary>
	public static object FromAbi(nint inspectable)
	{
		fromAbi ??= FindType("WinRT.MarshalInspectable`1")?.MakeGenericType(typeof(object))
			.GetMethod("FromAbi", BindingFlags.Public | BindingFlags.Static, new[] { typeof(nint) })
			?? throw new InvalidOperationException("The application's WinRT.Runtime has no MarshalInspectable<T>.FromAbi.");
		return fromAbi.Invoke(null, new object[] { inspectable })
			?? throw new InvalidOperationException("The live object could not be projected.");
	}

	/// <summary>WinUI's Microsoft.UI.Xaml.Markup.XamlReader.Load: loose XAML to live objects.</summary>
	public static object LoadXaml(string xaml)
	{
		xamlLoad ??= FindType("Microsoft.UI.Xaml.Markup.XamlReader")
			?.GetMethod("Load", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string) })
			?? throw new InvalidOperationException("Microsoft.UI.Xaml.Markup.XamlReader was not found in the application.");
		try {
			return xamlLoad.Invoke(null, new object[] { xaml })!;
		} catch (TargetInvocationException ex) when (ex.InnerException != null) {
			throw ex.InnerException;
		}
	}

	/// <summary>
	/// The property XAML content of <paramref name="owner"/> goes into: the type's
	/// [ContentProperty] when the projection carries it, else the framework's well-known ones.
	/// </summary>
	public static PropertyInfo? ContentProperty(object owner)
	{
		for (var type = owner.GetType(); type != null && type != typeof(object); type = type.BaseType) {
			foreach (var attribute in type.GetCustomAttributes(inherit: false)) {
				if (attribute.GetType().Name != "ContentPropertyAttribute")
					continue;
				var name = attribute.GetType().GetProperty("Name")?.GetValue(attribute) as string
					?? attribute.GetType().GetField("Name")?.GetValue(attribute) as string;
				if (name != null)
					return owner.GetType().GetProperty(name);
			}
			var known = type.FullName switch {
				"Microsoft.UI.Xaml.Controls.Panel" => "Children",
				"Microsoft.UI.Xaml.Controls.ItemsControl" => "Items",
				"Microsoft.UI.Xaml.Controls.ContentControl" => "Content",
				"Microsoft.UI.Xaml.Controls.UserControl" => "Content",
				"Microsoft.UI.Xaml.Controls.Border" => "Child",
				"Microsoft.UI.Xaml.Controls.Viewbox" => "Child",
				"Microsoft.UI.Xaml.Window" => "Content",
				_ => null,
			};
			if (known != null)
				return owner.GetType().GetProperty(known);
		}
		return null;
	}

	/// <summary>Replaces what <paramref name="property"/> of <paramref name="owner"/> holds.</summary>
	public static void ReplaceContent(object owner, PropertyInfo property, IReadOnlyList<object> children)
	{
		var current = property.GetValue(owner);
		if (IsCollection(property.PropertyType) && current != null) {
			// Projected WinRT collections (UIElementCollection, ItemCollection) are IList<T>, not
			// always the non-generic IList.
			var clear = current.GetType().GetMethod("Clear", Type.EmptyTypes);
			var add = current.GetType().GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1);
			if (clear == null || add == null)
				throw new InvalidOperationException($"{owner.GetType().Name}.{property.Name} cannot be cleared and refilled.");
			clear.Invoke(current, null);
			foreach (var child in children)
				add.Invoke(current, new[] { child });
			return;
		}
		if (children.Count > 1)
			throw new InvalidOperationException($"{owner.GetType().Name}.{property.Name} holds a single element, but the markup has {children.Count}.");
		property.SetValue(owner, children.Count == 0 ? null : children[0]);
	}

	static bool IsCollection(Type type) =>
		type != typeof(string) && type != typeof(object)
		&& type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));

	static Type? FindType(string fullName)
	{
		foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
			try {
				var type = assembly.GetType(fullName, throwOnError: false);
				if (type != null)
					return type;
			} catch {
				// Dynamic or partially loadable assemblies; keep looking.
			}
		}
		return null;
	}
}
