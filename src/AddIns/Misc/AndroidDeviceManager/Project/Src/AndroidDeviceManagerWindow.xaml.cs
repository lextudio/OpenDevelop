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
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

using ICSharpCode.SharpDevelop;

namespace ICSharpCode.AndroidDeviceManager
{
	public partial class AndroidDeviceManagerWindow : Window
	{
		readonly AvdManagerService service = new AvdManagerService();

		public AndroidDeviceManagerWindow()
		{
			InitializeComponent();
			Loaded += async (s, e) => await RefreshAsync();
		}

		string SdkRoot => AvdManagerService.GetSavedSdkPath();

		public async Task RefreshAsync()
		{
			if (string.IsNullOrWhiteSpace(SdkRoot)) {
				MessageBox.Show(this, "Set the Android SDK location in the Android SDK Manager first.", "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}

			try {
				var avds = await service.ListAvdsAsync(SdkRoot);
				avdListView.ItemsSource = avds;
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		}

		AvdInfo SelectedAvd => avdListView.SelectedItem as AvdInfo;

		async void CreateButton_Click(object sender, RoutedEventArgs e)
		{
			var editor = new AvdEditorWindow(SdkRoot, existingAvd: null) { Owner = this };
			if (editor.ShowDialog() == true)
				await RefreshAsync();
		}

		async void EditButton_Click(object sender, RoutedEventArgs e)
		{
			var avd = SelectedAvd;
			if (avd == null)
				return;
			var editor = new AvdEditorWindow(SdkRoot, avd) { Owner = this };
			if (editor.ShowDialog() == true)
				await RefreshAsync();
		}

		void StartButton_Click(object sender, RoutedEventArgs e)
		{
			var avd = SelectedAvd;
			if (avd == null)
				return;
			try {
				service.StartAvd(SdkRoot, avd.Name);
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		async void DeleteButton_Click(object sender, RoutedEventArgs e)
		{
			var avd = SelectedAvd;
			if (avd == null)
				return;
			if (MessageBox.Show(this, "Delete AVD \"" + avd.Name + "\"? This cannot be undone.", "Android Device Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
				return;

			try {
				await service.DeleteAvdAsync(SdkRoot, avd.Name);
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			await RefreshAsync();
		}

		async void RefreshButton_Click(object sender, RoutedEventArgs e)
		{
			await RefreshAsync();
		}

		public System.Collections.Generic.IEnumerable<AvdInfo> GetAvdsForTesting()
		{
			return (avdListView.ItemsSource as System.Collections.Generic.IEnumerable<AvdInfo>) ?? Enumerable.Empty<AvdInfo>();
		}
	}
}
