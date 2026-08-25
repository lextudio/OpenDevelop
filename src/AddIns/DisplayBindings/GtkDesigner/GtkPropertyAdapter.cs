using System;
using System.ComponentModel;
using System.Linq;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Xceed.Wpf.Toolkit.PropertyGrid;

namespace ICSharpCode.GtkDesigner;

public sealed class GtkPropertyAdapter : ICustomTypeDescriptor, IPropertyGridEventSource, IEventBindingHost
{
	readonly DesignerElementNode node; readonly Action<string, string> set; readonly Action<string, string> setEvent;
	public GtkPropertyAdapter(DesignerElementNode node, Action<string, string> set, Action<string, string>? setEvent = null) { this.node = node; this.set = set; this.setEvent = setEvent ?? set; }
	[Category("Identity")] public string Id { get => node.Id; set => set("$id", value); }
	[Category("Identity"), ReadOnly(true)] public string Class => node.Type;
	[Category("Common")] public string Label { get => Get("label"); set => set("label", value); }
	[Category("Common"), DisplayName("Placeholder text")] public string PlaceholderText { get => Get("placeholder-text"); set => set("placeholder-text", value); }
	[Category("Window")] public string Title { get => Get("title"); set => set("title", value); }
	[Category("Layout")] public string Orientation { get => Get("orientation", "horizontal"); set => set("orientation", value); }
	[Category("Layout")] public string Spacing { get => Get("spacing", "0"); set => set("spacing", value); }
	[Category("Layout"), DisplayName("Margin start")] public string MarginStart { get => Get("margin-start", "0"); set => set("margin-start", value); }
	[Category("Layout"), DisplayName("Margin end")] public string MarginEnd { get => Get("margin-end", "0"); set => set("margin-end", value); }
	[Category("Behavior")] public string Sensitive { get => Get("sensitive", "True"); set => set("sensitive", value.ToLowerInvariant()); }
	[Category("Behavior")] public string Visible { get => Get("visible", "True"); set => set("visible", value.ToLowerInvariant()); }
	string Get(string name, string fallback = "") => node.Properties.FirstOrDefault(p => p.Name == name)?.Value ?? fallback;
	string IPropertyGridEventSource.GetEventHandler(string eventName) => node.Events.FirstOrDefault(e => e.Name == eventName)?.Handler ?? "";
	void IPropertyGridEventSource.SetEventHandler(string eventName, string handlerName) { setEvent(eventName, handlerName); var item = node.Events.FirstOrDefault(e => e.Name == eventName); if (item != null) item.Handler = handlerName; }
	void IEventBindingHost.BindEvent(string eventName) { if (string.IsNullOrEmpty(((IPropertyGridEventSource)this).GetEventHandler(eventName))) ((IPropertyGridEventSource)this).SetEventHandler(eventName, node.Id.TrimStart('$') + "_" + eventName.Replace('-', '_')); }
	public PropertyDescriptorCollection GetProperties() => TypeDescriptor.GetProperties(this, true);
	public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();
	public EventDescriptorCollection GetEvents() => new(node.Events.Select(e => (EventDescriptor)new RemoteEventDescriptor(e)).ToArray(), true);
	public EventDescriptorCollection GetEvents(Attribute[]? attributes) => GetEvents();
	public AttributeCollection GetAttributes() => AttributeCollection.Empty;
	public string GetClassName() => node.Type; public string GetComponentName() => node.Id; public TypeConverter? GetConverter() => null;
	public EventDescriptor? GetDefaultEvent() => GetEvents().Cast<EventDescriptor>().FirstOrDefault(); public PropertyDescriptor? GetDefaultProperty() => null; public object? GetEditor(Type editorBaseType) => null; public object GetPropertyOwner(PropertyDescriptor? pd) => this;
	sealed class RemoteEventDescriptor : EventDescriptor { readonly DesignerEventInfo item; public RemoteEventDescriptor(DesignerEventInfo item) : base(item.Name, new Attribute[] { new CategoryAttribute(item.Category) }) => this.item = item; public override Type ComponentType => typeof(GtkPropertyAdapter); public override Type EventType => typeof(EventHandler); public override bool IsMulticast => false; public override void AddEventHandler(object component, Delegate value) { } public override void RemoveEventHandler(object component, Delegate value) { } public override string Description => item.Handler; }
}
