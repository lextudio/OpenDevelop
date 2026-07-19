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

namespace ICSharpCode.AndroidSdkManager
{
	/// <summary>
	/// One row parsed from `sdkmanager --list --verbose` (installed/available/updates sections merged
	/// into a single record keyed by package Id, e.g. "platforms;android-26" or "build-tools;30.0.3").
	/// </summary>
	public sealed class SdkPackage
	{
		public string Id { get; set; }
		public string DisplayName { get; set; }
		public string InstalledVersion { get; set; }
		public string AvailableVersion { get; set; }
		public bool IsInstalled { get; set; }
		public bool HasUpdate { get; set; }

		/// <summary>
		/// sdkmanager's text output does not report package size, so this stays blank;
		/// kept as a field so the Size column has somewhere to bind if that ever changes.
		/// </summary>
		public string Size { get; set; } = string.Empty;

		public string VersionText {
			get { return IsInstalled ? InstalledVersion : AvailableVersion; }
		}

		public string StatusText {
			get {
				if (HasUpdate)
					return "Update available";
				if (IsInstalled)
					return "Installed";
				return string.Empty;
			}
		}
	}
}
