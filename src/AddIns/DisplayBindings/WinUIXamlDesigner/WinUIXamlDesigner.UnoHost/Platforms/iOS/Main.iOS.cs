using ICSharpCode.WinUIXamlDesigner.UnoHost;
using Uno.UI.Hosting;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost.iOS
{
	/// <summary>iOS entry point: the IDE launches the app with <c>--port N --token T</c> (simctl). Skia-based
	/// Uno hosts the app through the platform host builder rather than as the UIApplication delegate.</summary>
	public class EntryPoint
	{
		public static void Main(string[] args)
		{
			// The managed args are empty under the Skia host: the real process arguments (simctl's
			// "--port N --token T") are only in NSProcessInfo.
			App.LaunchArgs = Foundation.NSProcessInfo.ProcessInfo.Arguments;
			var host = UnoPlatformHostBuilder.Create()
				.App(() => new App())
				.UseAppleUIKit()
				.Build();
			host.Run();
		}
	}
}
