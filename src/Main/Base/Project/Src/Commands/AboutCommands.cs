using System;
using System.Diagnostics;
using System.Windows;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Updates;

namespace ICSharpCode.SharpDevelop.Commands
{
	public class AboutSharpDevelop : AbstractMenuCommand
	{
		public override void Run()
		{
			var owner = Application.Current.MainWindow;
			var dialog = new Gui.AboutDialog
			{
				Owner = owner,
				WindowStartupLocation = WindowStartupLocation.CenterOwner
			};
			dialog.ShowDialog();
		}
	}

	/// <summary>
	/// User-initiated update check (doc/technotes/auto-update.md). Always force-checks
	/// (<see cref="UpdateService.CheckForUpdatesAsync"/>), unlike the silent weekly startup check
	/// in WorkbenchStartup. Results are shown in the status bar so checking does not interrupt the
	/// current work; an available update includes a Download button there.
	/// </summary>
	public class CheckForUpdates : AbstractMenuCommand
	{
		public override async void Run()
		{
			SD.StatusBar.SetMessage("${res:Updates.StatusBar.Checking}");

			string downloadUrl;
			try {
				downloadUrl = await UpdateService.CheckForUpdatesAsync(new UpdateSettings());
			} catch (Exception ex) {
				LoggingService.Debug("Update check failed: " + ex.Message);
				SD.StatusBar.SetMessage("${res:Updates.StatusBar.CheckFailed}", highlighted: true);
				return;
			}

			if (downloadUrl != null) {
				SD.StatusBar.SetMessage(
					"${res:Updates.StatusBar.UpdateAvailable}",
					highlighted: true,
					actionText: "${res:Updates.StatusBar.Download}",
					action: () => Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true }));
			} else {
				SD.StatusBar.SetMessage("${res:Updates.StatusBar.LatestVersion}");
			}
		}
	}
}
