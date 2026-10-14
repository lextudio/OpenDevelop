#nullable enable
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>
	/// Properties-pad row for an <c>ImageSource</c> descriptor (Image.Source, Window.Icon): an
	/// editable drop-down of the project's images, relative to the designed XAML file, as Visual
	/// Studio offers. Typing any other URI still works; either way the value goes through the
	/// ordinary text write path.
	/// </summary>
	public sealed class WpfImageSourceEditor : ITypeEditor
	{
		internal const string ComboName = "ImageSourceCombo";

		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var combo = new ComboBox {
				Name = ComboName,
				IsEditable = true,
				IsReadOnly = propertyItem.IsReadOnly,
				ItemsSource = (propertyItem.Instance as WpfSurfaceElementPropertyAdapter)?.ImageSourceCandidates?.Invoke()
					?? System.Array.Empty<string>()
			};
			// Text commits when the row loses focus (a typed URI); picking an item commits at once.
			BindingOperations.SetBinding(combo, ComboBox.TextProperty, new Binding("Value") {
				Source = propertyItem, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
			});
			combo.SelectionChanged += (_, _) => {
				if (combo.SelectedItem is string path && !Equals(propertyItem.Value, path))
					propertyItem.Value = path;
			};
			return combo;
		}
	}
}
