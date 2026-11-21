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
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Windows;

using ICSharpCode.SharpDevelop.Designer.Devices;
using ICSharpCode.SharpDevelop.Designer.Presentation;

namespace ICSharpCode.DeviceManager
{
	/// <summary>
	/// The one place that opens the shared Device Manager dialog, so the Tools command and the DevFlow
	/// actions cannot drift apart (owners, non-modal behaviour, preferences source).
	/// </summary>
	internal static class DeviceManagerWindow
	{
		static DeviceManagerDialog? open;

		public static bool IsOpen => open is { IsVisible: true };

		public static void Show(Window? owner)
		{
			open?.Close();
			open = new DeviceManagerDialog(
				DeviceService.Default,
				DevicePreferencesStore.Load,
				preferences => DevicePreferencesStore.Save(preferences)) {
				Owner = owner,
			};
			open.Show();
		}

		public static void Close()
		{
			open?.Close();
			open = null;
		}
	}
}
