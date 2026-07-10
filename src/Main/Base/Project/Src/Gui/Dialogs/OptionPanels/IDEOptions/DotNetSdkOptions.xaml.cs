using System.Linq;
using System.Windows;

using ICSharpCode.SharpDevelop.Project.Sdk;

namespace ICSharpCode.SharpDevelop.Gui.OptionPanels
{
	public partial class DotNetSdkOptions : OptionPanel
	{
		public DotNetSdkOptions()
		{
			InitializeComponent();
		}

		public override void LoadOptions()
		{
			base.LoadOptions();
			Refresh();
		}

		public override bool SaveOptions()
		{
			var selected = sdkListBox.SelectedItem as DotNetSdkInfo;
			// Selecting the entry that already represents "System" clears the stored override,
			// rather than pinning to that entry's exact root path - keeps the default meaning
			// "system SDK, whichever one that resolves to" even if the system SDK is later
			// upgraded/reinstalled at a slightly different path.
			DotNetSdkService.SelectedSdkRootPath =
				(selected == null || selected.Origin == DotNetSdkOrigin.System) ? string.Empty : selected.RootPath;
			return base.SaveOptions();
		}

		void Refresh()
		{
			var discovered = DotNetSdkService.DiscoverSdks();
			sdkListBox.ItemsSource = discovered;

			string selectedRoot = DotNetSdkService.SelectedSdkRootPath;
			DotNetSdkInfo toSelect = null;
			if (!string.IsNullOrEmpty(selectedRoot))
				toSelect = discovered.FirstOrDefault(s => string.Equals(s.RootPath, selectedRoot, System.StringComparison.OrdinalIgnoreCase));
			toSelect ??= discovered.FirstOrDefault(s => s.Origin == DotNetSdkOrigin.System);
			sdkListBox.SelectedItem = toSelect;

			var effective = DotNetSdkService.ResolveEffectiveSdk();
			effectiveSdkText.Text = $"Currently effective: {effective.Label}" +
				(effective.RootPath != null ? $" ({effective.RootPath})" : " (not found - falling back to PATH)");
		}

		void refreshButtonClick(object sender, RoutedEventArgs e)
		{
			Refresh();
		}

		void addCustomPathButtonClick(object sender, RoutedEventArgs e)
		{
			string path = SD.FileService.BrowseForFolder(
				"Select a folder containing a \"dotnet\" executable and an \"sdk\" subfolder", null);
			if (string.IsNullOrEmpty(path))
				return;
			DotNetSdkService.AddCustomRoot(path);
			Refresh();
		}
	}
}
