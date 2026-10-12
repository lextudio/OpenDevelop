using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.FormsDesigner.Services
{
	/// <summary>
	/// Properties-pad row for a WinForms ListBox/ComboBox <c>ItemsCollection</c> descriptor: the item
	/// count plus a "…" button running the shared <see cref="CollectionItemsEditing"/> flow, which
	/// commits the edited list as one <c>design/set-items</c> mutation (the in-process designer's
	/// string collection editor).
	/// </summary>
	public sealed class FormsItemsCollectionEditor : ITypeEditor
	{
		internal const string EditButtonName = "EditItemsCollectionButton";

		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button {
				Name = EditButtonName,
				Content = "\u2026",
				ToolTip = "Edit items",
				MinWidth = 22,
				Padding = new Thickness(3, 0, 3, 0)
			};
			// The row's own Instance is its selected object, so no visual-tree walk is needed.
			button.Click += (_, _) => CollectionItemsEditing.Edit(propertyItem.Instance, propertyItem.PropertyDescriptor,
				Window.GetWindow(button), exception => {
					LoggingService.Error(exception);
					MessageService.ShowError(exception.Message);
				});
			DockPanel.SetDock(button, Dock.Right);
			panel.Children.Add(button);
			var value = new TextBlock {
				Margin = new Thickness(5, 0, 2, 0),
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center
			};
			BindingOperations.SetBinding(value, TextBlock.TextProperty, new Binding("Value") { Source = propertyItem, Mode = BindingMode.OneWay });
			panel.Children.Add(value);
			return panel;
		}
	}
}
