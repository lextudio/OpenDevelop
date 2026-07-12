// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;

using ICSharpCode.WpfDesign;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// Wraps a <see cref="DesignItem"/> (the WPF designer's own property abstraction, which is
	/// not backed by real CLR properties) as an <see cref="ICustomTypeDescriptor"/> so it can be
	/// bound directly to Xceed's <c>PropertyGrid.SelectedObject</c> — the grid's default
	/// reflection-based discovery goes through <see cref="TypeDescriptor.GetProperties(object)"/>,
	/// which honors <see cref="ICustomTypeDescriptor"/> when the object implements it.
	/// </summary>
	public sealed class DesignItemPropertyGridAdapter : ICustomTypeDescriptor
	{
		public DesignItem DesignItem { get; }

		public DesignItemPropertyGridAdapter(DesignItem designItem)
		{
			DesignItem = designItem ?? throw new ArgumentNullException(nameof(designItem));
		}

		public PropertyDescriptorCollection GetProperties()
		{
			var descriptors = DesignItem.Properties
				.Where(p => !p.IsEvent)
				.Select(p => (PropertyDescriptor)new DesignItemPropertyDescriptor(p))
				.ToArray();
			return new PropertyDescriptorCollection(descriptors);
		}

		public PropertyDescriptorCollection GetProperties(Attribute[] attributes) => GetProperties();

		public string GetClassName() => DesignItem.ComponentType.Name;
		public string GetComponentName() => DesignItem.Name;
		public TypeConverter GetConverter() => null;
		public EventDescriptor GetDefaultEvent() => null;
		public PropertyDescriptor GetDefaultProperty() => null;
		public object GetEditor(Type editorBaseType) => null;
		public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
		public EventDescriptorCollection GetEvents(Attribute[] attributes) => EventDescriptorCollection.Empty;
		public AttributeCollection GetAttributes() => AttributeCollection.Empty;
		public object GetPropertyOwner(PropertyDescriptor pd) => this;
	}

	sealed class DesignItemPropertyDescriptor : PropertyDescriptor
	{
		readonly DesignItemProperty property;

		public DesignItemPropertyDescriptor(DesignItemProperty property)
			: base(property.Name, BuildAttributes(property))
		{
			this.property = property;
		}

		static Attribute[] BuildAttributes(DesignItemProperty property)
		{
			return new Attribute[] {
				new CategoryAttribute(string.IsNullOrEmpty(property.Category) ? "Misc" : property.Category)
			};
		}

		public override Type ComponentType => property.DesignItem.ComponentType;
		public override bool IsReadOnly => false;
		public override Type PropertyType => property.ReturnType;

		public override bool CanResetValue(object component) => property.IsSet;
		public override void ResetValue(object component) => property.Reset();
		public override bool ShouldSerializeValue(object component) => property.IsSet;

		public override object GetValue(object component) => property.ValueOnInstance;

		public override void SetValue(object component, object value)
		{
			property.SetValue(value);
			OnValueChanged(component, EventArgs.Empty);
		}
	}
}
