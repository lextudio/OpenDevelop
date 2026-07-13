using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hornung.ResourceToolkit.ResourceFileContent;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace Hornung.ResourceToolkit.Gui
{
    public class EditStringResourceDialog : Window
    {
        readonly IResourceFileContent content;
        readonly TextBox keyTextBox;
        readonly TextBox valueTextBox;

        public EditStringResourceDialog(IResourceFileContent content, string key, string value, bool allowEditKey) : base()
        {
            this.content = content;
            key = key ?? String.Empty;
            value = value ?? String.Empty;

            this.Title = StringParser.Parse("${res:Hornung.ResourceToolkit.EditStringResourceDialog.Caption}");
            this.SizeToContent = SizeToContent.WidthAndHeight;
            this.Width = 380;
            this.ResizeMode = ResizeMode.NoResize;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var stackPanel = new StackPanel { Margin = new Thickness(12) };

            var keyLabel = new Label { Content = StringParser.Parse("${res:Hornung.ResourceToolkit.KeyLabel}") };
            stackPanel.Children.Add(keyLabel);

            keyTextBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
            if (allowEditKey) {
                keyTextBox.LostKeyboardFocus += KeyValidating;
            } else {
                keyTextBox.IsReadOnly = true;
            }
            keyTextBox.Text = key;
            stackPanel.Children.Add(keyTextBox);

            var valueLabel = new Label { Content = StringParser.Parse("${res:Hornung.ResourceToolkit.ValueLabel}") };
            stackPanel.Children.Add(valueLabel);

            valueTextBox = new TextBox {
                Margin = new Thickness(0, 0, 0, 8),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 90
            };
            valueTextBox.Text = value;
            stackPanel.Children.Add(valueTextBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var okButton = new Button {
                Content = StringParser.Parse("${res:Global.OKButtonText}"),
                Width = 75,
                Height = 23,
                Margin = new Thickness(0, 0, 6, 0),
                IsDefault = true
            };
            okButton.Click += OkButtonClick;
            buttonPanel.Children.Add(okButton);

            var cancelButton = new Button {
                Content = StringParser.Parse("${res:Global.CancelButtonText}"),
                Width = 75,
                Height = 23,
                IsCancel = true
            };
            buttonPanel.Children.Add(cancelButton);

            stackPanel.Children.Add(buttonPanel);
            this.Content = stackPanel;

            if (allowEditKey) {
                keyTextBox.Focus();
                keyTextBox.Select(key.Length, 0);
            } else {
                valueTextBox.Focus();
                valueTextBox.Select(value.Length, 0);
            }
        }

        void KeyValidating(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (keyTextBox.Text.Trim().Length == 0) {
                e.Handled = true;
                keyTextBox.Focus();
                MessageService.ShowWarning("${res:Hornung.ResourceToolkit.EditStringResourceDialog.KeyIsEmpty}");
            } else if (this.content.ContainsKey(keyTextBox.Text.Trim())) {
                e.Handled = true;
                keyTextBox.Focus();
                MessageService.ShowWarning("${res:Hornung.ResourceToolkit.EditStringResourceDialog.DuplicateKey}");
            }
        }

        void OkButtonClick(object sender, RoutedEventArgs e)
        {
            if (keyTextBox.Text.Trim().Length == 0) {
                MessageService.ShowWarning("${res:Hornung.ResourceToolkit.EditStringResourceDialog.KeyIsEmpty}");
                keyTextBox.Focus();
                return;
            }
            if (this.content != null && this.content.ContainsKey(keyTextBox.Text.Trim())) {
                MessageService.ShowWarning("${res:Hornung.ResourceToolkit.EditStringResourceDialog.DuplicateKey}");
                keyTextBox.Focus();
                return;
            }
            this.DialogResult = true;
        }

        public string Key {
            get { return keyTextBox.Text.Trim(); }
        }

        public string Value {
            get { return valueTextBox.Text; }
        }
    }
}
