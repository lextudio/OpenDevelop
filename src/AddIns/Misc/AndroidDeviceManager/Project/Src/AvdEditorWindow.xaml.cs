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
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace ICSharpCode.AndroidDeviceManager
{
	public partial class AvdEditorWindow : Window
	{
		static readonly string[] DefaultPropertyKeysForNewAvd = {
			"hw.battery", "hw.camera.back", "hw.camera.front", "hw.cpu.ncore", "hw.dPad",
			"hw.gps", "hw.gpu.mode", "hw.keyboard", "hw.lcd.density", "hw.mainKeys",
			"hw.ramSize", "hw.sdCard", "hw.sensors.orientation", "hw.sensors.proximity", "hw.trackBall",
			"sdcard.size",
		};

		readonly AvdManagerService service = new AvdManagerService();
		readonly string sdkRoot;
		readonly AvdInfo existingAvd;
		readonly ObservableCollection<PropertyRow> rows = new ObservableCollection<PropertyRow>();

		public AvdEditorWindow(string sdkRoot, AvdInfo existingAvd)
		{
			this.sdkRoot = sdkRoot;
			this.existingAvd = existingAvd;
			InitializeComponent();
			Title = existingAvd == null ? "Create Android Virtual Device" : "Edit " + existingAvd.Name;

			propertyListView.ItemsSource = rows;
			Loaded += async (s, e) => await InitializeAsync();
		}

		async System.Threading.Tasks.Task InitializeAsync()
		{
			try {
				var devices = await service.ListDeviceDefinitionsAsync(sdkRoot);
				baseDeviceComboBox.ItemsSource = devices;

				var images = await service.ListInstalledSystemImagesAsync(sdkRoot);
				systemImageComboBox.ItemsSource = images;
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
			}

			if (existingAvd != null) {
				nameTextBox.Text = existingAvd.Name;
				nameTextBox.IsEnabled = false;

				var device = (baseDeviceComboBox.ItemsSource as System.Collections.Generic.IEnumerable<DeviceDefinition>)
					?.FirstOrDefault(d => string.Equals(d.Id, existingAvd.Device, StringComparison.OrdinalIgnoreCase));
				if (device != null)
					baseDeviceComboBox.SelectedItem = device;

				var config = AvdConfig.Load(existingAvd.ConfigIniPath);
				foreach (var key in config.Keys) {
					var definition = HardwarePropertyCatalog.Find(key);
					if (definition != null)
						rows.Add(new PropertyRow(definition, config.Get(key)));
				}
			} else {
				foreach (var key in DefaultPropertyKeysForNewAvd) {
					var definition = HardwarePropertyCatalog.Find(key);
					if (definition != null)
						rows.Add(new PropertyRow(definition, definition.DefaultValue));
				}
			}

			RefreshAddPropertyList();
		}

		void RefreshAddPropertyList()
		{
			var present = rows.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
			addPropertyComboBox.ItemsSource = HardwarePropertyCatalog.All.Where(p => !present.Contains(p.Key)).ToList();
		}

		void PropertyListView_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			var row = propertyListView.SelectedItem as PropertyRow;
			detailsTitleText.Text = row?.Title ?? string.Empty;
			detailsDefaultText.Text = row != null ? "Default: " + HardwarePropertyCatalog.Find(row.Key)?.DefaultValue : string.Empty;
			detailsDescriptionText.Text = row?.Description ?? string.Empty;
		}

		void AddPropertyButton_Click(object sender, RoutedEventArgs e)
		{
			if (addPropertyComboBox.SelectedItem is HardwareProperty definition) {
				var row = new PropertyRow(definition, definition.DefaultValue);
				rows.Add(row);
				RefreshAddPropertyList();
				propertyListView.SelectedItem = row;
			}
		}

		void RemovePropertyButton_Click(object sender, RoutedEventArgs e)
		{
			if ((sender as FrameworkElement)?.Tag is PropertyRow row) {
				rows.Remove(row);
				RefreshAddPropertyList();
			}
		}

		async void SaveButton_Click(object sender, RoutedEventArgs e)
		{
			var name = nameTextBox.Text?.Trim();
			if (string.IsNullOrEmpty(name)) {
				MessageBox.Show(this, "Enter a name for the device.", "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
				return;
			}

			saveButton.IsEnabled = false;
			try {
				string configPath;
				if (existingAvd == null) {
					var image = systemImageComboBox.SelectedItem as SystemImageInfo;
					if (image == null) {
						MessageBox.Show(this, "Select a system image (OS).", "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
						return;
					}
					var device = baseDeviceComboBox.SelectedItem as DeviceDefinition;
					var created = await service.CreateAvdAsync(sdkRoot, name, image.PackageId, device?.Id, force: true);
					if (!created) {
						MessageBox.Show(this, "avdmanager failed to create the AVD. Check the SDK location and that the selected system image is installed.", "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Error);
						return;
					}
					configPath = System.IO.Path.Combine(
						System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
						".android", "avd", name + ".avd", "config.ini");
				} else {
					configPath = existingAvd.ConfigIniPath;
				}

				var config = AvdConfig.Load(configPath);
				foreach (var row in rows)
					config.Set(row.Key, row.Value);
				config.Save(configPath);

				DialogResult = true;
			} catch (Exception ex) {
				MessageBox.Show(this, ex.Message, "Android Device Manager", MessageBoxButton.OK, MessageBoxImage.Error);
			} finally {
				saveButton.IsEnabled = true;
			}
		}

		void CancelButton_Click(object sender, RoutedEventArgs e)
		{
			DialogResult = false;
		}

		public System.Collections.Generic.IEnumerable<PropertyRow> GetRowsForTesting() => rows;
	}
}
