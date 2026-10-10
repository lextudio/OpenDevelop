using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Microsoft.Win32;
using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.FormsDesigner.Services
{
	/// <summary>
	/// Provides the explicit IDE-side picker for a resource-backed image property.
	/// The designer host deliberately keeps the corresponding scalar value read-only: only the
	/// parent owns the project resource transaction and can send its resulting snapshot back.
	/// </summary>
	public sealed class ResourceImagePropertyEditor : ITypeEditor
	{
		const long MaxResourceImageBytes = 20L * 1024 * 1024;
		internal const string ReplaceButtonName = "ReplaceResourceImageButton";
		// The DevFlow integration action supplies a temporary file so it can exercise the
		// actual button handler without opening a native modal picker. Normal IDE use never
		// sets this and always follows the OpenFileDialog path below.
		internal static Func<string> TestFileNameSelector;
		internal static Exception TestException;

		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button {
				Name = ReplaceButtonName,
				Content = "…",
				ToolTip = "Replace resource image",
				MinWidth = 22,
				Padding = new Thickness(3, 0, 3, 0)
			};
			button.Click += (_, _) => ReplaceResourceImage(button, propertyItem);
			DockPanel.SetDock(button, Dock.Right);
			panel.Children.Add(button);

			var value = new TextBlock {
				Margin = new Thickness(5, 0, 2, 0),
				TextTrimming = TextTrimming.CharacterEllipsis,
				VerticalAlignment = VerticalAlignment.Center
			};
			BindingOperations.SetBinding(value, TextBlock.TextProperty, new Binding("Value") {
				Source = propertyItem,
				Mode = BindingMode.OneWay
			});
			panel.Children.Add(value);
			return panel;
		}

		static void ReplaceResourceImage(FrameworkElement source, PropertyItem propertyItem)
		{
			var grid = FindAncestor<PropertyGrid>(source);
			if (grid?.SelectedObject is not IResourceImageEditorHost host) {
				ReportFailure(new InvalidOperationException("The resource-image editor is not hosted by a WinForms Properties pad."));
				return;
			}
			try {
				var fileName = TestFileNameSelector?.Invoke();
				if (String.IsNullOrEmpty(fileName)) {
					var dialog = new OpenFileDialog {
						Title = "Replace resource image",
						Filter = "Image files|*.png;*.bmp;*.gif;*.jpg;*.jpeg;*.ico|All files|*.*",
						CheckFileExists = true,
						Multiselect = false
					};
					if (dialog.ShowDialog() != true)
						return;
					fileName = dialog.FileName;
				}
				var file = new FileInfo(fileName);
				if (file.Length > MaxResourceImageBytes)
					throw new InvalidOperationException("The replacement image is larger than 20 MiB.");
				host.ReplaceResourceImage(propertyItem.PropertyDescriptor.Name, File.ReadAllBytes(fileName));
			} catch (Exception exception) {
				if (TestFileNameSelector != null) {
					TestException = exception;
					return;
				}
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		static void ReportFailure(Exception exception)
		{
			if (TestFileNameSelector != null) {
				TestException = exception;
				return;
			}
			LoggingService.Error(exception);
			MessageService.ShowError(exception.Message);
		}

		static T FindAncestor<T>(DependencyObject element) where T : class
		{
			for (DependencyObject current = element; current != null; current = GetParent(current)) {
				if (current is T result)
					return result;
			}
			return null;
		}

		static DependencyObject GetParent(DependencyObject element)
		{
			if (element is Visual || element is System.Windows.Media.Media3D.Visual3D)
				return VisualTreeHelper.GetParent(element);
			return LogicalTreeHelper.GetParent(element);
		}
	}
}
