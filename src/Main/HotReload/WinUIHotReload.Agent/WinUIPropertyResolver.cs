using System.Reflection;

namespace WinUIHotReload;

/// <summary>What XAML Diagnostics needs to set one property from its XAML string.</summary>
/// <param name="FullName">Declaring type plus property, e.g. Microsoft.UI.Xaml.FrameworkElement.Margin.</param>
/// <param name="WinRtValueType">The type CreateInstance converts the string to.</param>
/// <param name="IsEvent">The attribute names an event handler, which needs generated code.</param>
internal sealed record ResolvedProperty(string FullName, string WinRtValueType, bool IsEvent);

/// <summary>
/// Turns (element type, XAML attribute name) into the names XAML Diagnostics accepts, using
/// reflection over the application's own CsWinRT projection - already loaded in this process, and
/// always the exact WinAppSDK version the application runs on.
/// </summary>
internal static class WinUIPropertyResolver
{
	public static ResolvedProperty Resolve(string elementTypeName, string propertyName)
	{
		var type = FindType(elementTypeName);
		for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
			var property = current.GetProperty(propertyName,
				BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
			// GetPropertyIndex validates the declaring type against the object, so this has to be
			// the type that declares it ("FrameworkElement.Margin"), not the element's own type.
			if (property != null)
				return new ResolvedProperty(current.FullName + "." + propertyName, WinRtTypeName(property.PropertyType), false);
			if (current.GetEvent(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) != null)
				return new ResolvedProperty(current.FullName + "." + propertyName, "", true);
		}

		// Unknown to reflection (a trimmed projection, or a type we could not find): let WinUI's own
		// metadata have the final say.
		return new ResolvedProperty(elementTypeName + "." + propertyName, "Windows.Foundation.String", false);
	}

	/// <summary>Namespaces the default XAML namespace maps to in WinUI 3.</summary>
	static readonly string[] PresentationNamespaces = {
		"Microsoft.UI.Xaml.Controls",
		"Microsoft.UI.Xaml.Controls.Primitives",
		"Microsoft.UI.Xaml",
		"Microsoft.UI.Xaml.Shapes",
		"Microsoft.UI.Xaml.Documents",
		"Microsoft.UI.Xaml.Media",
		"Microsoft.UI.Xaml.Media.Animation",
		"Microsoft.UI.Xaml.Media.Imaging",
	};

	/// <summary>The CLR type an element name denotes, or null when it cannot be resolved here.</summary>
	public static Type? ResolveElementType(System.Xml.Linq.XName name)
	{
		var ns = name.NamespaceName;
		if (ns.StartsWith("using:", StringComparison.Ordinal))
			return FindType(ns.Substring("using:".Length) + "." + name.LocalName);
		if (ns == "http://schemas.microsoft.com/winfx/2006/xaml/presentation")
			return PresentationNamespaces.Select(n => FindType(n + "." + name.LocalName)).FirstOrDefault(t => t != null);
		return null;
	}

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

	/// <summary>
	/// CreateInstance works on WinRT metadata names. Projected WinUI types keep their names; the
	/// fundamental types are renamed by the projection and are mapped back here.
	/// </summary>
	static string WinRtTypeName(Type type)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;
		if (type == typeof(string) || type == typeof(object) || type == typeof(Uri))
			return "Windows.Foundation.String";
		if (type == typeof(double)) return "Windows.Foundation.Double";
		if (type == typeof(float)) return "Windows.Foundation.Single";
		if (type == typeof(int)) return "Windows.Foundation.Int32";
		if (type == typeof(uint)) return "Windows.Foundation.UInt32";
		if (type == typeof(long)) return "Windows.Foundation.Int64";
		if (type == typeof(bool)) return "Windows.Foundation.Boolean";
		// Brush is abstract; a XAML string ("Red", "#FF0000") always denotes a solid colour brush.
		if (type.FullName == "Microsoft.UI.Xaml.Media.Brush")
			return "Microsoft.UI.Xaml.Media.SolidColorBrush";
		return type.FullName ?? "Windows.Foundation.String";
	}
}
