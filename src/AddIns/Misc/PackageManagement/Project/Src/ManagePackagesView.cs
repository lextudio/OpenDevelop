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
using System.Windows;

namespace ICSharpCode.PackageManagement
{
	public partial class ManagePackagesView : Window, IManagePackagesView
	{
		bool disposed;

		public ManagePackagesView()
		{
			InitializeComponent();
			Closed += (sender, e) => Dispose();
		}

		void CloseButton_Click(object sender, RoutedEventArgs e)
		{
			// IsCancel assigns DialogResult, which only works for ShowDialog(). DevFlow drives
			// this same view with Show(), so use Close() to support both modal and non-modal hosts.
			Close();
		}

		/// <summary>Shows the Updates view filtered to one package reference from Solution Explorer.</summary>
		public void ShowUpdatesFor(string packageId)
		{
			if (string.IsNullOrWhiteSpace(packageId))
				return;
			PackageUpdatesTab.IsSelected = true;
			var viewModel = DataContext as ManagePackagesViewModel;
			if (viewModel == null)
				return;
			viewModel.UpdatedPackagesViewModel.SearchTerms = packageId;
			viewModel.UpdatedPackagesViewModel.SearchCommand.Execute(null);
		}

		public string SelectedTabHeader => PackageUpdatesTab.IsSelected ? PackageUpdatesTab.Header?.ToString() : null;

		public string UpdatesSearchTerms => (DataContext as ManagePackagesViewModel)?.UpdatedPackagesViewModel.SearchTerms;
		
		public void Dispose()
		{
			if (disposed)
				return;
			disposed = true;
			var viewModel = DataContext as ManagePackagesViewModel;
			viewModel?.Dispose();
		}
	}
}
