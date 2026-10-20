using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

/// <summary>A Properties-pad descriptor that exposes a host-published plain item list
/// (<see cref="DesignerPropertyInfo.CollectionItems"/>).</summary>
public interface IItemsCollectionProperty
{
	IReadOnlyList<string> CollectionItems { get; }
	IReadOnlyList<string> CollectionItemKinds { get; }
	bool SupportsDataGridColumnKinds { get; }
}

/// <summary>Supplies the canonical host identifier when a property-grid descriptor has to use a
/// display-safe name (for example, a dotted protocol name represented as an Xceed binding name).</summary>
public interface IItemsCollectionPropertyName
{
	string PropertyName { get; }
}

/// <summary>The Properties pad's selected object, which commits an edited item list.</summary>
public interface IItemsCollectionEditorHost
{
	void SetItems(string propertyName, IReadOnlyList<DesignerCollectionItemEdit> items);
}

/// <summary>
/// The shared "…" flow behind an <c>ItemsCollection</c> Properties-pad row, used by every designer
/// whose host publishes one: open <see cref="CollectionItemsDialog"/> (or a test edit) over the
/// row's items and commit only an accepted, changed list to the row's host.
/// </summary>
public static class CollectionItemsEditing
{
	// DevFlow supplies an edit script instead of a modal dialog so the real editor button and
	// commit path can be exercised; normal use never sets it. Returning false cancels.
	public static Func<CollectionItemsEditModel, bool>? TestEdit { get; set; }
	public static Exception? TestException { get; set; }

	/// <summary>The outcome of <see cref="RunScriptedEdit"/>.</summary>
	public sealed record ScriptedEditResult(bool Success, string? Error, string[]? Before, string[]? After, bool Committed);

	/// <summary>
	/// Applies an edit script to a model, as a user would in the dialog: ';'-separated
	/// <c>add:&lt;afterIndex&gt;:&lt;text&gt;</c>, <c>remove:&lt;i&gt;</c>, <c>up:&lt;i&gt;</c>,
	/// <c>down:&lt;i&gt;</c>, <c>rename:&lt;i&gt;:&lt;text&gt;</c>, or <c>cancel</c>. Returns false for
	/// cancel; throws for an unknown or rejected step.
	/// </summary>
	public static bool ApplyScript(CollectionItemsEditModel model, string script)
	{
		foreach (var step in (script ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = step.Split(':', 3);
			var index = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : -1;
			if (parts[0] == "cancel")
				return false;
			var ok = parts[0] switch {
				"add" => model.Add(parts.Length > 2 ? parts[2] : "", index) >= 0,
				"add-checkbox" => model.SupportsDataGridColumnKinds && model.Add(parts.Length > 2 ? parts[2] : "", index, "CheckBox") >= 0,
				"add-combo" => model.SupportsDataGridColumnKinds && model.Add(parts.Length > 2 ? parts[2] : "", index, "ComboBox") >= 0,
				"remove" => model.Remove(index),
				"up" => model.MoveUp(index),
				"down" => model.MoveDown(index),
				"rename" => model.Rename(index, parts.Length > 2 ? parts[2] : ""),
				_ => throw new ArgumentException("Unknown items edit step: " + step)
			};
			if (!ok)
				throw new ArgumentException("Items edit step was rejected: " + step);
		}
		return true;
	}

	/// <summary>Clicks a rendered "…" items button with <paramref name="script"/> applied in place of
	/// the modal dialog, exercising the real button handler and commit path (DevFlow seam).</summary>
	public static ScriptedEditResult RunScriptedEdit(Button button, string script)
	{
		string[]? before = null;
		string[]? after = null;
		var committed = false;
		TestException = null;
		TestEdit = model => {
			before = model.Entries.Select(entry => entry.Text).ToArray();
			if (!ApplyScript(model, script))
				return false;
			after = model.Entries.Select(entry => entry.Text).ToArray();
			// The editor commits only an accepted AND changed list; report that decision.
			committed = model.IsChanged;
			return true;
		};
		try {
			button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
		} finally {
			TestEdit = null;
		}
		return TestException != null
			? new ScriptedEditResult(false, TestException.Message, before, after, false)
			: new ScriptedEditResult(true, null, before, after, committed);
	}

	/// <summary>Runs the editor for <paramref name="descriptor"/> on <paramref name="instance"/>
	/// (the row's own selected object). Failures go to <paramref name="reportError"/>, or to
	/// <see cref="TestException"/> while a test edit is active.</summary>
	public static void Edit(object? instance, PropertyDescriptor? descriptor, Window? owner, Action<Exception> reportError)
	{
		try {
			if (instance is not IItemsCollectionEditorHost host || descriptor is not IItemsCollectionProperty property)
				throw new InvalidOperationException("The items editor is not hosted by a designer Properties pad.");
			var model = new CollectionItemsEditModel(property.CollectionItems, property.CollectionItemKinds, property.SupportsDataGridColumnKinds);
			var accepted = TestEdit != null
				? TestEdit(model)
				: new CollectionItemsDialog(model) { Owner = owner }.ShowDialog() == true;
			if (accepted && model.IsChanged)
				host.SetItems((descriptor as IItemsCollectionPropertyName)?.PropertyName ?? descriptor.Name, model.ToEdits());
		} catch (Exception exception) {
			if (TestEdit != null) {
				TestException = exception;
				return;
			}
			reportError(exception);
		}
	}
}

/// <summary>A small list editor over <see cref="CollectionItemsEditModel"/>: add, remove,
/// reorder and rename items; OK commits, Cancel discards.</summary>
public sealed class CollectionItemsDialog : Window
{
	readonly CollectionItemsEditModel model;
	readonly ListBox list = new() { DisplayMemberPath = nameof(CollectionItemsEditModel.Entry.Text), MinHeight = 160 };
	readonly TextBox text = new() { Margin = new Thickness(0, 6, 0, 0) };
	bool syncing;

	public CollectionItemsDialog(CollectionItemsEditModel model)
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
		if (model.SupportsDataGridColumnKinds) {
			commands.Children.Add(Command("Add Text", () => Select(model.Add("Column " + (model.Entries.Count + 1), list.SelectedIndex, "Text"))));
			commands.Children.Add(Command("Add CheckBox", () => Select(model.Add("Column " + (model.Entries.Count + 1), list.SelectedIndex, "CheckBox"))));
			commands.Children.Add(Command("Add ComboBox", () => Select(model.Add("Column " + (model.Entries.Count + 1), list.SelectedIndex, "ComboBox"))));
		} else commands.Children.Add(Command("Add", () => Select(model.Add("Item " + (model.Entries.Count + 1), list.SelectedIndex))));
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
