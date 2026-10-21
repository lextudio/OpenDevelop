using System.Globalization;
using System.Windows;
using ICSharpCode.Core;
using Microsoft.Win32;
using OpenDevelop.Profiler.AddIn.Services;

namespace OpenDevelop.Profiler.AddIn.Dialogs;

/// <summary>Analysis &gt; Profiler &gt; Profiler Settings: how many captures to keep, when to ask before opening one, and where they go.</summary>
public partial class SettingsWindow : Window
{
	public SettingsWindow()
	{
		InitializeComponent();
		retainedBox.Text = ProfilerSettings.RetainedSessions.ToString(CultureInfo.CurrentCulture);
		largeBox.Text = ProfilerSettings.LargeCaptureMegabytes.ToString(CultureInfo.CurrentCulture);
		folderBox.Text = ProfilerSettings.StoreFolder;
		scanBox.IsChecked = ProfilerSettings.ScanHeapContents;
	}

	public static void ShowSettings(Window? owner) => new SettingsWindow { Owner = owner }.ShowDialog();

	void Browse_Click(object sender, RoutedEventArgs e)
	{
		var dialog = new OpenFolderDialog();
		if (dialog.ShowDialog() == true)
			folderBox.Text = dialog.FolderName;
	}

	void Ok_Click(object sender, RoutedEventArgs e)
	{
		// Nothing is saved unless every field is valid, so a typo cannot half-apply.
		if (!TryParse(retainedBox.Text, out var retained) || !TryParse(largeBox.Text, out var largeMb))
		{
			errorText.Text = StringParser.Parse("${res:AddIns.Profiler.Settings.Invalid}");
			errorText.Visibility = Visibility.Visible;
			return;
		}
		ProfilerSettings.RetainedSessions = retained;
		ProfilerSettings.LargeCaptureMegabytes = largeMb;
		ProfilerSettings.StoreFolder = folderBox.Text;
		ProfilerSettings.ScanHeapContents = scanBox.IsChecked == true;
		DialogResult = true;
	}

	static bool TryParse(string text, out int value) =>
		int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.CurrentCulture, out value) && value >= 1;
}
