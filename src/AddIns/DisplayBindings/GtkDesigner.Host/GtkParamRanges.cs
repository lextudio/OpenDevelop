using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ICSharpCode.GtkDesigner.Host;

/// <summary>
/// Numeric property limits from GTK's own GParamSpec - GIR does not carry them (Stetic read them
/// the same way, libstetic/ParamSpec.cs). Gir.Core 0.8 does not expose GParamSpecInt/Double's
/// minimum and maximum, so they are read from the native structs: g_type_class_ref +
/// g_object_class_find_property, then the fields after the 72-byte GParamSpec header (64-bit
/// layout; on any other pointer size no range is reported rather than guessed). Verified against
/// GtkWidget:opacity 0..1, GtkWidget:margin-start 0..32767, GtkScale:digits -1..64.
/// </summary>
static unsafe class GtkParamRanges
{
	const int ParamSpecHeader = 72;
	static readonly ConcurrentDictionary<(string Type, string Property), (double Min, double Max)?> cache = new();
	static readonly Lazy<Natives?> natives = new(LoadNatives);

	sealed class Natives
	{
		public delegate* unmanaged<nuint, nint> ClassRef;
		public delegate* unmanaged<nint, byte*, nint> FindProperty;
		public delegate* unmanaged<nuint, nint> TypeName;
	}

	static Natives? LoadNatives()
	{
		if (IntPtr.Size != 8) return null;
		var name = OperatingSystem.IsWindows() ? "libgobject-2.0-0.dll" : OperatingSystem.IsMacOS() ? "libgobject-2.0.0.dylib" : "libgobject-2.0.so.0";
		if (!NativeLibrary.TryLoad(name, out var library)) return null;
		return new Natives {
			ClassRef = (delegate* unmanaged<nuint, nint>)NativeLibrary.GetExport(library, "g_type_class_ref"),
			FindProperty = (delegate* unmanaged<nint, byte*, nint>)NativeLibrary.GetExport(library, "g_object_class_find_property"),
			TypeName = (delegate* unmanaged<nuint, nint>)NativeLibrary.GetExport(library, "g_type_name"),
		};
	}

	/// <summary>The [min, max] GTK enforces for a numeric property of the GIR class
	/// <paramref name="qualifiedType"/> ("Gtk.Box"), or null when it has none or it cannot be read.</summary>
	public static (double Min, double Max)? RangeOf(string qualifiedType, string property)
		=> cache.GetOrAdd((qualifiedType, property), key => Read(key.Type, key.Property));

	static (double Min, double Max)? Read(string qualifiedType, string property)
	{
		try {
			if (natives.Value is not { } n || GTypeOf(qualifiedType) is not { } gtype) return null;
			var klass = n.ClassRef(gtype);
			if (klass == 0) return null;
			var bytes = System.Text.Encoding.UTF8.GetBytes(property + "\0");
			nint spec; fixed (byte* p = bytes) spec = n.FindProperty(klass, p);
			if (spec == 0) return null;
			var specType = Marshal.PtrToStringUTF8(n.TypeName(*(nuint*)*(nint*)spec));   // GTypeInstance.g_class->g_type
			var fields = spec + ParamSpecHeader;
			return specType switch {
				"GParamInt" => (*(int*)fields, *(int*)(fields + 4)),
				"GParamUInt" => (*(uint*)fields, *(uint*)(fields + 4)),
				"GParamLong" or "GParamInt64" => (*(long*)fields, *(long*)(fields + 8)),
				"GParamULong" or "GParamUInt64" => (*(ulong*)fields, *(ulong*)(fields + 8)),
				"GParamFloat" => (*(float*)fields, *(float*)(fields + 4)),
				"GParamDouble" => (*(double*)fields, *(double*)(fields + 8)),
				_ => null
			};
		} catch (Exception) {
			return null;   // a range is a refinement; never let it break the property list
		}
	}

	/// <summary>The GType of a Gir.Core class by its GIR name, via its static GetGType().</summary>
	static nuint? GTypeOf(string qualifiedType)
	{
		var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(qualifiedType, false)).FirstOrDefault(t => t != null);
		var result = type?.GetMethod("GetGType", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)?.Invoke(null, null);
		if (result == null) return null;
		var toNuint = result.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static)
			.FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == typeof(nuint) && m.GetParameters().Length == 1);
		return toNuint?.Invoke(null, new[] { result }) as nuint?;
	}

	/// <summary>"0 to 32767" for the description - omitted when the range is just the C type's
	/// full span (int, uint), which says nothing a numeric editor does not already enforce.</summary>
	public static string? Describe((double Min, double Max) range)
	{
		var full = (range.Min == int.MinValue && range.Max == int.MaxValue) || (range.Min == 0 && range.Max == uint.MaxValue)
			|| (range.Min == long.MinValue && range.Max == long.MaxValue) || double.IsInfinity(range.Max) || range.Max >= double.MaxValue;
		if (full) return null;
		static string F(double v) => v == int.MaxValue ? "int.MaxValue" : v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
		return F(range.Min) + " to " + F(range.Max);
	}
}
