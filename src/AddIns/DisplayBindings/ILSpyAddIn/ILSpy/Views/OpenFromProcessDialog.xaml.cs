// Copyright (c) 2026 Christoph Wille
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

using System.Linq;
using System.Windows;
using System.Windows.Controls;

using ICSharpCode.ILSpy.Processes;
using ICSharpCode.ILSpy.ViewModels;

using Loc = ICSharpCode.ILSpy.Properties.Resources;

namespace ICSharpCode.ILSpy.Views
{
	public partial class OpenFromProcessDialog : Window
	{
		readonly OpenFromProcessDialogViewModel viewModel;

		public OpenFromProcessDialog()
			: this(new ProcessExplorer()) { }

		public OpenFromProcessDialog(IProcessExplorer explorer)
		{
			InitializeComponent();
			viewModel = new OpenFromProcessDialogViewModel(explorer);
			DataContext = viewModel;

			Title = Loc.OpenFromProcess_Title;
			FilterLabel.Content = Loc.OpenFromProcess_Filter;
			RefreshButton.Content = Loc.OpenFromProcess_Refresh;
			ModulesLabel.Text = Loc.OpenFromProcess_Assemblies;
			AddSelectedButton.Content = Loc.OpenFromProcess_AddSelected;
			AddEntryAssemblyButton.Content = Loc.OpenFromProcess_AddEntryAssembly;
			VisibilityHint.Text = Loc.OpenFromProcess_VisibilityHint;

			viewModel.CloseRequested += paths => {
				DialogResult = true;
				Close();
			};

			viewModel.PropertyChanged += (_, e) => {
				if (e.PropertyName == nameof(viewModel.IsLoadingModules))
					LoadingBar.Visibility = viewModel.IsLoadingModules ? Visibility.Visible : Visibility.Collapsed;
				if (e.PropertyName == nameof(viewModel.ErrorMessage))
				{
					if (!string.IsNullOrEmpty(viewModel.ErrorMessage))
					{
						ErrorText.Text = viewModel.ErrorMessage;
						ErrorText.Visibility = Visibility.Visible;
					}
					else
					{
						ErrorText.Visibility = Visibility.Collapsed;
					}
				}
			};

			Loaded += (_, _) => {
				viewModel.RefreshCommand.Execute(null);
				FilterBox.Focus();
			};

			Closing += (_, _) => viewModel.CancelAllOperations();
		}

		public string[] SelectedPaths { get; private set; }

		void OnAddSelected(object sender, RoutedEventArgs e)
		{
			SelectedPaths = viewModel.SelectedModules
				.Where(m => !m.IsInMemory && m.Path != null)
				.Select(m => m.Path)
				.ToArray();
			DialogResult = true;
			Close();
		}

		void OnAddEntryAssembly(object sender, RoutedEventArgs e)
		{
			SelectedPaths = new[] { viewModel.SelectedProcess?.Process.ResolveEntryAssemblyPath(viewModel.Modules.Select(m => m.Module).ToList()) };
			DialogResult = true;
			Close();
		}
	}
}
