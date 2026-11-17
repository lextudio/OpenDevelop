using System;
using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.AddInManager2.ViewModel;

namespace ICSharpCode.AddInManager2.View
{
	/// <summary>
	/// Options panel listing the GitHub repositories whose add-ins the AddIn Manager offers.
	/// </summary>
	public partial class TrustedRepositoriesView : OptionPanel
	{
		private TrustedRepositoriesViewModel viewModel;

		public TrustedRepositoriesView()
		{
			InitializeComponent();
		}

		private TrustedRepositoriesViewModel ViewModel
		{
			get
			{
				if (viewModel == null)
				{
					viewModel = DataContext as TrustedRepositoriesViewModel;
				}
				return viewModel;
			}
		}

		public override void LoadOptions()
		{
			ViewModel.Load();
		}

		public override bool SaveOptions()
		{
			ViewModel.Save();
			return true;
		}
	}
}
