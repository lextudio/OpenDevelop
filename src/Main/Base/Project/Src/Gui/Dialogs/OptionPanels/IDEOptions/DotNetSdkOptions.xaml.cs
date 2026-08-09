using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using ICSharpCode.Core;
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
			ApplyDiscoveredSdks(DotNetSdkService.DiscoverSdks());
		}

		public override async Task LoadOptionsAsync(CancellationToken cancellationToken)
		{
			// PropertyService is UI-owned; snapshot its values before moving filesystem scans
			// to a worker thread.
			var customRoots = DotNetSdkService.CustomRoots.ToArray();
			var discovered = await Task.Run(() => DotNetSdkService.DiscoverSdks(customRoots), cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			ApplyDiscoveredSdks(discovered);
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

		void ApplyDiscoveredSdks(System.Collections.Generic.IReadOnlyList<DotNetSdkInfo> discovered, string preferredRoot = null)
		{
			sdkListBox.ItemsSource = discovered;

			string selectedRoot = preferredRoot ?? DotNetSdkService.SelectedSdkRootPath;
			DotNetSdkInfo toSelect = null;
			if (!string.IsNullOrEmpty(selectedRoot))
				toSelect = discovered.FirstOrDefault(s => string.Equals(s.RootPath, selectedRoot, System.StringComparison.OrdinalIgnoreCase));
			toSelect ??= discovered.FirstOrDefault(s => s.Origin == DotNetSdkOrigin.System);
			sdkListBox.SelectedItem = toSelect;

			UpdateSelectedSdkText();
		}

		void sdkListBoxSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			UpdateSelectedSdkText();
		}

		void UpdateSelectedSdkText()
		{
			var selected = sdkListBox.SelectedItem as DotNetSdkInfo;
			effectiveSdkText.Text = selected == null
				? "Selected SDK: none"
				: $"Selected SDK: {selected.Label} ({selected.RootPath})";
		}

		async void refreshButtonClick(object sender, RoutedEventArgs e)
		{
			await LoadOptionsAsync(CancellationToken.None);
		}

		async void addCustomPathButtonClick(object sender, RoutedEventArgs e)
		{
			string path = SD.FileService.BrowseForFolder(
				"Select a folder containing a \"dotnet\" executable and an \"sdk\" subfolder", null);
			if (string.IsNullOrEmpty(path))
				return;
			if (!DotNetSdkService.TryDescribeCustomRoot(path, out var customSdk, out string error)) {
				MessageService.ShowError(error);
				return;
			}

			DotNetSdkService.AddCustomRoot(customSdk.RootPath);
			var customRoots = DotNetSdkService.CustomRoots.ToArray();
			var discovered = await Task.Run(() => DotNetSdkService.DiscoverSdks(customRoots));
			ApplyDiscoveredSdks(discovered, customSdk.RootPath);
		}
	}
}
