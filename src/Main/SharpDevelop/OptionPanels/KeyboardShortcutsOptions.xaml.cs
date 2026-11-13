using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
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
			public string Shortcut { get { return Row.Gesture ?? Text("None}"); } }
			public string Default { get { return Row.DefaultGesture ?? ""; } }
			public string ScopeName { get { return Row.IsRouted ? Text("BuiltIn}") : Row.Scope == KeyboardShortcutsModel.WorkbenchScope ? Text("Workbench}") : Row.Group; } }
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
			model = new KeyboardShortcutsModel(CommandCatalog.Build(SD.AddInTree.AddIns), KeyBindingService.Default.Overrides);
			views = model.Rows.Select(r => new RowView(model, r)).ToList();
			collection = CollectionViewSource.GetDefaultView(views);
			collection.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
			collection.Filter = Accept;
			commandList.ItemsSource = collection;
			ShowDetails();
		}

		public override bool SaveOptions()
		{
			if (model.IsDirty)
				model.Commit(KeyBindingService.Default);
			return base.SaveOptions();
		}

		/// <summary>
		/// The height the page needs, measured at the default dialog size: the parts that do not shrink (description, search, details, buttons, margins) are about 160 px
		/// and the list gives way down to its MinHeight of 100 px. Some headroom is added for wrapped text (translated strings, narrow dialog). Below this the dialog scrolls
		/// the whole page; the dialog's own MinHeight keeps that from happening in practice.
		/// </summary>
		public double MinimumContentHeight { get { return 320; } }

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
			return Contains(view.Name, query) || Contains(view.Group, query) || Contains(view.Row.Gesture, query) || Contains(view.Row.Key, query);
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
			// Tab (alone) leaves the box, so the page stays usable from the keyboard
			if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
				return;
			e.Handled = true;
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
			}
		}
	}
}
