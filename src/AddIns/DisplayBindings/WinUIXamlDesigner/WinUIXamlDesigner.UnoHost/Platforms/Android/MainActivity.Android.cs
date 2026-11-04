using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost.Droid
{
	/// <summary>
	/// The one activity. The IDE starts it with <c>am start --es port N --es token T</c> (see
	/// <c>AndroidEmulatorHostProcess</c>); the extras become the same <c>--port/--token</c> arguments the
	/// other heads receive on their command line.
	/// </summary>
	[Activity(MainLauncher = true, Exported = true, Theme = "@style/AppTheme",
		ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.UiMode)]
	public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
	{
		protected override void OnCreate(Bundle? savedInstanceState)
		{
			var port = Intent?.GetStringExtra("port");
			var token = Intent?.GetStringExtra("token");
			if (!string.IsNullOrEmpty(port) && !string.IsNullOrEmpty(token))
				App.LaunchArgs = new[] { "--port", port, "--token", token };
			base.OnCreate(savedInstanceState);
		}
	}
}
