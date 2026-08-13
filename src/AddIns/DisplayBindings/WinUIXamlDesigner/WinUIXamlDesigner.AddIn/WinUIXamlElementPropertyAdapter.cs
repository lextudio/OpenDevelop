using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Xml.Linq;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>
/// Exposes a XAML source element to the shell's Properties pad. Deliberately backed by the
/// document rather than by the live ProGPU visual: no <c>Microsoft.UI.Xaml</c> type crosses into
/// the shell, and every edit is a source mutation that can be re-parsed, undone and re-rendered.
/// </summary>
sealed class WinUIXamlElementPropertyAdapter : ICustomTypeDescriptor
{
	readonly XElement element;
	readonly Action<XElement, XName, string> setAttribute;

	/// <summary>
	/// <paramref name="setAttribute"/> routes the write through the designer's edit model rather
	/// than mutating the element here, so a property change goes on the undo stack like any other
	/// edit and re-renders through the same path.
	/// </summary>
	public WinUIXamlElementPropertyAdapter(XElement element, Action<XElement, XName, string> setAttribute)
	{
		this.element = element ?? throw new ArgumentNullException(nameof(element));
		this.setAttribute = setAttribute ?? throw new ArgumentNullException(nameof(setAttribute));
	}

	public override string ToString() => element.Name.LocalName;

	public PropertyDescriptorCollection GetProperties() => GetProperties(null);

	public PropertyDescriptorCollection GetProperties(Attribute[] attributes)
	{
		var descriptors = new List<PropertyDescriptor>();
		foreach (var attribute in element.Attributes()) {
			if (attribute.IsNamespaceDeclaration) continue;
			descriptors.Add(new XamlAttributeDescriptor(element, attribute.Name, setAttribute));
		}
		return new PropertyDescriptorCollection(descriptors.ToArray(), readOnly: true);
	}

	public AttributeCollection GetAttributes() => AttributeCollection.Empty;
	public string GetClassName() => element.Name.LocalName;
	public string GetComponentName() => element.Name.LocalName;
	public TypeConverter GetConverter() => TypeDescriptor.GetConverter(typeof(object));
	public EventDescriptor GetDefaultEvent() => null;
	public PropertyDescriptor GetDefaultProperty() => null;
	public object GetEditor(Type editorBaseType) => null;
	public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
	public EventDescriptorCollection GetEvents(Attribute[] attributes) => EventDescriptorCollection.Empty;
	public object GetPropertyOwner(PropertyDescriptor pd) => this;

	sealed class XamlAttributeDescriptor : PropertyDescriptor
	{
		readonly XElement element;
		readonly XName attributeName;
		readonly Action<XElement, XName, string> setAttribute;

		public XamlAttributeDescriptor(XElement element, XName attributeName, Action<XElement, XName, string> setAttribute)
			: base(attributeName.LocalName, null)
		{
			this.element = element;
			this.attributeName = attributeName;
			this.setAttribute = setAttribute;
		}

		public override Type ComponentType => typeof(WinUIXamlElementPropertyAdapter);
		public override Type PropertyType => typeof(string);
		public override bool IsReadOnly => false;
		public override string Category => attributeName.NamespaceName.Length == 0 ? "XAML" : attributeName.NamespaceName;

		public override object GetValue(object component) => element.Attribute(attributeName)?.Value;

		public override void SetValue(object component, object value) =>
			setAttribute(element, attributeName, value?.ToString());

		public override bool CanResetValue(object component) => false;
		public override void ResetValue(object component) { }
		public override bool ShouldSerializeValue(object component) => true;
	}
}
