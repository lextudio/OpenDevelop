// Copyright (c) 2026 AlphaSierraPapa for the SharpDevelop Team
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

using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ICSharpCode.ILSpy.Views
{
	public partial class SetTargetFrameworkDialog : Window
	{
		static readonly IReadOnlyList<string> CommonFrameworks = new[] {
			"net11.0", "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0",
			"netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1",
			"netstandard2.1", "netstandard2.0",
			"net48", "net472", "net471", "net47", "net462", "net461", "net46",
		};

		public string Result { get; private set; }

		public SetTargetFrameworkDialog()
		{
			InitializeComponent();
			PromptText.Text = Properties.Resources.TargetFramework;
			PresetList.ItemsSource = CommonFrameworks;
			PresetList.SelectionChanged += (_, _) => {
				if (PresetList.SelectedItem is string preset)
					FrameworkBox.Text = preset;
			};
			PresetList.MouseDoubleClick += (_, _) => {
				if (PresetList.SelectedItem is string)
					OnOk();
			};
			OkButton.Click += (_, _) => OnOk();
			CancelButton.Click += (_, _) => { DialogResult = false; };
		}

		public SetTargetFrameworkDialog(string currentFrameworkName) : this()
		{
			Title = Properties.Resources.SetTargetFramework;
			var shortName = TargetFrameworkConverter.ToShortFolderName(currentFrameworkName);
			if (!string.IsNullOrEmpty(shortName))
			{
				FrameworkBox.Text = shortName;
				PresetList.SelectedItem = CommonFrameworks.FirstOrDefault(f => f == shortName);
			}
		}

		void OnOk()
		{
			var text = FrameworkBox.Text;
			if (string.IsNullOrWhiteSpace(text))
			{
				Result = string.Empty;
				DialogResult = true;
				return;
			}
			if (!TargetFrameworkConverter.TryParseToFrameworkName(text, out var frameworkName, out var error))
			{
				ErrorText.Text = error;
				ErrorText.Visibility = Visibility.Visible;
				return;
			}
			Result = frameworkName;
			DialogResult = true;
		}
	}
}
