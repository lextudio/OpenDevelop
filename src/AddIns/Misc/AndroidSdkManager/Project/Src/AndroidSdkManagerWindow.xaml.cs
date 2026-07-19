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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using ICSharpCode.Core;
using ICSharpCode.TreeView;
using Microsoft.Win32;

namespace ICSharpCode.AndroidSdkManager
{
	public partial class AndroidSdkManagerWindow : Window
	{
		const string RecentPathsKey = "AndroidSdkManager.RecentPaths";

		readonly AndroidSdkManagerService service = new AndroidSdkManagerService();
		IReadOnlyList<SdkPackage> packages = Array.Empty<SdkPackage>();
		bool isRefreshing;

		public AndroidSdkManagerWindow()
		{
			InitializeComponent();
			Loaded += async (s, e) => await InitializeAsync();
		}

		async Task InitializeAsync()
		{
			isRefreshing = true;
			try {
				var recent = PropertyService.GetList<string>(RecentPathsKey);
				sdkLocationComboBox.ItemsSource = recent;
				sdkLocationComboBox.Text = AndroidSdkManagerService.GetSavedSdkPath();
			} finally {
				isRefreshing = false;
			}

			await RefreshAsync();
		}

		async Task RefreshAsync()
		{
			var sdkRoot = sdkLocationComboBox.Text;
			if (string.IsNullOrWhiteSpace(sdkRoot))
				return;

			try {
				packages = await service.ListPackagesAsync(sdkRoot);
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Android SDK Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
				packages = Array.Empty<SdkPackage>();
			}

			isRefreshing = true;
			try {
				platformsTreeView.Root = SdkPackageTreeBuilder.BuildPlatformsRoot(packages);
				toolsTreeView.Root = SdkPackageTreeBuilder.BuildToolsRoot(packages);
				HookCheckedChanged(platformsTreeView.Root);
				HookCheckedChanged(toolsTreeView.Root);
			} finally {
				isRefreshing = false;
			}

			UpdateStatusBar();
		}

		void HookCheckedChanged(SharpTreeNode root)
		{
			foreach (var node in root.Descendants().OfType<SdkPackageTreeNode>())
				node.PropertyChanged += (s, e) => { if (e.PropertyName == "IsChecked" && !isRefreshing) UpdateStatusBar(); };
		}

		IEnumerable<SdkPackageTreeNode> AllPackageNodes()
		{
			return platformsTreeView.Root.Descendants().OfType<SdkPackageTreeNode>()
				.Concat(toolsTreeView.Root.Descendants().OfType<SdkPackageTreeNode>());
		}

		void UpdateStatusBar()
		{
			var nodes = AllPackageNodes().ToList();
			var pendingCount = nodes.Count(n => n.IsPendingChange);
			var updateCount = nodes.Count(n => n.Package.HasUpdate);

			installCountText.Text = "Install: " + pendingCount;
			applyChangesButton.IsEnabled = pendingCount > 0;
			updatesAvailableButton.Content = updateCount + " Updates Available";
			updatesAvailableButton.IsEnabled = updateCount > 0;
		}

		void SdkLocationComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			if (isRefreshing)
				return;
			CommitSdkLocation();
		}

		void SdkLocationComboBox_LostFocus(object sender, RoutedEventArgs e)
		{
			CommitSdkLocation();
		}

		async void CommitSdkLocation()
		{
			var path = sdkLocationComboBox.Text;
			if (string.IsNullOrWhiteSpace(path) || path == AndroidSdkManagerService.GetSavedSdkPath())
				return;

			AndroidSdkManagerService.SaveSdkPath(path);

			var recent = PropertyService.GetList<string>(RecentPathsKey).ToList();
			recent.Remove(path);
			recent.Insert(0, path);
			if (recent.Count > 10)
				recent.RemoveRange(10, recent.Count - 10);
			PropertyService.SetList(RecentPathsKey, recent);
			sdkLocationComboBox.ItemsSource = recent;

			await RefreshAsync();
		}

		void BrowseButton_Click(object sender, RoutedEventArgs e)
		{
			var dialog = new OpenFolderDialog {
				Title = "Select Android SDK Location",
				InitialDirectory = sdkLocationComboBox.Text,
			};
			if (dialog.ShowDialog(this) == true) {
				sdkLocationComboBox.Text = dialog.FolderName;
				CommitSdkLocation();
			}
		}

		void UpdatesAvailableButton_Click(object sender, RoutedEventArgs e)
		{
			foreach (var node in AllPackageNodes().Where(n => n.Package.HasUpdate))
				node.IsChecked = true;
			UpdateStatusBar();
		}

		async void ApplyChangesButton_Click(object sender, RoutedEventArgs e)
		{
			await ApplyChangesAsync();
		}

		async Task ApplyChangesAsync()
		{
			var sdkRoot = sdkLocationComboBox.Text;
			var toInstall = AllPackageNodes().Where(n => n.IsPendingInstall).Select(n => n.Package.Id).ToList();
			var toRemove = AllPackageNodes().Where(n => n.IsPendingRemoval).Select(n => n.Package.Id).ToList();
			if (toInstall.Count == 0 && toRemove.Count == 0)
				return;

			applyChangesButton.IsEnabled = false;
			try {
				if (toInstall.Count > 0)
					await service.InstallAsync(sdkRoot, toInstall);
				if (toRemove.Count > 0)
					await service.UninstallAsync(sdkRoot, toRemove);
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Android SDK Manager", MessageBoxButton.OK, MessageBoxImage.Error);
			}

			await RefreshAsync();
		}

		// --- DevFlow test hooks (see AndroidSdkManagerDevFlowActions) ---

		public void SetSdkLocationForTesting(string path)
		{
			sdkLocationComboBox.Text = path;
			CommitSdkLocation();
		}

		public IEnumerable<SdkPackageTreeNode> GetAllPackageNodesForTesting()
		{
			return AllPackageNodes();
		}

		public void ApplyChangesForTesting()
		{
			_ = ApplyChangesAsync();
		}
	}
}
