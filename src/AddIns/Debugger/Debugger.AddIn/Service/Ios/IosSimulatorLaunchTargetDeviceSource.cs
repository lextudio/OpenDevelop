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
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer.Devices;
using ICSharpCode.SharpDevelop.Project;

namespace Debugger.AddIn.Ios
{
	/// <summary>The iOS Simulators in the launch-target drop-down: the same choice as Debug &gt; iOS Simulator.</summary>
	public sealed class IosSimulatorLaunchTargetDeviceSource : ILaunchTargetDeviceSource
	{
		public string Platform => "ios";

		public event EventHandler DevicesChanged {
			add => DeviceService.Default.Changed += value;
			remove => DeviceService.Default.Changed -= value;
		}

		public IReadOnlyList<LaunchTargetDevice> GetDevices(IProject project)
		{
			string preferred = project.Preferences.Get(IosSimulatorProjectBehavior.SimulatorPreferenceKey, string.Empty);
			return IosSimulatorMenuBuilder.BuildChoices(DeviceService.Default.Devices, preferred,
					StringParser.Parse("${res:MainWindow.Windows.Debug.IosSimulator.Automatic}"))
				.Select(c => new LaunchTargetDevice(c.Udid, c.Label, c.IsChecked)).ToList();
		}

		public void Select(IProject project, string deviceId) => IosSimulatorMenuBuilder.Select(project, deviceId);

		public void RequestRefresh() => DeviceService.Default.RefreshAsync().FireAndForget();
	}
}
