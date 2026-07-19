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

using System.Collections.Generic;

namespace ICSharpCode.AndroidDeviceManager
{
	public enum HardwarePropertyKind
	{
		Bool,
		Enum,
		Integer,
		Text
	}

	/// <summary>
	/// Static metadata (type, allowed values, default, human-readable description) for the
	/// hw.*/vm.*/disk.* keys stored in an AVD's config.ini, driving the Advanced Settings
	/// property grid's value editor and its Details panel - mirrors the semantics documented in
	/// the Android emulator's own hardware-properties.ini.
	/// </summary>
	public sealed class HardwareProperty
	{
		public string Key { get; }
		public string Title { get; }
		public string Description { get; }
		public HardwarePropertyKind Kind { get; }
		public IReadOnlyList<string> EnumValues { get; }
		public string DefaultValue { get; }

		public HardwareProperty(string key, string title, HardwarePropertyKind kind, string description, string defaultValue, IReadOnlyList<string> enumValues = null)
		{
			Key = key;
			Title = title;
			Kind = kind;
			Description = description;
			DefaultValue = defaultValue;
			EnumValues = enumValues;
		}
	}

	public static class HardwarePropertyCatalog
	{
		public static readonly IReadOnlyList<HardwareProperty> All = new List<HardwareProperty> {
			new HardwareProperty("hw.battery", "Battery", HardwarePropertyKind.Bool,
				"Whether the device has a battery. When off, the emulator behaves as if permanently connected to AC power.", "yes"),
			new HardwareProperty("hw.camera.back", "Back camera", HardwarePropertyKind.Enum,
				"The emulated back-facing camera source: emulated (uses a virtual scene), a connected webcam, or none.", "emulated",
				new[] { "none", "emulated", "virtualscene", "webcam0" }),
			new HardwareProperty("hw.camera.front", "Front camera", HardwarePropertyKind.Enum,
				"The emulated front-facing camera source.", "emulated",
				new[] { "none", "emulated", "webcam0" }),
			new HardwareProperty("hw.cpu.ncore", "CPU cores", HardwarePropertyKind.Integer,
				"Number of virtual CPU cores visible to the guest OS.", "4"),
			new HardwareProperty("hw.dPad", "D-Pad support", HardwarePropertyKind.Bool,
				"Whether the device has a directional pad (D-Pad).", "no"),
			new HardwareProperty("hw.gps", "GPS", HardwarePropertyKind.Bool,
				"Whether the device has a GPS (satellite navigation) receiver.", "yes"),
			new HardwareProperty("hw.gpu.mode", "GPU emulation mode", HardwarePropertyKind.Enum,
				"How graphics are rendered: auto lets the emulator pick, host uses the host GPU, swiftshader_indirect/angle_indirect are software renderers, guest renders entirely in software on the guest.", "auto",
				new[] { "auto", "host", "swiftshader_indirect", "angle_indirect", "guest" }),
			new HardwareProperty("hw.keyboard", "Keyboard", HardwarePropertyKind.Bool,
				"Whether a hardware QWERTY keyboard is present.", "yes"),
			new HardwareProperty("hw.lcd.density", "LCD density", HardwarePropertyKind.Enum,
				"The density of the emulated LCD display, measured in density-independent pixels, or 'dp' (dp is a virtual pixel unit). When the setting is 160 dp, each dp corresponds to one physical pixel. At runtime, Android uses this value to select and scale the appropriate resources/assets for correct display rendering.", "160",
				new[] { "120", "160", "213", "240", "260", "280", "300", "320", "340", "360", "400", "420", "440", "480", "560", "640" }),
			new HardwareProperty("hw.lcd.height", "LCD height", HardwarePropertyKind.Integer,
				"The height of the emulated LCD display, in pixels.", "1920"),
			new HardwareProperty("hw.lcd.width", "LCD width", HardwarePropertyKind.Integer,
				"The width of the emulated LCD display, in pixels.", "1080"),
			new HardwareProperty("hw.mainKeys", "Software navigation buttons", HardwarePropertyKind.Bool,
				"Whether the device relies on software Back/Home/Recents buttons rather than physical ones.", "no"),
			new HardwareProperty("hw.ramSize", "Memory (RAM)", HardwarePropertyKind.Integer,
				"The amount of physical RAM available to the emulated device, in megabytes.", "1536"),
			new HardwareProperty("hw.sdCard", "SD Card support", HardwarePropertyKind.Bool,
				"Whether the device supports a removable SD Card.", "yes"),
			new HardwareProperty("hw.sensors.orientation", "Orientation sensor", HardwarePropertyKind.Bool,
				"Whether the device has an orientation sensor (accelerometer/gyroscope-derived).", "yes"),
			new HardwareProperty("hw.sensors.proximity", "Proximity sensor", HardwarePropertyKind.Bool,
				"Whether the device has a proximity sensor.", "yes"),
			new HardwareProperty("hw.trackBall", "Trackball", HardwarePropertyKind.Bool,
				"Whether the device has a trackball.", "no"),
			new HardwareProperty("vm.heapSize", "VM heap size", HardwarePropertyKind.Integer,
				"The maximum heap size, in megabytes, available to each Dalvik/ART virtual machine instance on the device.", "256"),
			new HardwareProperty("disk.dataPartition.size", "Internal storage", HardwarePropertyKind.Text,
				"The size of the /data partition, e.g. '2048M' or '6G'.", "2048M"),
			new HardwareProperty("sdcard.size", "SD card size", HardwarePropertyKind.Text,
				"The size of the emulated SD card image, e.g. '512M' or '1G'.", "512M"),
		};

		static readonly Dictionary<string, HardwareProperty> byKey = BuildIndex();

		static Dictionary<string, HardwareProperty> BuildIndex()
		{
			var index = new Dictionary<string, HardwareProperty>(System.StringComparer.OrdinalIgnoreCase);
			foreach (var property in All)
				index[property.Key] = property;
			return index;
		}

		public static HardwareProperty Find(string key)
		{
			return byKey.TryGetValue(key, out var property) ? property : null;
		}
	}
}
