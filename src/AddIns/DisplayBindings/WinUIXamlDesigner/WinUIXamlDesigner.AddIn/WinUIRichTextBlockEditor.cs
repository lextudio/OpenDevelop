using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.WinUIXamlDesigner;

/// <summary>Properties-pad affordance for structured RichTextBlock content.</summary>
sealed class WinUIRichTextBlockEditor : ITypeEditor
{
	public FrameworkElement ResolveEditor(PropertyItem propertyItem)
	{
		var panel = new DockPanel { LastChildFill = true };
		var button = new Button {
			Content = "…", ToolTip = "Edit formatted text", MinWidth = 22,
			Padding = new Thickness(3, 0, 3, 0)
		};
		button.Click += (_, _) => propertyItem.PropertyDescriptor.SetValue(propertyItem.Instance, propertyItem.Value);
		DockPanel.SetDock(button, Dock.Right);
		panel.Children.Add(button);
		var value = new TextBlock {
			Margin = new Thickness(5, 0, 2, 0), TextTrimming = TextTrimming.CharacterEllipsis,
			VerticalAlignment = VerticalAlignment.Center
		};
		BindingOperations.SetBinding(value, TextBlock.TextProperty,
			new Binding("Value") { Source = propertyItem, Mode = BindingMode.OneWay });
		panel.Children.Add(value);
		return panel;
	}
}
