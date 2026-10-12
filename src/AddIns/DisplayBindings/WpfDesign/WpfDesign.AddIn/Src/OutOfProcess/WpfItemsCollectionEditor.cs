#nullable enable
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using ICSharpCode.SharpDevelop.Designer.Remote;

using Xceed.Wpf.Toolkit.PropertyGrid;
using Xceed.Wpf.Toolkit.PropertyGrid.Editors;

namespace ICSharpCode.WpfDesign.AddIn.OutOfProcess
{
	/// <summary>A Properties-pad descriptor that exposes a host-published plain item list.</summary>
	interface IItemsCollectionProperty
	{
		IReadOnlyList<string> CollectionItems { get; }
	}

	/// <summary>The Properties pad's selected object, which commits an edited item list.</summary>
	interface IItemsCollectionEditorHost
	{
		void SetItems(string propertyName, IReadOnlyList<DesignerCollectionItemEdit> items);
	}

	/// <summary>
	/// Properties-pad row for an <c>ItemsCollection</c> descriptor: the item count plus a "…" button
	/// that opens <see cref="ItemsCollectionDialog"/> and commits the whole edited list in one
	/// <c>design/set-items</c> mutation (the in-process designer's CollectionEditor role).
	/// </summary>
	public sealed class WpfItemsCollectionEditor : ITypeEditor
	{
		internal const string EditButtonName = "EditItemsCollectionButton";
		// DevFlow supplies an edit script instead of a modal dialog so the real button handler and
		// commit path can be exercised; normal use never sets it. Returning false cancels.
		internal static Func<CollectionItemsEditModel, bool>? TestEdit;
		internal static Exception? TestException;

		public FrameworkElement ResolveEditor(PropertyItem propertyItem)
		{
			var panel = new DockPanel { LastChildFill = true };
			var button = new Button {
				Name = EditButtonName,
				Content = "…",
				ToolTip = "Edit items",
				MinWidth = 22,
				Padding = new Thickness(3, 0, 3, 0)
			};
			button.Click += (_, _) => EditItems(button, propertyItem);
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

		static void EditItems(FrameworkElement source, PropertyItem propertyItem)
		{
			try {
				if (FindAncestor<Xceed.Wpf.Toolkit.PropertyGrid.PropertyGrid>(source)?.SelectedObject is not IItemsCollectionEditorHost host
					|| propertyItem.PropertyDescriptor is not IItemsCollectionProperty property)
					throw new InvalidOperationException("The items editor is not hosted by a WPF designer Properties pad.");
				var model = new CollectionItemsEditModel(property.CollectionItems);
				var accepted = TestEdit != null
					? TestEdit(model)
					: new ItemsCollectionDialog(model) { Owner = Window.GetWindow(source) }.ShowDialog() == true;
				if (accepted && model.IsChanged)
					host.SetItems(propertyItem.PropertyDescriptor.Name, model.ToEdits());
			} catch (Exception exception) {
				if (TestEdit != null) {
					TestException = exception;
					return;
				}
				LoggingService.Error(exception);
				MessageService.ShowError(exception.Message);
			}
		}

		static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
		{
			while (current != null) {
				if (current is T match)
					return match;
				current = current is Visual ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
			}
			return null;
		}
	}

	/// <summary>A small list editor over <see cref="CollectionItemsEditModel"/>: add, remove,
	/// reorder and rename items; OK commits, Cancel discards.</summary>
	sealed class ItemsCollectionDialog : Window
	{
		readonly CollectionItemsEditModel model;
		readonly ListBox list = new() { DisplayMemberPath = nameof(CollectionItemsEditModel.Entry.Text), MinHeight = 160 };
		readonly TextBox text = new() { Margin = new Thickness(0, 6, 0, 0) };
		bool syncing;

		public ItemsCollectionDialog(CollectionItemsEditModel model)
		{
			this.model = model;
			Title = "Edit Items";
			Width = 360;
			Height = 320;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;
			ShowInTaskbar = false;
			ResizeMode = ResizeMode.CanResizeWithGrip;
			list.ItemsSource = model.Entries;

			Button Command(string label, Action action)
			{
				var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 4), MinWidth = 80 };
				button.Click += (_, _) => action();
				return button;
			}
			var commands = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
			commands.Children.Add(Command("Add", () => Select(model.Add("Item " + (model.Entries.Count + 1), list.SelectedIndex))));
			commands.Children.Add(Command("Remove", () => { var index = list.SelectedIndex; if (model.Remove(index)) Select(Math.Min(index, model.Entries.Count - 1)); }));
			commands.Children.Add(Command("Move Up", () => { var index = list.SelectedIndex; if (model.MoveUp(index)) Select(index - 1); }));
			commands.Children.Add(Command("Move Down", () => { var index = list.SelectedIndex; if (model.MoveDown(index)) Select(index + 1); }));

			var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 6, 0) };
			ok.Click += (_, _) => DialogResult = true;
			var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
			var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
			footer.Children.Add(ok);
			footer.Children.Add(cancel);

			var body = new DockPanel();
			DockPanel.SetDock(commands, Dock.Right);
			body.Children.Add(commands);
			var left = new DockPanel();
			DockPanel.SetDock(text, Dock.Bottom);
			left.Children.Add(text);
			left.Children.Add(list);
			body.Children.Add(left);
			var root = new DockPanel { Margin = new Thickness(10) };
			DockPanel.SetDock(footer, Dock.Bottom);
			root.Children.Add(footer);
			root.Children.Add(body);
			Content = root;
			SetResourceReference(BackgroundProperty, "ToolWindowBackground");

			list.SelectionChanged += (_, _) => {
				syncing = true;
				text.Text = (list.SelectedItem as CollectionItemsEditModel.Entry)?.Text ?? "";
				text.IsEnabled = list.SelectedItem != null;
				syncing = false;
			};
			text.TextChanged += (_, _) => {
				if (syncing || list.SelectedIndex < 0)
					return;
				var index = list.SelectedIndex;
				model.Rename(index, text.Text);
				list.Items.Refresh();
				list.SelectedIndex = index;
			};
			Select(model.Entries.Count > 0 ? 0 : -1);
		}

		void Select(int index)
		{
			list.SelectedIndex = index;
			if (index >= 0)
				list.ScrollIntoView(list.SelectedItem);
		}
	}
}
