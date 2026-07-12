// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Xml;

using ICSharpCode.Core;

namespace ICSharpCode.XmlEditor
{
	/// <summary>
	/// Base class for the AddElementDialog and AddAttributeDialog. This
	/// dialog presents a list of names and an extra text box for entering
	/// a custom name. It is used to add a new node to the XML tree. It
	/// contains all the core logic for the AddElementDialog and
	/// AddAttributeDialog classes.
	/// </summary>
	public class AddXmlNodeDialog : Window, IAddXmlNodeDialog
	{
		ListBox namesListBox;
		TextBlock customNameTextBoxLabel;
		TextBox customNameTextBox;
		TextBlock errorTextBlock;
		Button okButton;
		Button cancelButton;

		public AddXmlNodeDialog() : this(new string[0])
		{
		}

		/// <summary>
		/// Creates the dialog and adds the specified names to the
		/// list box.
		/// </summary>
		public AddXmlNodeDialog(string[] names)
		{
			InitializeComponent();
			InitStrings();
			if (names.Length > 0) {
				AddNames(names);
			} else {
				RemoveNamesListBox();
			}
		}

		/// <summary>
		/// Gets the selected names in the list box together with the
		/// custom name entered in the text box.
		/// </summary>
		public string[] GetNames()
		{
			var names = new List<string>();
			foreach (string name in namesListBox.SelectedItems) {
				names.Add(name);
			}

			string customName = customNameTextBox.Text.Trim();
			if (customName.Length > 0) {
				names.Add(customName);
			}
			return names.ToArray();
		}

		public void Dispose()
		{
			Close();
		}

		AddXmlNodeDialogResult IAddXmlNodeDialog.ShowDialog()
		{
			bool? result = base.ShowDialog();
			return result == true ? AddXmlNodeDialogResult.OK : AddXmlNodeDialogResult.Cancel;
		}

		/// <summary>
		/// Gets the text from the error provider.
		/// </summary>
		public string ErrorText {
			get { return errorTextBlock.Text; }
		}

		/// <summary>
		/// Gets or sets the custom name label's text.
		/// </summary>
		public string CustomNameLabelText {
			get { return customNameTextBoxLabel.Text; }
			set { customNameTextBoxLabel.Text = value; }
		}

		void NamesListBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			UpdateOkButtonState();
		}

		void CustomNameTextBoxTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
		{
			UpdateOkButtonState();
		}

		void InitializeComponent()
		{
			Title = "AddXmlNodeDialog";
			Width = 289;
			Height = 244;
			MinWidth = 289;
			MinHeight = 143;
			ResizeMode = ResizeMode.CanResize;
			ShowInTaskbar = false;
			WindowStartupLocation = WindowStartupLocation.CenterOwner;

			var root = new DockPanel();

			namesListBox = new ListBox { SelectionMode = SelectionMode.Extended };
			namesListBox.SelectionChanged += NamesListBoxSelectionChanged;
			DockPanel.SetDock(namesListBox, Dock.Top);

			customNameTextBoxLabel = new TextBlock { Text = "Custom:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
			customNameTextBox = new TextBox { Margin = new Thickness(0, 0, 6, 0) };
			customNameTextBox.TextChanged += CustomNameTextBoxTextChanged;

			errorTextBlock = new TextBlock { Foreground = System.Windows.Media.Brushes.Red, Margin = new Thickness(6, 0, 6, 6) };

			okButton = new Button { Content = "OK", IsDefault = true, IsEnabled = false, Width = 75, Margin = new Thickness(6) };
			okButton.Click += (s, e) => { DialogResult = true; };
			cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 75, Margin = new Thickness(6) };

			var customNamePanel = new StackPanel { Orientation = Orientation.Horizontal };
			customNamePanel.Children.Add(customNameTextBoxLabel);
			customNamePanel.Children.Add(customNameTextBox);

			var buttonsPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			buttonsPanel.Children.Add(okButton);
			buttonsPanel.Children.Add(cancelButton);

			var bottomPanel = new StackPanel();
			bottomPanel.Children.Add(customNamePanel);
			bottomPanel.Children.Add(errorTextBlock);
			bottomPanel.Children.Add(buttonsPanel);
			DockPanel.SetDock(bottomPanel, Dock.Bottom);

			root.Children.Add(bottomPanel);
			root.Children.Add(namesListBox);
			Content = root;
		}

		/// <summary>
		/// Adds the names to the list box.
		/// </summary>
		void AddNames(string[] names)
		{
			var sorted = new List<string>(names);
			sorted.Sort(StringComparer.Ordinal);
			foreach (string name in sorted) {
				namesListBox.Items.Add(name);
			}
		}

		/// <summary>
		/// Enables or disables the ok button depending on whether any list
		/// item is selected or a custom name has been entered.
		/// </summary>
		void UpdateOkButtonState()
		{
			okButton.IsEnabled = IsOkButtonEnabled;
		}

		/// <summary>
		/// Returns whether any items are selected in the list box.
		/// </summary>
		bool IsItemSelected {
			get { return namesListBox.SelectedIndex >= 0; }
		}

		bool IsOkButtonEnabled {
			get { return IsItemSelected || ValidateCustomName(); }
		}

		/// <summary>
		/// Returns whether there is a valid string in the custom
		/// name text box. The string must be a name that can be used to
		/// create an xml element or attribute.
		/// </summary>
		bool ValidateCustomName()
		{
			string name = customNameTextBox.Text.Trim();
			if (name.Length > 0) {
				try {
					VerifyName(name);
					errorTextBlock.Text = string.Empty;
					return true;
				} catch (XmlException ex) {
					errorTextBlock.Text = ex.Message;
				}
			}
			return false;
		}

		/// <summary>
		/// Checks that the name would make a valid element name or
		/// attribute name. Trying to use XmlConvert and its Verify methods
		/// so the validation is not done ourselves. XmlDocument has a
		/// CheckName method but this is not public.
		/// </summary>
		static void VerifyName(string name)
		{
			// Check the qualification is valid.
			string[] parts = name.Split(new char[] {':'}, 2);
			if (parts.Length == 1) {
				// No colons.
				XmlConvert.VerifyName(name);
				return;
			}

			string firstPart = parts[0].Trim();
			string secondPart = parts[1].Trim();
			if (firstPart.Length > 0 && secondPart.Length > 0) {
				XmlConvert.VerifyNCName(firstPart);
				XmlConvert.VerifyNCName(secondPart);
			} else {
				// Throw an error using VerifyNCName since the
				// qualified name parts have no strings.
				XmlConvert.VerifyNCName(name);
			}
		}

		/// <summary>
		/// Sets the control's text using string resources.
		/// </summary>
		void InitStrings()
		{
			okButton.Content = StringParser.Parse("${res:Global.OKButtonText}");
			cancelButton.Content = StringParser.Parse("${res:Global.CancelButtonText}");
		}

		/// <summary>
		/// Removes the names list box from the dialog, re-positions the
		/// remaining controls and resizes the dialog to fit.
		/// </summary>
		void RemoveNamesListBox()
		{
			var root = (DockPanel)Content;
			root.Children.Remove(namesListBox);

			MinHeight = 0;
			SizeToContent = SizeToContent.Height;
		}
	}
}
