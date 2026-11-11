// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;

using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Project
{
	/// <summary>One device a <see cref="ILaunchTargetDeviceSource"/> offers; a null <see cref="Id"/> is "Automatic".</summary>
	public sealed record LaunchTargetDevice(string Id, string Label, bool IsSelected);

	/// <summary>
	/// A list of devices Run and Debug can target for one platform (an iOS Simulator, an Android device), shown
	/// in the launch-target drop-down below the profiles of that platform. Registered by add-ins at
	/// <see cref="AddInPath"/>; the drop-down must never wait for the machine, so <see cref="GetDevices"/>
	/// answers from the last snapshot and <see cref="RequestRefresh"/> updates it in the background.
	/// </summary>
	public interface ILaunchTargetDeviceSource
	{
		/// <summary>The <c>compatibleTargetFramework</c> platform name this source serves: "ios" or "android".</summary>
		string Platform { get; }

		/// <summary>The last known devices, "Automatic" first, the project's stored choice marked selected.</summary>
		IReadOnlyList<LaunchTargetDevice> GetDevices(IProject project);

		/// <summary>Stores the device Run and Debug use for <paramref name="project"/>; null means "Automatic".</summary>
		void Select(IProject project, string deviceId);

		/// <summary>Starts looking for devices; <see cref="DevicesChanged"/> is raised when the list differs.</summary>
		void RequestRefresh();

		/// <summary>Raised, on any thread, after a refresh produced a different list.</summary>
		event EventHandler DevicesChanged;
	}

	public static class LaunchTargetDeviceSources
	{
		public const string AddInPath = "/SharpDevelop/Workbench/LaunchTargetDeviceSources";

		static IReadOnlyList<ILaunchTargetDeviceSource> sources;

		/// <summary>The registered sources; an override is for tests.</summary>
		public static IReadOnlyList<ILaunchTargetDeviceSource> All {
			get => sources ??= AddInTree.BuildItems<ILaunchTargetDeviceSource>(AddInPath, null, false).ToArray();
			set => sources = value;
		}

		public static ILaunchTargetDeviceSource ForPlatform(string platform)
		{
			return string.IsNullOrEmpty(platform) ? null
				: All.FirstOrDefault(s => string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase));
		}
	}
}
