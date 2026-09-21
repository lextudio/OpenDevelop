using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI;

namespace UnoHotReloadFixture;

public partial class App : Application
{
    internal static Window MainWindow { get; private set; }
    internal static readonly string SessionToken = Guid.NewGuid().ToString("N");
    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new Window();
        MainWindow = window;
#if DEBUG
        window.UseStudio();
#endif
        var frame = new Frame();
        window.Content = frame;
        frame.Navigate(typeof(MainPage), args.Arguments);
        window.Activate();
		var probePortFile = Path.Combine(AppContext.BaseDirectory, "od-hot-reload-probe-port.txt");
		var configuredProbePort = Environment.GetEnvironmentVariable("OD_UNO_HOT_RELOAD_DEVFLOW_PORT")
			?? (File.Exists(probePortFile) ? File.ReadAllText(probePortFile).Trim() : null);
		if (int.TryParse(configuredProbePort, out var port)
			&& port is > 0 and <= 65535)
		{
			new LeXtudio.DevFlow.Agent.Uno.UnoAgentService(
				new Microsoft.Maui.DevFlow.Agent.Core.AgentOptions { Port = port }).Start();
		}
    }
}
