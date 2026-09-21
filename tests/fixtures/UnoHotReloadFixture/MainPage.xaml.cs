using Microsoft.UI.Xaml.Controls;
using Microsoft.Maui.DevFlow.Agent.Core;
using System.Threading;

namespace UnoHotReloadFixture;

public sealed partial class MainPage : Page
{
    public MainPage()
	{
		InitializeComponent();
	}

	[DevFlowAction("uno-hot-reload.probe", Description = "Read the visible Hot Reload probe text from the running Uno page.")]
	public static string Probe()
	{
		var window = App.MainWindow;
		if (window == null)
			return "{\"error\":\"MainPage is not available\"}";
		string result = "{\"error\":\"UI dispatcher timeout\"}";
		using var complete = new ManualResetEventSlim();
		window.DispatcherQueue.TryEnqueue(() => {
			result = System.Text.Json.JsonSerializer.Serialize(new {
				text = FindProbe(window.Content)?.Text,
				processId = System.Environment.ProcessId,
				sessionToken = App.SessionToken
			});
			complete.Set();
		});
		return complete.Wait(System.TimeSpan.FromSeconds(5)) ? result : "{\"error\":\"UI dispatcher timeout\"}";
	}

	static TextBlock FindProbe(Microsoft.UI.Xaml.DependencyObject element)
	{
		if (element is TextBlock text && text.Name == "ProbeText") return text;
		for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++) {
			var found = FindProbe(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
			if (found != null) return found;
		}
		return null;
	}
}
