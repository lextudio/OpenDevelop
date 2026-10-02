using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.GtkDesigner;

/// <summary>
/// The Properties-pad object for one GtkBuilder element. Its properties are not hand-written:
/// the design host reports every property the GIR catalogue knows for the class (plus any the
/// file sets), each typed by <see cref="DesignerPropertyInfo.Kind"/>, and this adapter turns
/// them into descriptors the property grid edits natively - a check box for booleans, a numeric
/// editor for numbers, a drop-down for enums - grouped by the GTK class that declares them.
/// Stetic's property editor table (PropertyEditorCell: bool, int ranges, float ranges, enum vs
/// flags) is the model; its GTK 2 widgets are not.
/// </summary>
public sealed class GtkPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
{
	readonly DesignerElementNode node; readonly Action<string, string> set; readonly Action<string, string> setEvent; readonly Action<string>? reset;
	PropertyDescriptorCollection? properties;

	public GtkPropertyAdapter(DesignerElementNode node, Action<string, string> set, Action<string, string>? setEvent = null, Action<string>? reset = null)
	{
		this.node = node; this.set = set; this.setEvent = setEvent ?? set; this.reset = reset;
	}

	[Category("Identity")] public string Id { get => node.Id; set => set("$id", value); }
	[Category("Identity"), ReadOnly(true)] public string Class => node.Type;

	internal void Set(string gtkName, string value) => set(gtkName, value);
	internal bool CanReset => reset != null;
	internal void Reset(string gtkName) => reset?.Invoke(gtkName);

	/// <summary>"placeholder-text" -> "PlaceholderText": the descriptor name (Gir.Core's and .NET's
	/// convention, and what automation addresses); the grid shows <see cref="DisplayNameOf"/>.</summary>
	/// A child's &lt;layout&gt; property ("layout:column-span") becomes "LayoutColumnSpan".
	internal static string DescriptorName(string gtkName)
	{
		var layout = gtkName.StartsWith(LayoutPrefix, StringComparison.Ordinal);
		var name = layout ? gtkName.Substring(LayoutPrefix.Length) : gtkName;
		return (layout ? "Layout" : "") + string.Concat(name.Split('-', '_').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));
	}

	/// <summary>The host's DDP prefix for a child's &lt;layout&gt; property.</summary>
	internal const string LayoutPrefix = "layout:";

	/// <summary>"placeholder-text" -> "Placeholder text"; "layout:column-span" -> "Column span"
	/// (its category, "Layout (GtkGrid)", already says which container it belongs to).</summary>
	internal static string DisplayNameOf(string gtkName)
	{
		if (gtkName.StartsWith(LayoutPrefix, StringComparison.Ordinal)) gtkName = gtkName.Substring(LayoutPrefix.Length);
		var words = gtkName.Replace('_', '-').Split('-').Where(p => p.Length > 0).ToArray();
		return words.Length == 0 ? gtkName : char.ToUpperInvariant(words[0][0]) + words[0].Substring(1) + (words.Length > 1 ? " " + string.Join(" ", words.Skip(1)) : "");
	}

	public PropertyDescriptorCollection GetProperties()
	{
		if (properties != null) return properties;
		var list = new List<PropertyDescriptor>(TypeDescriptor.GetProperties(this, true).Cast<PropertyDescriptor>());
		var names = new HashSet<string>(list.Select(p => p.Name), StringComparer.Ordinal);
		foreach (var info in node.Properties.Where(p => p.Name != "$id"))
			if (names.Add(DescriptorName(info.Name)))
				list.Add(new GtkPropertyDescriptor(this, info));
		return properties = new PropertyDescriptorCollection(list.ToArray(), true);
	}

	public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();

	string IPropertyGridEventSource.GetEventHandler(string eventName) => node.Events.FirstOrDefault(e => e.Name == eventName)?.Handler ?? "";
	void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName) { setEvent(eventName, handlerName); var item = node.Events.FirstOrDefault(e => e.Name == eventName); if (item != null) item.Handler = handlerName; }
	void IEventBindingHost.BindEvent(string eventName) { if (string.IsNullOrEmpty(((IPropertyGridEventSource)this).GetEventHandler(eventName))) ((IPropertyGridEventSource)this).SetEventHandler(eventName, node.Id.TrimStart('$') + "_" + eventName.Replace('-', '_')); }
	public EventDescriptorCollection GetEvents() => new(node.Events.Select(e => (EventDescriptor)new RemoteEventDescriptor(e)).ToArray(), true);
	public EventDescriptorCollection GetEvents(Attribute[]? attributes) => GetEvents();
	public AttributeCollection GetAttributes() => AttributeCollection.Empty;
	public string GetClassName() => node.Type; public string GetComponentName() => node.Id; public TypeConverter? GetConverter() => null;
	public EventDescriptor? GetDefaultEvent() => GetEvents().Cast<EventDescriptor>().FirstOrDefault(); public PropertyDescriptor? GetDefaultProperty() => null; public object? GetEditor(Type editorBaseType) => null; public object GetPropertyOwner(PropertyDescriptor? pd) => this;
	sealed class RemoteEventDescriptor : EventDescriptor { readonly DesignerEventInfo item; public RemoteEventDescriptor(DesignerEventInfo item) : base(item.Name, new Attribute[] { new CategoryAttribute(item.Category) }) => this.item = item; public override Type ComponentType => typeof(GtkPropertyAdapter); public override Type EventType => typeof(EventHandler); public override bool IsMulticast => false; public override void AddEventHandler(object component, Delegate value) { } public override void RemoveEventHandler(object component, Delegate value) { } public override string Description => item.Handler; }
}

/// <summary>One GIR-described GTK property. The value travels as GtkBuilder text; the host has
/// already type-checked and canonicalised it, and re-checks every edit before it is written.</summary>
sealed class GtkPropertyDescriptor : PropertyDescriptor
{
	readonly GtkPropertyAdapter owner;
	readonly DesignerPropertyInfo info;
	readonly Type type;

	public GtkPropertyDescriptor(GtkPropertyAdapter owner, DesignerPropertyInfo info)
		: base(GtkPropertyAdapter.DescriptorName(info.Name), Attributes(info))
	{
		this.owner = owner; this.info = info;
		type = info.Kind switch {
			"Boolean" => typeof(bool),
			// Gdk.RGBA: the grid's color picker; the value converts to and from GTK color text.
			"Color" => typeof(System.Windows.Media.Color),
			// GIR's fundamental type decides integer vs floating point; the grid's numeric editor follows.
			"Number" => info.TypeName is "gfloat" or "gdouble" ? typeof(double) : info.TypeName.StartsWith("gu", StringComparison.Ordinal) ? typeof(ulong) : typeof(long),
			_ => typeof(string)
		};
	}

	static Attribute[] Attributes(DesignerPropertyInfo info)
	{
		var attributes = new List<Attribute> {
			new CategoryAttribute(string.IsNullOrEmpty(info.Category) ? "GTK" : info.Category),
			new DisplayNameAttribute(GtkPropertyAdapter.DisplayNameOf(info.Name)),
			// The GTK name stays visible: it is what the .ui file, the GTK docs and code use.
			new DescriptionAttribute(string.IsNullOrEmpty(info.Description) ? info.Name : info.Name + " - " + info.Description),
			new ReadOnlyAttribute(info.IsReadOnly)
		};
		if (info.Kind == "Enum" && info.AllowedValues.Count > 0)
			attributes.Add(new TypeConverterAttribute(typeof(GtkEnumConverter)));
		return attributes.ToArray();
	}

	internal IReadOnlyList<string> AllowedValues => info.AllowedValues;
	/// <summary>Flags combine with '|', so their text is free; a plain enum is one of its nicks.</summary>
	internal bool IsExclusiveChoice => info.IsEnum && info.AllowedValues.Count > 0;

	public override Type ComponentType => typeof(GtkPropertyAdapter);
	public override Type PropertyType => type;
	public override bool IsReadOnly => info.IsReadOnly;
	public override bool CanResetValue(object component) => owner.CanReset && info.ShouldSerialize && !info.IsReadOnly;
	public override bool ShouldSerializeValue(object component) => info.ShouldSerialize;

	public override object? GetValue(object component)
	{
		var text = info.Value ?? "";
		if (type == typeof(bool)) return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
		if (type == typeof(double)) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
		if (type == typeof(long)) return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0L;
		if (type == typeof(ulong)) return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0UL;
		if (type == typeof(System.Windows.Media.Color)) return GtkColorText.Parse(text) ?? System.Windows.Media.Colors.Transparent;
		return text;
	}

	public override void SetValue(object? component, object? value)
	{
		var text = value switch {
			bool b => b ? "True" : "False",
			System.Windows.Media.Color c => GtkColorText.Format(c),
			double d => d.ToString("R", CultureInfo.InvariantCulture),
			IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
			_ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
		};
		owner.Set(info.Name, text);
		// The grid re-reads right after commit; the snapshot this descriptor holds is the one taken
		// at selection time, so reflect the accepted edit (an exception would have left it as-is).
		info.Value = text; info.ShouldSerialize = true;
		OnValueChanged(component, EventArgs.Empty);
	}

	public override void ResetValue(object component)
	{
		owner.Reset(info.Name);
		info.ShouldSerialize = false;
		OnValueChanged(component, EventArgs.Empty);
	}
}

/// <summary>GTK color text (what GtkBuilder hands to gdk_rgba_parse) to and from a WPF color, so
/// a Gdk.RGBA property gets the grid's color picker. GTK's hex order is RGB(A) - alpha LAST,
/// unlike WPF's #AARRGGBB.</summary>
static class GtkColorText
{
	public static System.Windows.Media.Color? Parse(string text)
	{
		var t = text.Trim();
		if (t.StartsWith("#", StringComparison.Ordinal)) {
			var hex = t.Substring(1);
			if (!hex.All(Uri.IsHexDigit)) return null;
			int Digit(int i, int width) { var v = Convert.ToInt32(hex.Substring(i * width, width), 16); return width == 1 ? v * 17 : v; }
			switch (hex.Length) {
				case 3: return System.Windows.Media.Color.FromArgb(255, (byte)Digit(0, 1), (byte)Digit(1, 1), (byte)Digit(2, 1));
				case 4: return System.Windows.Media.Color.FromArgb((byte)Digit(3, 1), (byte)Digit(0, 1), (byte)Digit(1, 1), (byte)Digit(2, 1));
				case 6: return System.Windows.Media.Color.FromArgb(255, (byte)Digit(0, 2), (byte)Digit(1, 2), (byte)Digit(2, 2));
				case 8: return System.Windows.Media.Color.FromArgb((byte)Digit(3, 2), (byte)Digit(0, 2), (byte)Digit(1, 2), (byte)Digit(2, 2));
				default: return null;
			}
		}
		if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && t.IndexOf('(') is var open and > 0 && t.EndsWith(")", StringComparison.Ordinal)) {
			var parts = t.Substring(open + 1, t.Length - open - 2).Split(',').Select(p => p.Trim()).ToArray();
			if (parts.Length is not (3 or 4)) return null;
			static byte Channel(string p) => p.EndsWith("%", StringComparison.Ordinal)
				? (byte)Math.Round(Math.Clamp(double.Parse(p.TrimEnd('%'), CultureInfo.InvariantCulture), 0, 100) * 2.55)
				: (byte)Math.Clamp(double.Parse(p, CultureInfo.InvariantCulture), 0, 255);
			try {
				var alpha = parts.Length == 4 ? (byte)Math.Round(Math.Clamp(double.Parse(parts[3], CultureInfo.InvariantCulture), 0, 1) * 255) : (byte)255;
				return System.Windows.Media.Color.FromArgb(alpha, Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
			} catch (FormatException) { return null; }
		}
		// CSS color names: WPF's named colors are the same X11/CSS set.
		var named = typeof(System.Windows.Media.Colors).GetProperty(t, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.IgnoreCase);
		return named?.GetValue(null) is System.Windows.Media.Color c ? c : null;
	}

	public static string Format(System.Windows.Media.Color c) => c.A == 255
		? $"#{c.R:x2}{c.G:x2}{c.B:x2}"
		: string.Create(CultureInfo.InvariantCulture, $"rgba({c.R},{c.G},{c.B},{Math.Round(c.A / 255.0, 3)})");
}

/// <summary>Drop-down choices for a GTK enum property: its GtkBuilder nicks, from the GIR.</summary>
sealed class GtkEnumConverter : StringConverter
{
	public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => context?.PropertyDescriptor is GtkPropertyDescriptor;
	public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => context?.PropertyDescriptor is GtkPropertyDescriptor { IsExclusiveChoice: true };
	public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
		=> new((context?.PropertyDescriptor as GtkPropertyDescriptor)?.AllowedValues.ToArray() ?? Array.Empty<string>());
}
