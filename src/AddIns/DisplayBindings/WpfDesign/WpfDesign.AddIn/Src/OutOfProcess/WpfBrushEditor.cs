#nullable enable
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Presentation;

using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>
	/// Properties-pad row for a <c>Brush</c> descriptor: a swatch of the current brush, its text,
	/// and a "…" button that runs the shared <see cref="BrushEditing"/> flow (the in-process
	/// BrushEditor role) and commits through <c>design/set-brush</c>.
	/// </summary>
	public sealed class WpfBrushEditor : ITypeEditor
	{
		internal const string EditButtonName = "EditBrushButton";

		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button {
				Name = EditButtonName,
				Content = "…",
				ToolTip = "Edit brush",
				MinWidth = 22,
				Padding = new Thickness(3, 0, 3, 0)
			};
			button.Click += (_, _) => BrushEditing.Edit(propertyItem.Instance, propertyItem.PropertyDescriptor,
				Window.GetWindow(button), exception => {
					LoggingService.Error(exception);
					MessageService.ShowError(exception.Message);
				});
			DockPanel.SetDock(button, Dock.Right);
			panel.Children.Add(button);
			var swatch = new Border {
				Width = 16, Height = 12, Margin = new Thickness(4, 0, 0, 0),
				BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
				VerticalAlignment = VerticalAlignment.Center,
				Background = (propertyItem.PropertyDescriptor as IBrushProperty)?.Brush is { } brush
					? new BrushEditModel(brush).ToPreviewBrush() ?? Brushes.Transparent
					: Brushes.Transparent
			};
			DockPanel.SetDock(swatch, Dock.Left);
			panel.Children.Add(swatch);
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
