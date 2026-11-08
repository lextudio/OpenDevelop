// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using NuGet;

namespace ICSharpCode.PackageManagement
{
	public interface ISettingsProvider
	{
		event EventHandler SettingsChanged;
		ISettings LoadSettings();
		/// <summary>NuGet's own view of the configuration that applies to the open solution (its directory and every directory above it, then the user's and the machine's files).</summary>
		NuGet.Configuration.ISettings LoadNuGetSettings();
	}
}
