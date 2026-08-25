using System.ComponentModel;
using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Shell;

/// <summary>WinForms-style common-property view over an ordered group of backend property adapters.</summary>
public sealed class DesignerMultiPropertyAdapter : ICustomTypeDescriptor, IEventBindingHost
{
	readonly object[] targets;
	public DesignerMultiPropertyAdapter(IEnumerable<object> targets) => this.targets = targets?.Where(target => target != null).ToArray() ?? Array.Empty<object>();
	public IReadOnlyList<object> Targets => targets;
	public bool IsMixed(string propertyName)
	{
		var property = GetProperties().Find(propertyName, false);
		return property != null && property.GetValue(this) == null
			&& targets.Select(target => TypeDescriptor.GetProperties(target).Find(propertyName, false)?.GetValue(target)).Distinct().Skip(1).Any();
	}

	public PropertyDescriptorCollection GetProperties()
	{
		if (targets.Length == 0) return PropertyDescriptorCollection.Empty;
		var maps = targets.Select(target => TypeDescriptor.GetProperties(target).Cast<PropertyDescriptor>().ToDictionary(property => property.Name, StringComparer.Ordinal)).ToArray();
		var properties = maps[0].Values.Where(first => maps.Skip(1).All(map => map.TryGetValue(first.Name, out var other) && other.PropertyType == first.PropertyType))
			.Select(first => (PropertyDescriptor)new MultiPropertyDescriptor(targets, maps.Select(map => map[first.Name]).ToArray())).ToArray();
		return new PropertyDescriptorCollection(properties, true);
	}

	public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();
	public AttributeCollection GetAttributes() => AttributeCollection.Empty;
	public string GetClassName() => targets.Length == 0 ? "Selection" : $"{targets.Length} selected objects";
	public string GetComponentName() => GetClassName();
	public TypeConverter? GetConverter() => null;
	public EventDescriptor? GetDefaultEvent() => null;
	public PropertyDescriptor? GetDefaultProperty() => null;
	public object? GetEditor(Type editorBaseType) => null;
	public EventDescriptorCollection GetEvents()
	{
		if (targets.Length == 0) return EventDescriptorCollection.Empty;
		var maps = targets.Select(target => TypeDescriptor.GetEvents(target).Cast<EventDescriptor>().ToDictionary(item => item.Name, StringComparer.Ordinal)).ToArray();
		return new EventDescriptorCollection(maps[0].Values.Where(first => maps.Skip(1).All(map => map.TryGetValue(first.Name, out var other) && other.EventType == first.EventType)).ToArray(), true);
	}
	public EventDescriptorCollection GetEvents(Attribute[]? attributes) => GetEvents();
	public object GetPropertyOwner(PropertyDescriptor? pd) => this;
	void IEventBindingHost.BindEvent(string eventName)
	{
		var hosts = targets.OfType<IEventBindingHost>().ToArray();
		if (hosts.Length != targets.Length)
			throw new InvalidOperationException("Every selected object must support event binding.");
		foreach (var target in hosts)
			target.BindEvent(eventName);
	}

	sealed class MultiPropertyDescriptor : PropertyDescriptor
	{
		readonly object[] targets; readonly PropertyDescriptor[] properties;
		public MultiPropertyDescriptor(object[] targets, PropertyDescriptor[] properties) : base(properties[0]) { this.targets = targets; this.properties = properties; }
		public override Type ComponentType => typeof(DesignerMultiPropertyAdapter);
		public override Type PropertyType => properties[0].PropertyType;
		public override bool IsReadOnly => properties.Any(property => property.IsReadOnly);
		public override bool CanResetValue(object component) => !IsReadOnly && Enumerable.Range(0, properties.Length).All(index => properties[index].CanResetValue(targets[index]));
		public override object? GetValue(object? component) { var first = properties[0].GetValue(targets[0]); return properties.Skip(1).Select((property, index) => property.GetValue(targets[index + 1])).All(value => Equals(value, first)) ? first : null; }
		public override void ResetValue(object component) => ApplyAtomically(component, (property, target) => property.ResetValue(target));
		public override void SetValue(object? component, object? value) { if (!IsReadOnly) ApplyAtomically(component, (property, target) => property.SetValue(target, value)); }
		public override bool ShouldSerializeValue(object component) => properties.Where((property, index) => property.ShouldSerializeValue(targets[index])).Any();
		void ApplyAtomically(object? component, Action<PropertyDescriptor, object> apply)
		{
			var oldValues = properties.Select((property, index) => property.GetValue(targets[index])).ToArray();
			var applied = 0;
			try {
				for (; applied < targets.Length; applied++) apply(properties[applied], targets[applied]);
			} catch {
				for (var index = applied - 1; index >= 0; index--)
					try { properties[index].SetValue(targets[index], oldValues[index]); } catch { }
				throw;
			}
			OnValueChanged(component, EventArgs.Empty);
		}
	}
}
