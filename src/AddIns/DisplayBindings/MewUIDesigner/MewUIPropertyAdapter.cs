using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.MewUIDesigner;

/// <summary>Snapshot-driven Properties-pad adapter for MewUI. The host owns the supported
/// attribute set and type kind; this side only turns that contract into native property-grid
/// descriptors, so newly published MewUI attributes do not require another hand-written pad row.</summary>
public sealed class MewUIPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
{
	readonly DesignerElementNode node;
	readonly Func<string, string, bool> set;
	readonly Func<string, string, bool> setEvent;
	PropertyDescriptorCollection? properties;

	public MewUIPropertyAdapter(DesignerElementNode node, Func<string, string, bool> set, Func<string, string, bool>? setEvent = null)
	{
		this.node = node;
		this.set = set;
		this.setEvent = setEvent ?? set;
	}

	internal bool Set(string name, string value) => set(name, value);
	[Category("Identity"), ReadOnly(true)] public string Type => node.Type;

	public PropertyDescriptorCollection GetProperties()
	{
		if (properties != null) return properties;
		var list = TypeDescriptor.GetProperties(this, true).Cast<PropertyDescriptor>().ToList();
		var names = new HashSet<string>(node.Properties.Select(info => info.Name), StringComparer.Ordinal);
		var infos = node.Properties.Concat(FallbackProperties().Where(info => names.Add(info.Name)));
		list.AddRange(infos.Select(info => (PropertyDescriptor)new MewUIPropertyDescriptor(this, info)));
		return properties = new PropertyDescriptorCollection(list.ToArray(), true);
	}
	public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();

	// The source-only host can report only attributes that already exist. Preserve the old pad's
	// ability to add its fundamental properties, while all additional host-published attributes
	// remain fully data-driven. Text formatting stays textual: MewUI's accepted Color/Thickness
	// grammar is its own source contract, not WPF's formatter contract.
	IEnumerable<DesignerPropertyInfo> FallbackProperties()
	{
		yield return new DesignerPropertyInfo { Name = "$name", DisplayName = "Name", Category = "Identity" };
		yield return new DesignerPropertyInfo { Name = "Text", Category = "Common" };
		yield return new DesignerPropertyInfo { Name = "Content", Category = "Common" };
		yield return new DesignerPropertyInfo { Name = "Margin", Category = "Layout" };
		yield return new DesignerPropertyInfo { Name = "Padding", Category = "Layout" };
		yield return new DesignerPropertyInfo { Name = "Spacing", Category = "Layout", Kind = "Number", Value = "0" };
		yield return new DesignerPropertyInfo { Name = "Background", Category = "Appearance" };
		yield return new DesignerPropertyInfo { Name = "Foreground", Category = "Appearance" };
		yield return new DesignerPropertyInfo { Name = "IsEnabled", Category = "Behavior", Kind = "Boolean", Value = "true" };
		if (node.Type == "StackPanel")
			yield return new DesignerPropertyInfo { Name = "Orientation", Category = "Layout", Kind = "Enum", Value = "Vertical", AllowedValues = { "Horizontal", "Vertical" } };
	}

	string IPropertyGridEventSource.GetEventHandler(string eventName)
		=> node.Events.FirstOrDefault(e => string.Equals(e.Name, eventName, StringComparison.OrdinalIgnoreCase))?.Handler ?? "";
	void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName)
	{
		var item = node.Events.FirstOrDefault(e => string.Equals(e.Name, eventName, StringComparison.OrdinalIgnoreCase));
		if (item != null && item.Handler != handlerName && setEvent(eventName, handlerName)) item.Handler = handlerName;
	}
	void IEventBindingHost.BindEvent(string eventName)
	{
		if (string.IsNullOrEmpty(((IPropertyGridEventSource)this).GetEventHandler(eventName)))
			((IPropertyGridEventSource)this).SetEventHandler(eventName, (node.Name ?? node.Id) + "_" + eventName);
	}
	public EventDescriptorCollection GetEvents() => new(node.Events.Select(e => (EventDescriptor)new RemoteEventDescriptor(e)).ToArray(), true);
	public EventDescriptorCollection GetEvents(Attribute[]? attributes) => GetEvents();
	public AttributeCollection GetAttributes() => AttributeCollection.Empty;
	public string GetClassName() => node.Type;
	public string GetComponentName() => node.Name ?? node.Id;
	public TypeConverter? GetConverter() => null;
	public EventDescriptor? GetDefaultEvent() => GetEvents().Cast<EventDescriptor>().FirstOrDefault();
	public PropertyDescriptor? GetDefaultProperty() => null;
	public object? GetEditor(Type editorBaseType) => null;
	public object GetPropertyOwner(PropertyDescriptor? pd) => this;

	sealed class RemoteEventDescriptor : EventDescriptor
	{
		readonly DesignerEventInfo item;
		public RemoteEventDescriptor(DesignerEventInfo item) : base(item.Name, new Attribute[] { new CategoryAttribute(item.Category) }) => this.item = item;
		public override Type ComponentType => typeof(MewUIPropertyAdapter);
		public override Type EventType => typeof(EventHandler);
		public override bool IsMulticast => false;
		public override void AddEventHandler(object component, Delegate value) { }
		public override void RemoveEventHandler(object component, Delegate value) { }
		public override string Description => item.Handler;
	}
}

sealed class MewUIPropertyDescriptor : PropertyDescriptor
{
	readonly MewUIPropertyAdapter owner;
	readonly DesignerPropertyInfo info;
	readonly Type type;

	public MewUIPropertyDescriptor(MewUIPropertyAdapter owner, DesignerPropertyInfo info)
		: base(DescriptorName(info), CreateAttributes(info))
	{
		this.owner = owner; this.info = info;
		type = info.Kind switch {
			"Boolean" when bool.TryParse(info.Value, out _) => typeof(bool),
			"Number" when double.TryParse(info.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => typeof(double),
			_ => typeof(string)
		};
	}

	static string DescriptorName(DesignerPropertyInfo info) => info.Name == "$name" ? "Name" : info.Name;
	static Attribute[] CreateAttributes(DesignerPropertyInfo info)
	{
		var attributes = new List<Attribute> {
			new CategoryAttribute(string.IsNullOrEmpty(info.Category) ? "MewUI" : info.Category),
			new DisplayNameAttribute(string.IsNullOrEmpty(info.DisplayName) ? DescriptorName(info) : info.DisplayName),
			new DescriptionAttribute(info.Description ?? ""), new ReadOnlyAttribute(IsProtocolReadOnly(info))
		};
		if (info.Kind == "Enum" && info.AllowedValues.Count > 0) attributes.Add(new TypeConverterAttribute(typeof(MewUIEnumConverter)));
		return attributes.ToArray();
	}
	static bool IsProtocolReadOnly(DesignerPropertyInfo info) => info.IsReadOnly || info.Kind is "Unsupported" or "Reference" or "ReadOnly";
	internal IReadOnlyList<string> AllowedValues => info.AllowedValues;
	public override Type ComponentType => typeof(MewUIPropertyAdapter);
	public override Type PropertyType => type;
	public override bool IsReadOnly => IsProtocolReadOnly(info);
	public override bool CanResetValue(object component) => false;
	public override void ResetValue(object component) { }
	public override bool ShouldSerializeValue(object component) => info.ShouldSerialize;
	public override object? GetValue(object component)
	{
		if (info.IsNull) return null;
		if (type == typeof(string)) return info.Value;
		try { var converter = TypeDescriptor.GetConverter(type); return converter.CanConvertFrom(typeof(string)) ? converter.ConvertFromInvariantString(info.Value) : info.Value; }
		catch (Exception) { return info.Value; }
	}
	public override void SetValue(object component, object value)
	{
		if (IsReadOnly) return;
		var converter = TypeDescriptor.GetConverter(type);
		var text = converter.CanConvertTo(typeof(string)) ? converter.ConvertToInvariantString(value) ?? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
		if (!owner.Set(info.Name, text)) return;
		info.Value = text; info.IsNull = false; info.ShouldSerialize = true;
		OnValueChanged(component, EventArgs.Empty);
	}
}

sealed class MewUIEnumConverter : StringConverter
{
	public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => context?.PropertyDescriptor is MewUIPropertyDescriptor;
	public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => context?.PropertyDescriptor is MewUIPropertyDescriptor;
	public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
		=> new((context?.PropertyDescriptor as MewUIPropertyDescriptor)?.AllowedValues.ToArray() ?? Array.Empty<string>());
}
