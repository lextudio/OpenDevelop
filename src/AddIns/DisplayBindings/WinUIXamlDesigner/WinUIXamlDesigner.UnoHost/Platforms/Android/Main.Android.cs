using System;
using Android.App;
using Android.Runtime;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost.Droid
{
	/// <summary>The Android application: Skia-based Uno hosts the app through <c>NativeApplication</c>.</summary>
	[Application(Label = "Uno design host", LargeHeap = true, HardwareAccelerated = true)]
	public class MainApplication : Microsoft.UI.Xaml.NativeApplication
	{
		public MainApplication(IntPtr javaReference, JniHandleOwnership transfer)
			: base(() => new App(), javaReference, transfer)
		{
		}
	}
}
