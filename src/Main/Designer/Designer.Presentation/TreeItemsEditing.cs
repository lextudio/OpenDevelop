using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.SharpDevelop.Designer.Presentation;

public interface ITreeItemsProperty
{
	IReadOnlyList<DesignerTreeItem> TreeItems { get; }
}

public interface ITreeItemsEditorHost
{
	void SetTreeItems(string propertyName, IReadOnlyList<DesignerTreeItemEdit> items);
}

/// <summary>Model behind the WPF TreeViewItem editor. Source indices are assigned once per
/// original sibling list, so an edited entry can only retain an item beneath its original parent.</summary>
public sealed class TreeItemsEditModel
{
	public sealed class Entry : INotifyPropertyChanged
	{
		string text;
		internal Entry(int sourceIndex, string text, Entry? parent)
		{
			SourceIndex = sourceIndex;
			this.text = text;
			Parent = parent;
		}
		public int SourceIndex { get; }
		public Entry? Parent { get; }
		public ObservableCollection<Entry> Children { get; } = new();
		public string Text { get => text; set { value ??= ""; if (text == value) return; text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } }
		public event PropertyChangedEventHandler? PropertyChanged;
	}

	readonly List<DesignerTreeItemEdit> original;
	public ObservableCollection<Entry> Roots { get; } = new();

	public TreeItemsEditModel(IEnumerable<DesignerTreeItem> items)
	{
		var source = (items ?? throw new ArgumentNullException(nameof(items))).ToList();
		original = ToEdits(source);
		foreach (var entry in CreateEntries(source, null)) Roots.Add(entry);
	}

	static IEnumerable<Entry> CreateEntries(IReadOnlyList<DesignerTreeItem> items, Entry? parent)
	{
		for (var index = 0; index < items.Count; index++) {
			var entry = new Entry(index, items[index].Text ?? "", parent);
			foreach (var child in CreateEntries(items[index].Children ?? new List<DesignerTreeItem>(), entry)) entry.Children.Add(child);
			yield return entry;
		}
	}

	static List<DesignerTreeItemEdit> ToEdits(IReadOnlyList<DesignerTreeItem> items)
		=> items.Select((item, index) => new DesignerTreeItemEdit { SourceIndex = index, Text = item.Text ?? "", Children = ToEdits(item.Children ?? new List<DesignerTreeItem>()) }).ToList();

	public IReadOnlyList<DesignerTreeItemEdit> ToEdits() => ToEdits(Roots);
	static List<DesignerTreeItemEdit> ToEdits(IEnumerable<Entry> entries)
		=> entries.Select(entry => new DesignerTreeItemEdit { SourceIndex = entry.SourceIndex, Text = entry.Text, Children = ToEdits(entry.Children) }).ToList();

	static bool Equal(IReadOnlyList<DesignerTreeItemEdit> left, IReadOnlyList<DesignerTreeItemEdit> right)
		=> left.Count == right.Count && left.Zip(right).All(pair => pair.First.SourceIndex == pair.Second.SourceIndex
			&& String.Equals(pair.First.Text, pair.Second.Text, StringComparison.Ordinal) && Equal(pair.First.Children, pair.Second.Children));
	public bool IsChanged => !Equal(original, ToEdits());

	public Entry AddSibling(Entry? selected)
	{
		var siblings = selected?.Parent?.Children ?? Roots;
		var entry = new Entry(-1, "Item", selected?.Parent);
		var index = selected == null ? siblings.Count : siblings.IndexOf(selected) + 1;
		siblings.Insert(index, entry);
		return entry;
	}
	public Entry AddChild(Entry selected) { var entry = new Entry(-1, "Item", selected); selected.Children.Add(entry); return entry; }
	public bool Remove(Entry? entry) => entry != null && (entry.Parent?.Children ?? Roots).Remove(entry);
	public bool Move(Entry? entry, int offset)
	{
		if (entry == null) return false;
		var siblings = entry.Parent?.Children ?? Roots;
		var index = siblings.IndexOf(entry); var destination = index + offset;
		if (destination < 0 || destination >= siblings.Count) return false;
		siblings.Move(index, destination); return true;
	}
}

public static class TreeItemsEditing
{
	public static Func<TreeItemsEditModel, bool>? TestEdit { get; set; }
	public static Exception? TestException { get; set; }
	static bool testCommitted;
	public sealed record ScriptedEditResult(bool Success, string? Error, bool Committed);
	public static bool ApplyScript(TreeItemsEditModel model, string script)
	{
		foreach (var step in (script ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			var parts = step.Split(':', 3);
			var index = parts.Length > 1 && Int32.TryParse(parts[1], out var parsed) ? parsed : -1;
			if (parts[0] == "cancel") return false;
			var ok = parts[0] switch {
				"rename" when index >= 0 && index < model.Roots.Count => Rename(model.Roots[index], parts.Length > 2 ? parts[2] : ""),
				"add-child" when index >= 0 && index < model.Roots.Count => AddChild(model, model.Roots[index], parts.Length > 2 ? parts[2] : ""),
				"add" => AddRoot(model, parts.Length > 2 ? parts[2] : ""),
				"remove" when index >= 0 && index < model.Roots.Count => model.Remove(model.Roots[index]),
				_ => throw new ArgumentException("Tree items edit step was rejected: " + step)
			};
			if (!ok) throw new ArgumentException("Tree items edit step was rejected: " + step);
		}
		return true;
	}
	static bool Rename(TreeItemsEditModel.Entry entry, string text) { entry.Text = text; return true; }
	static bool AddChild(TreeItemsEditModel model, TreeItemsEditModel.Entry parent, string text) { model.AddChild(parent).Text = text; return true; }
	static bool AddRoot(TreeItemsEditModel model, string text) { model.AddSibling(null).Text = text; return true; }
	public static ScriptedEditResult RunScriptedEdit(Button button, string script)
	{
		TestException = null;
		testCommitted = false;
		TestEdit = model => ApplyScript(model, script);
		try { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
		finally { TestEdit = null; }
		return TestException != null
			? new ScriptedEditResult(false, TestException.Message, false)
			: new ScriptedEditResult(true, null, testCommitted);
	}

	public static void Edit(object? instance, PropertyDescriptor? descriptor, Window? owner, Action<Exception> reportError)
	{
		try {
			if (instance is not ITreeItemsEditorHost host || descriptor is not ITreeItemsProperty property)
				throw new InvalidOperationException("The tree editor is not hosted by a designer Properties pad.");
			var model = new TreeItemsEditModel(property.TreeItems);
			var accepted = TestEdit != null ? TestEdit(model) : new TreeItemsDialog(model) { Owner = owner }.ShowDialog() == true;
			if (accepted && model.IsChanged) {
				host.SetTreeItems((descriptor as IItemsCollectionPropertyName)?.PropertyName ?? descriptor.Name, model.ToEdits());
				if (TestEdit != null) testCommitted = true;
			}
		} catch (Exception exception) {
			if (TestEdit != null) {
				TestException = exception;
				return;
			}
			reportError(exception);
		}
	}
}

public sealed class TreeItemsDialog : Window
{
	readonly TreeItemsEditModel model;
	readonly ListBox list = new() { MinHeight = 180, DisplayMemberPath = nameof(DisplayEntry.Text) };
	readonly TextBox text = new() { Margin = new Thickness(0, 6, 0, 0) };
	bool syncing;
	sealed record DisplayEntry(TreeItemsEditModel.Entry Entry, string Text);

	public TreeItemsDialog(TreeItemsEditModel model)
	{
		this.model = model; Title = "Edit Tree Items"; Width = 380; Height = 360;
		WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; ResizeMode = ResizeMode.CanResizeWithGrip;
		Button Command(string label, Action action) { var button = new Button { Content = label, Margin = new Thickness(0, 0, 0, 4), MinWidth = 88 }; button.Click += (_, _) => action(); return button; }
		var commands = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
		commands.Children.Add(Command("Add Sibling", () => Select(model.AddSibling(Selected))));
		commands.Children.Add(Command("Add Child", () => { if (Selected != null) Select(model.AddChild(Selected)); }));
		commands.Children.Add(Command("Remove", () => { var selected = Selected; if (model.Remove(selected)) Refresh(); }));
		commands.Children.Add(Command("Move Up", () => { if (model.Move(Selected, -1)) Refresh(); }));
		commands.Children.Add(Command("Move Down", () => { if (model.Move(Selected, 1)) Refresh(); }));
		var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 75, Margin = new Thickness(0, 0, 6, 0) }; ok.Click += (_, _) => DialogResult = true;
		var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 75 };
		var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) }; footer.Children.Add(ok); footer.Children.Add(cancel);
		var body = new DockPanel(); DockPanel.SetDock(commands, Dock.Right); body.Children.Add(commands);
		var left = new DockPanel(); DockPanel.SetDock(text, Dock.Bottom); left.Children.Add(text); left.Children.Add(list); body.Children.Add(left);
		var root = new DockPanel { Margin = new Thickness(10) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(body); Content = root;
		list.SelectionChanged += (_, _) => { syncing = true; text.Text = Selected?.Text ?? ""; text.IsEnabled = Selected != null; syncing = false; };
		text.TextChanged += (_, _) => { if (!syncing && Selected is { } entry) { entry.Text = text.Text; Refresh(entry); } };
		Refresh();
	}

	TreeItemsEditModel.Entry? Selected => (list.SelectedItem as DisplayEntry)?.Entry;
	void Refresh(TreeItemsEditModel.Entry? select = null)
	{
		select ??= Selected;
		var entries = new List<DisplayEntry>();
		void Add(IEnumerable<TreeItemsEditModel.Entry> source, int depth) { foreach (var entry in source) { entries.Add(new DisplayEntry(entry, new string(' ', depth * 2) + entry.Text)); Add(entry.Children, depth + 1); } }
		Add(model.Roots, 0); list.ItemsSource = entries; list.SelectedItem = entries.FirstOrDefault(entry => entry.Entry == select); text.IsEnabled = list.SelectedItem != null;
	}
	void Select(TreeItemsEditModel.Entry entry) { Refresh(entry); list.ScrollIntoView(list.SelectedItem); }
}
