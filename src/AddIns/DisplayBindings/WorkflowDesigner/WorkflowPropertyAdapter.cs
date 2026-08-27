using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WorkflowDesigner;

/// <summary>Adapts one activity's <see cref="DesignerElementNode.Properties"/> (the host's
/// reflection-derived property list - see WorkflowDocument.GetProperties) to the shared
/// Properties pad. Copied from WpfSurfaceElementPropertyAdapter's shape (same problem: a
/// dynamic, per-node property set with no fixed CLR schema to declare) rather than reinvented,
/// since PropertyContainer/PropertyGrid only understands ICustomTypeDescriptor - MewUI's fixed-
/// property adapter shape doesn't fit here because CoreWF activities each have their own
/// property set.</summary>
public sealed class WorkflowPropertyAdapter : ICustomTypeDescriptor
{
	readonly Action<string, string> set;
	readonly DesignerElementNode node;

	public WorkflowPropertyAdapter(DesignerElementNode node, Action<string, string> set)
	{
		this.node = node ?? throw new ArgumentNullException(nameof(node));
		this.set = set ?? throw new ArgumentNullException(nameof(set));
	}

	internal void SetProperty(string propertyName, string value) => set(propertyName, value);

	public string GetClassName() => node.Type;
	public string GetComponentName() => node.Name ?? node.Path;
	public TypeConverter? GetConverter() => null;
	public EventDescriptor? GetDefaultEvent() => null;
	public PropertyDescriptor? GetDefaultProperty() => null;
	public object? GetEditor(Type editorBaseType) => null;
	public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
	public EventDescriptorCollection GetEvents(Attribute[]? attributes) => EventDescriptorCollection.Empty;
	public AttributeCollection GetAttributes() => AttributeCollection.Empty;
	public object GetPropertyOwner(PropertyDescriptor pd) => this;

	public PropertyDescriptorCollection GetProperties() =>
		new(node.Properties.Select(p => (PropertyDescriptor)new WorkflowPropertyDescriptor(this, p)).ToArray(), true);

	public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => GetProperties();
}

sealed class WorkflowPropertyDescriptor : PropertyDescriptor
{
	readonly WorkflowPropertyAdapter owner;
	readonly DesignerPropertyInfo property;

	public WorkflowPropertyDescriptor(WorkflowPropertyAdapter owner, DesignerPropertyInfo property)
		: base(property.Name, new Attribute[] {
			new CategoryAttribute(string.IsNullOrEmpty(property.Category) ? "Misc" : property.Category),
			new ReadOnlyAttribute(property.IsReadOnly)
		})
	{
		this.owner = owner;
		this.property = property;
	}

	public override Type ComponentType => typeof(WorkflowPropertyAdapter);
	public override string DisplayName => string.IsNullOrEmpty(property.DisplayName) ? property.Name : property.DisplayName;
	public override bool IsReadOnly => property.IsReadOnly;
	public override Type PropertyType => typeof(string);
	public override bool CanResetValue(object component) => false;
	public override void ResetValue(object component) { }
	public override bool ShouldSerializeValue(object component) => true;
	public override object? GetValue(object component) => property.Value;

	public override void SetValue(object component, object value)
	{
		var serialized = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
		owner.SetProperty(property.Name, serialized);
		property.Value = serialized;
		OnValueChanged(component, EventArgs.Empty);
	}
}
