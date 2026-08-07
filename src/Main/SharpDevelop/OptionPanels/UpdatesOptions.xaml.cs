using System;

using ICSharpCode.SharpDevelop.Gui;
using ICSharpCode.SharpDevelop.Updates;

namespace ICSharpCode.SharpDevelop.OptionPanels
{
	partial class UpdatesOptions : OptionPanel
	{
		public UpdatesOptions()
		{
			InitializeComponent();
			automaticCheckBox.IsChecked = new UpdateSettings().AutomaticUpdateCheckEnabled;
		}

		public override bool SaveOptions()
		{
			new UpdateSettings().AutomaticUpdateCheckEnabled = automaticCheckBox.IsChecked == true;
			return base.SaveOptions();
		}

		async void CheckNowButton_Click(object sender, System.Windows.RoutedEventArgs e)
		{
			checkNowButton.IsEnabled = false;
			checkResultText.Text = "Checking...";
			try {
				string downloadUrl = await UpdateService.CheckForUpdatesAsync(new UpdateSettings());
				checkResultText.Text = downloadUrl != null
					? "A new version is available."
					: "You have the latest version.";
			} catch (Exception ex) {
				checkResultText.Text = "Check failed: " + ex.Message;
			} finally {
				checkNowButton.IsEnabled = true;
			}
		}
	}
}
