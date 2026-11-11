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
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;

namespace Debugger.AddIn.Android
{
	/// <summary>
	/// The Android devices in the launch-target drop-down: the same choice as Debug &gt; Android Device. Listing
	/// them runs <c>adb devices</c>, so the drop-down reads the last snapshot and a refresh runs in the background.
	/// </summary>
	public sealed class AndroidLaunchTargetDeviceSource : ILaunchTargetDeviceSource
	{
		static IReadOnlyList<string> snapshot = Array.Empty<string>();
		static int refreshing;

		public string Platform => "android";

		public event EventHandler DevicesChanged;

		public IReadOnlyList<LaunchTargetDevice> GetDevices(IProject project)
		{
			string preferred = project.Preferences.Get(AndroidDebugProjectBehavior.DevicePreferenceKey, string.Empty);
			return AndroidDebugDeviceMenuBuilder.BuildChoices(Volatile.Read(ref snapshot), preferred,
					StringParser.Parse("${res:MainWindow.Windows.Debug.AndroidDevice.Automatic}"))
				.Select(c => new LaunchTargetDevice(c.Serial, c.Label, c.IsChecked)).ToList();
		}

		public void Select(IProject project, string deviceId) => AndroidDebugDeviceMenuBuilder.Select(project, deviceId);

		public void RequestRefresh()
		{
			if (Interlocked.Exchange(ref refreshing, 1) != 0)
				return;
			Task.Run(() => {
				try {
					var devices = AndroidDebugDeviceMenuBuilder.ListDevices().OrderBy(s => s, StringComparer.Ordinal).ToArray();
					if (!devices.SequenceEqual(Volatile.Read(ref snapshot))) {
						Volatile.Write(ref snapshot, devices);
						DevicesChanged?.Invoke(this, EventArgs.Empty);
					}
				} finally {
					Interlocked.Exchange(ref refreshing, 0);
				}
			});
		}
	}
}
