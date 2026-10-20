#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Presentation;

using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>Properties-pad editor for a host-proven explicit TreeViewItem hierarchy. Unlike the
	/// flat Items editor it preserves parent/child structure and sends one tree mutation on OK.</summary>
	public sealed class WpfTreeItemsEditor : ITypeEditor
	{
		internal const string EditButtonName = "EditTreeItemsButton";
		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button { Name = EditButtonName, Content = "…", ToolTip = "Edit tree items", MinWidth = 22, Padding = new Thickness(3, 0, 3, 0) };
			button.Click += (_, _) => TreeItemsEditing.Edit(propertyItem.Instance, propertyItem.PropertyDescriptor,
				Window.GetWindow(button), exception => { LoggingService.Error(exception); MessageService.ShowError(exception.Message); });
			DockPanel.SetDock(button, Dock.Right); panel.Children.Add(button);
			var value = new TextBlock { Margin = new Thickness(5, 0, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
			BindingOperations.SetBinding(value, TextBlock.TextProperty, new Binding("Value") { Source = propertyItem, Mode = BindingMode.OneWay });
			panel.Children.Add(value);
			return panel;
		}
	}
}
