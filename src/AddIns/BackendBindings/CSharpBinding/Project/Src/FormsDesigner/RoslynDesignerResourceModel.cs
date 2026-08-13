using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Globalization;

namespace CSharpBinding.FormsDesigner
{
	/// <summary>Resource projection used by the Roslyn loader without CodeDOM serialization.</summary>
	sealed class RoslynDesignerResourceModel
	{
		readonly IResourceService service;
		readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

		public RoslynDesignerResourceModel(IServiceProvider services)
		{
			service = services.GetService(typeof(IResourceService)) as IResourceService;
			var reader = service?.GetResourceReader(CultureInfo.InvariantCulture);
			if (reader == null) return;
			using (reader) foreach (DictionaryEntry item in reader) values[(string)item.Key] = item.Value;
		}

		public void Apply(IComponent component, string resourceName)
		{
			foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component)) {
				if (property.IsReadOnly) continue;
				if (values.TryGetValue(resourceName + "." + property.Name, out var value) && (value == null || property.PropertyType.IsInstanceOfType(value)))
					property.SetValue(component, value);
			}
		}

		public void Write(IComponent component, string resourceName)
		{
			var writer = service?.GetResourceWriter(CultureInfo.InvariantCulture);
			if (writer == null) return;
			foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(component)) {
				if (!property.IsReadOnly && property.ShouldSerializeValue(component)) {
					var value = property.GetValue(component);
					if (value == null || value.GetType().IsSerializable)
						writer.AddResource(resourceName + "." + property.Name, value);
				}
			}
			writer.Generate();
		}
	}
}
