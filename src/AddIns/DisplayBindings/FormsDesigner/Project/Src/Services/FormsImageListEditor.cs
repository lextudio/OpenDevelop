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
	/// Properties-pad row for a WinForms ImageList's <c>Images</c> descriptor: the image count plus a "…" button running the shared
	/// <see cref="ImageListEditing"/> flow, which commits the edited list as one IDE-owned source-and-resource transaction (the
	/// in-process designer's ImageCollectionEditor role).
	/// </summary>
	public sealed class FormsImageListEditor : ITypeEditor
	{
		internal const string EditButtonName = "EditImageListButton";

		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button {
				Name = EditButtonName,
				Content = "…",
				ToolTip = "Edit images",
				MinWidth = 22,
				Padding = new Thickness(3, 0, 3, 0)
			};
			button.Click += (_, _) => ImageListEditing.Edit(propertyItem.Instance, propertyItem.PropertyDescriptor,
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
