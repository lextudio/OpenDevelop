using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.SharpDevelop.OptionPanels
{
	/// <summary>
	/// The Keyboard page: a working copy of the shortcuts (<see cref="KeyboardShortcutsModel"/>) that is written through the
	/// <see cref="KeyBindingService"/> only when the dialog's OK is pressed. doc/technotes/keyboard-shortcuts.md.
	/// </summary>
	partial class KeyboardShortcutsOptions : OptionPanel, IFillsOptionPanelHeight
	{
		const string ResourcePrefix = "${res:Dialog.Options.IDEOptions.Keyboard.";

		/// <summary>One row of the list; the model row it shows is the truth, this only formats it.</summary>
		sealed class RowView : INotifyPropertyChanged
		{
			readonly KeyboardShortcutsModel model;
			public RowView(KeyboardShortcutsModel model, ShortcutRow row) { this.model = model; Row = row; }

			public ShortcutRow Row { get; private set; }
			public string Name { get { return Row.Name; } }
			public string Group { get { return Row.Group; } }
			public string GroupName {
				get {
					switch (Row.Group) {
						case "Toolbars": return Text("Group.Toolbars}");
						case "Pads": return Text("Group.Pads}");
						case "Tools": return Text("Group.Tools}");
						default: return Row.GroupName;
					}
				}
			}
			public string Shortcut { get { return Row.Gesture ?? Text("None}"); } }
			public string Default { get { return Row.DefaultGesture ?? ""; } }
			public string ScopeName { get { return Row.IsRouted ? Text("BuiltIn}") : Row.Scope == KeyboardShortcutsModel.WorkbenchScope ? Text("Workbench}") : GroupName; } }
			public bool HasConflict { get { return model.ConflictsOf(Row).Count > 0; } }
			public string Mark { get { return HasConflict ? "!" : Row.IsCustomized ? "*" : ""; } }

			public event PropertyChangedEventHandler PropertyChanged;
			public void Refresh()
			{
				if (PropertyChanged != null)
					PropertyChanged(this, new PropertyChangedEventArgs(null));
			}
		}

		static string Text(string key)
		{
			return StringParser.Parse(ResourcePrefix + key);
		}

		readonly KeyboardShortcutsModel model;
		readonly List<RowView> views;
		readonly ICollectionView collection;

		public KeyboardShortcutsOptions()
		{
			InitializeComponent();
			var addIns = SD.AddInTree.AddIns;
			model = new KeyboardShortcutsModel(CommandCatalog.Build(addIns), KeyBindingService.Default.Overrides, CommandCatalog.MainMenuLabels(addIns));
			views = model.Rows.Select(r => new RowView(model, r)).ToList();
			collection = CollectionViewSource.GetDefaultView(views);
			collection.GroupDescriptions.Add(new PropertyGroupDescription("GroupName"));
			collection.SortDescriptions.Add(new SortDescription("GroupName", ListSortDirection.Ascending));
			collection.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
			collection.Filter = Accept;
			commandList.ItemsSource = collection;
			ShowDetails();
			ShowUnknown();
		}

		void UnknownToggled(object sender, RoutedEventArgs e)
		{
			RaiseMinimumContentHeightChanged();
		}

		/// <summary>Overrides of commands that are not loaded: listed so they can be seen and removed (they would apply again if their AddIn came back).</summary>
		void ShowUnknown()
		{
			var unknown = model.UnknownOverrides;
			unknownExpander.Visibility = unknown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
			RaiseMinimumContentHeightChanged();
			unknownHeader.Text = string.Format(Text("Unknown.Header}"), unknown.Count);
			unknownList.Items.Clear();
			foreach (UnknownOverride item in unknown) {
				string key = item.Key;
				var remove = new Button { Content = Text("Remove}"), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0) };
				AutomationProperties.SetName(remove, Text("Remove}") + " " + key);
				remove.Click += delegate { model.RemoveUnknown(key); ShowUnknown(); };
				var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
				line.Children.Add(new TextBlock {
					Text = key + "  " + (string.IsNullOrEmpty(item.Gesture) ? Text("None}") : item.Gesture),
					VerticalAlignment = VerticalAlignment.Center
				});
				line.Children.Add(remove);
				unknownList.Items.Add(line);
			}
		}

		public override bool SaveOptions()
		{
			if (model.IsDirty)
				model.Commit(KeyBindingService.Default);
			return base.SaveOptions();
		}

		/// <summary>
		/// The height the page needs, measured at the default dialog size: the parts that do not shrink (description, search, details, buttons, margins) are about 160 px
		/// and the list gives way down to its MinHeight of 100 px. Some headroom is added for wrapped text (translated strings, narrow dialog). The section of overrides for
		/// commands that are not loaded adds its header (about 26 px) when it is shown and its list (up to 80 px) when it is open. Below this the dialog scrolls the whole page;
		/// the dialog's own MinHeight keeps that from happening in practice.
		/// </summary>
		public double MinimumContentHeight {
			get {
				double height = 320;
				if (unknownExpander.Visibility == Visibility.Visible) {
					height += 26;
					if (unknownExpander.IsExpanded)
						height += 80;
				}
				return height;
			}
		}

		public event EventHandler MinimumContentHeightChanged;

		void RaiseMinimumContentHeightChanged()
		{
			if (MinimumContentHeightChanged != null)
				MinimumContentHeightChanged(this, EventArgs.Empty);
		}

		RowView Selected { get { return commandList.SelectedItem as RowView; } }

		bool Accept(object item)
		{
			var view = (RowView)item;
			switch ((filterBox.SelectedItem as ComboBoxItem)?.Tag as string) {
				case "Customized": if (!view.Row.IsCustomized) return false; break;
				case "Unassigned": if (view.Row.Gesture != null) return false; break;
				case "Conflicts": if (!view.HasConflict) return false; break;
			}
			string query = searchBox.Text == null ? "" : searchBox.Text.Trim();
			if (query.Length == 0)
				return true;
			if (gestureSearchBox.IsChecked == true) {
				// the text is a chord: show the commands that use exactly it ("who owns Ctrl+B?")
				return KeyboardShortcutsModel.SameGesture(query, view.Row.Gesture);
			}
			return Contains(view.Name, query) || Contains(view.GroupName, query) || Contains(view.Row.Gesture, query) || Contains(view.Row.Key, query);
		}

		static bool Contains(string text, string query)
		{
			return text != null && text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
		}

		void SearchChanged(object sender, TextChangedEventArgs e)
		{
			if (collection != null)
				collection.Refresh();
		}

		void GestureSearchChanged(object sender, RoutedEventArgs e)
		{
			searchBox.Text = "";
			if (collection != null)
				collection.Refresh();
		}

		/// <summary>In shortcut-search mode the next chord typed into the search box becomes the query (Tab alone still leaves the box).</summary>
		void SearchKeyDown(object sender, KeyEventArgs e)
		{
			if (gestureSearchBox.IsChecked != true)
				return;
			Key key = e.Key == Key.System ? e.SystemKey : e.Key;
			CaptureKeyAction action = KeyboardShortcutsModel.ClassifyCaptureKey(key, Keyboard.Modifiers);
			if (action == CaptureKeyAction.PassThrough)
				return;
			e.Handled = true;
			if (action == CaptureKeyAction.Clear)
				searchBox.Text = "";
			else {
				string chord = KeyboardShortcutsModel.FormatChord(key, Keyboard.Modifiers);
				if (chord != null)
					searchBox.Text = chord;
			}
		}

		void FilterChanged(object sender, SelectionChangedEventArgs e)
		{
			if (collection != null)
				collection.Refresh();
		}

		void SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			captureBox.Text = "";
			ShowDetails();
		}

		void ShowDetails()
		{
			RowView view = Selected;
			bool editable = view != null && !view.Row.IsRouted;
			captureBox.IsEnabled = editable;
			selectedText.Text = view == null ? "" : string.Format(Text("Selected}"), view.Name);
			locationsText.Text = view == null ? "" : string.Format(Text("Locations}"), string.Join(", ", view.Row.Locations));
			resetButton.IsEnabled = editable && view.Row.IsCustomized;
			removeButton.IsEnabled = editable && view.Row.Gesture != null;
			Evaluate();
		}

		/// <summary>Checks the text in the capture box and shows what is wrong, who has the gesture, and what can be done.</summary>
		void Evaluate()
		{
			RowView view = Selected;
			assignButton.IsEnabled = false;
			reassignButton.Visibility = Visibility.Collapsed;
			messageText.Visibility = Visibility.Collapsed;
			string text = captureBox.Text;
			if (view == null || view.Row.IsRouted || string.IsNullOrEmpty(text))
				return;
			AssignCheck check = model.Check(view.Row.Key, text);
			if (!check.IsValid) {
				Say(check.Error);
				return;
			}
			bool changed = !string.Equals(check.Gesture, view.Row.Gesture, StringComparison.OrdinalIgnoreCase);
			if (check.Conflicts.Count > 0) {
				ShortcutRow other = check.Conflicts[0];
				Say(string.Format(Text("ConflictWith}"), check.Gesture, other.Name, other.Group));
				reassignButton.Visibility = Visibility.Visible;
				return;
			}
			if (check.Shadows.Count > 0)
				Say(string.Format(Text("Shadows}"), check.Gesture, check.Shadows[0].Name));
			assignButton.IsEnabled = changed;
		}

		void Say(string message)
		{
			messageText.Text = message;
			messageText.Visibility = Visibility.Visible;
		}

		void CaptureKeyDown(object sender, KeyEventArgs e)
		{
			Key key = e.Key == Key.System ? e.SystemKey : e.Key;
			CaptureKeyAction action = KeyboardShortcutsModel.ClassifyCaptureKey(key, Keyboard.Modifiers);
			if (action == CaptureKeyAction.PassThrough)
				return;
			e.Handled = true;
			if (action == CaptureKeyAction.Clear) {
				captureBox.Text = "";
				return;
			}
			string chord = KeyboardShortcutsModel.FormatChord(key, Keyboard.Modifiers);
			if (chord == null)
				return;
			captureBox.Text = chord;
			Evaluate();
		}

		// the text can also arrive without a key press (paste, automation): check it the same way
		void CaptureTextChanged(object sender, TextChangedEventArgs e)
		{
			if (collection != null)
				Evaluate();
		}

		void CaptureFocused(object sender, KeyboardFocusChangedEventArgs e)
		{
			if (string.IsNullOrEmpty(captureBox.Text))
				captureBox.ToolTip = Text("Capture.Prompt}");
		}

		void Apply(Action<RowView> change)
		{
			RowView view = Selected;
			if (view == null)
				return;
			change(view);
			captureBox.Text = "";
			foreach (RowView each in views)
				each.Refresh();
			collection.Refresh();
			ShowDetails();
		}

		void AssignClick(object sender, RoutedEventArgs e)
		{
			string text = captureBox.Text;
			Apply(v => model.Assign(v.Row.Key, text));
		}

		void ReassignClick(object sender, RoutedEventArgs e)
		{
			string text = captureBox.Text;
			Apply(v => model.Assign(v.Row.Key, text, reassign: true));
		}

		void RemoveClick(object sender, RoutedEventArgs e)
		{
			Apply(v => model.Assign(v.Row.Key, ""));
		}

		void ResetClick(object sender, RoutedEventArgs e)
		{
			Apply(v => model.Reset(v.Row.Key));
		}

		void ResetAllClick(object sender, RoutedEventArgs e)
		{
			if (MessageService.AskQuestion(ResourcePrefix + "ResetAllQuestion}")) {
				model.ResetAll();
				Apply(v => { });
				ShowUnknown();
			}
		}
	}
}
