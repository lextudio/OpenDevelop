using System;
using System.ComponentModel;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.MewUIDesigner;

public sealed class MewUIPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
{
	readonly DesignerElementNode node;
	readonly Action<string, string> set; readonly Action<string, string> setEvent;
	public MewUIPropertyAdapter(DesignerElementNode node, Action<string, string> set, Action<string, string>? setEvent = null) { this.node = node; this.set = set; this.setEvent = setEvent ?? set; }
	[Category("Identity"), ReadOnly(true)] public string Type => node.Type;
	[Category("Identity")] public string Name { get => node.Name ?? node.Id; set => set("$name", value); }
	[Category("Common")] public string Text { get => Get("Text", ""); set => set("Text", value); }
	[Category("Common")] public string Content { get => Get("Content", ""); set => set("Content", value); }
	[Category("Layout")] public string Margin { get => Get("Margin", ""); set => set("Margin", value); }
	[Category("Layout")] public string Padding { get => Get("Padding", ""); set => set("Padding", value); }
	[Category("Layout")] public string Spacing { get => Get("Spacing", ""); set => set("Spacing", value); }
	[Category("Appearance")] public string Background { get => Get("Background", ""); set => set("Background", value); }
	[Category("Appearance")] public string Foreground { get => Get("Foreground", ""); set => set("Foreground", value); }
	[Category("Behavior")] public string IsEnabled { get => Get("IsEnabled", "true"); set => set("IsEnabled", value); }
	string Get(string key, string fallback) => node.Properties.FirstOrDefault(p => p.Name == key)?.Value ?? fallback;
	string IPropertyGridEventSource.GetEventHandler(string eventName) => node.Events.FirstOrDefault(e => string.Equals(e.Name, eventName, StringComparison.OrdinalIgnoreCase))?.Handler ?? "";
	void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName) { setEvent(eventName, handlerName); var item = node.Events.FirstOrDefault(e => string.Equals(e.Name, eventName, StringComparison.OrdinalIgnoreCase)); if (item != null) item.Handler = handlerName; }
	void IEventBindingHost.BindEvent(string eventName) { if (string.IsNullOrEmpty(((IPropertyGridEventSource)this).GetEventHandler(eventName))) ((IPropertyGridEventSource)this).SetEventHandler(eventName, (node.Name ?? node.Id) + "_" + eventName); }
	public PropertyDescriptorCollection GetProperties() => TypeDescriptor.GetProperties(this, true);
	public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();
	public EventDescriptorCollection GetEvents() => new(node.Events.Select(e => (EventDescriptor)new RemoteEventDescriptor(e)).ToArray(), true);
	public EventDescriptorCollection GetEvents(Attribute[]? attributes) => GetEvents(); public AttributeCollection GetAttributes() => AttributeCollection.Empty;
	public string GetClassName() => node.Type; public string GetComponentName() => node.Name ?? node.Id; public TypeConverter? GetConverter() => null; public EventDescriptor? GetDefaultEvent() => GetEvents().Cast<EventDescriptor>().FirstOrDefault(); public PropertyDescriptor? GetDefaultProperty() => null; public object? GetEditor(Type editorBaseType) => null; public object GetPropertyOwner(PropertyDescriptor? pd) => this;
	sealed class RemoteEventDescriptor : EventDescriptor { readonly DesignerEventInfo item; public RemoteEventDescriptor(DesignerEventInfo item) : base(item.Name, new Attribute[] { new CategoryAttribute(item.Category) }) => this.item = item; public override Type ComponentType => typeof(MewUIPropertyAdapter); public override Type EventType => typeof(EventHandler); public override bool IsMulticast => false; public override void AddEventHandler(object component, Delegate value) { } public override void RemoveEventHandler(object component, Delegate value) { } public override string Description => item.Handler; }
}
