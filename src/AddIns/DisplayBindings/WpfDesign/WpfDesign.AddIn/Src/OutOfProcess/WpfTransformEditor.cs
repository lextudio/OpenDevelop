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
	/// <summary>
	/// Properties-pad row for a <c>Transform</c> descriptor (RenderTransform, LayoutTransform): the transform's text and a "…" button that
	/// runs the shared <see cref="TransformEditing"/> flow and commits through <c>design/set-transform</c>.
	/// </summary>
	public sealed class WpfTransformEditor : ITypeEditor
	{
		internal const string EditButtonName = "EditTransformButton";

		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button {
				Name = EditButtonName,
				Content = "…",
				ToolTip = "Edit transform",
				MinWidth = 22,
				Padding = new Thickness(3, 0, 3, 0)
			};
			button.Click += (_, _) => TransformEditing.Edit(propertyItem.Instance, propertyItem.PropertyDescriptor,
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
