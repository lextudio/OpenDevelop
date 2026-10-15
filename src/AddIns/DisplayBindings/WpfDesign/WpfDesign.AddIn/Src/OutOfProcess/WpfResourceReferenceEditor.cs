#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Presentation;

using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>
	/// Properties-pad row for a property that points at a resource (<c>Style="{StaticResource key}"</c>, an unset Style or template): a
	/// drop-down of "(none)" and, for every resource in scope whose value fits the property's type, its <c>{StaticResource key}</c> and
	/// <c>{DynamicResource key}</c> forms. Picking one commits through <c>design/set-resource-reference</c>.
	/// </summary>
	public sealed class WpfResourceReferenceEditor : ITypeEditor
	{
		internal const string ComboName = "ResourceReferenceCombo";
		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var keys = (propertyItem.PropertyDescriptor as WpfSurfacePropertyDescriptor)?.ResourceReferenceKeys ?? new List<string>();
			var current = propertyItem.Value as string;
			var entries = ResourceReferenceEntries.Entries(keys);
			// A reference to a resource that is not offered (its dictionary moved) is still shown, so the row never lies.
			if (!string.IsNullOrEmpty(current) && !entries.Contains(current))
				entries.Add(current);
			var combo = new ComboBox { Name = ComboName, ItemsSource = entries, IsEnabled = propertyItem.Instance is WpfSurfaceElementPropertyAdapter };
			combo.SelectedItem = string.IsNullOrEmpty(current) ? ResourceReferenceEntries.NoneEntry : current;
			combo.SelectionChanged += (_, _) => {
				if (combo.SelectedItem is not string entry || entry == (string.IsNullOrEmpty(current) ? ResourceReferenceEntries.NoneEntry : current)
					|| !ResourceReferenceEntries.TryParse(entry, out var kind, out var key))
					return;
				try {
					(propertyItem.Instance as WpfSurfaceElementPropertyAdapter)?.SetResourceReference(propertyItem.PropertyDescriptor.Name, kind, key);
					current = entry == ResourceReferenceEntries.NoneEntry ? "" : entry;
				} catch (System.Exception exception) {
					LoggingService.Error(exception);
					MessageService.ShowError(exception.Message);
					combo.SelectedItem = string.IsNullOrEmpty(current) ? ResourceReferenceEntries.NoneEntry : current;
				}
			};
			return combo;
		}
	}
}
