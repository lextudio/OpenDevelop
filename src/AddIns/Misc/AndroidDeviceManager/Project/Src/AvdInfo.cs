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

namespace ICSharpCode.AndroidDeviceManager
{
	/// <summary>One row parsed from `avdmanager list avd`.</summary>
	public sealed class AvdInfo
	{
		public string Name { get; set; }
		public string Device { get; set; }
		public string Path { get; set; }
		public string Target { get; set; }
		public string BasedOn { get; set; }
		public string Skin { get; set; }
		public string Sdcard { get; set; }

		public string ConfigIniPath => System.IO.Path.Combine(Path ?? string.Empty, "config.ini");
	}

	/// <summary>One row parsed from `avdmanager list device`: a hardware profile like "pixel_3a" / "Pixel 3a".</summary>
	public sealed class DeviceDefinition
	{
		public string Id { get; set; }
		public string DisplayName { get; set; }

		public override string ToString() => DisplayName;
	}

	/// <summary>One row parsed from `sdkmanager --list` restricted to "system-images;..." packages.</summary>
	public sealed class SystemImageInfo
	{
		public string PackageId { get; set; }
		public string ApiLevel { get; set; }
		public string Tag { get; set; }
		public string Abi { get; set; }
		public string DisplayName { get; set; }

		public override string ToString() => DisplayName;
	}
}
