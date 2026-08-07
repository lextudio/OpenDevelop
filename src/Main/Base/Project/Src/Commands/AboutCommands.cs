using System;
using System.Diagnostics;
using System.Windows;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Updates;
using ICSharpCode.SharpDevelop.ViewModels;

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
	/// in WorkbenchStartup, and reports its result through the shell-wide notification banner
	/// (doc/technotes/ilspy.md "Follow-on infrastructure: a shell-wide notification banner") rather
	/// than a dialog, so it doesn't block the UI thread while the GitHub request is in flight.
	/// </summary>
	public class CheckForUpdates : AbstractMenuCommand
	{
		public override async void Run()
		{
			var notificationHost = SD.Services.GetService(typeof(INotificationHost)) as INotificationHost;
			notificationHost?.Show("Checking for updates...", null, null);

			string downloadUrl;
			try {
				downloadUrl = await UpdateService.CheckForUpdatesAsync(new UpdateSettings());
			} catch (Exception ex) {
				LoggingService.Debug("Update check failed: " + ex.Message);
				notificationHost?.Show("Update check failed.", null, null);
				return;
			}

			if (downloadUrl != null) {
				notificationHost?.Show(
					"A new version of OpenDevelop is available.",
					"Download",
					() => Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true }));
			} else {
				notificationHost?.Show("You have the latest version.", null, null);
			}
		}
	}
}
