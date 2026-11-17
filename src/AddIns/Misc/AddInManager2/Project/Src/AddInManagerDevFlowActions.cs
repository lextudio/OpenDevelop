#nullable enable
// Narrow DevFlow probes that open the AddIn Manager's real WPF window and options panel in the running
// application and report what they bound, so an integration test can tell a broken XAML or binding
// from a working one (the view-model tests cannot construct WPF views off Windows).

using System;
using System.Linq;
using System.Text.Json;
using ICSharpCode.AddInManager2.View;
using ICSharpCode.AddInManager2.ViewModel;
using LeXtudio.DevFlow.Agent.Core;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace ICSharpCode.AddInManager2
{
	[DevFlowUIThread]
	public static class AddInManagerDevFlowActions
	{
		static AddInManagerView? shown;

		[DevFlowAction("od.addin-manager.show", Description = "Show the real AddIn Manager window without blocking (Show, not ShowDialog) and report what it bound: each list's title, entry count, expansion and message. Replaces a window opened before. Close it with od.addin-manager.close.")]
		public static string Show()
		{
			Close();
			var view = AddInManagerView.Create();
			view.Show();
			shown = view;
			var viewModel = view.ViewModel;
			return JsonSerializer.Serialize(new {
				success = viewModel != null,
				loaded = view.IsLoaded,
				title = view.Title,
				lists = viewModel?.ViewModels.Select(v => new {
					type = v.GetType().Name,
					title = v.TitleWithHighlight,
					entries = v.AddInPackages.Count,
					expanded = v.IsExpandedInView,
					allowRefresh = v.AllowRefresh,
					hasError = v.HasError,
					message = v.ErrorMessage,
				}),
			});
		}

		[DevFlowAction("od.addin-manager.close", Description = "Close the window opened by od.addin-manager.show.")]
		public static string Close()
		{
			bool wasOpen = shown != null;
			if (shown != null) {
				shown.Close();
				shown.Dispose();
				shown = null;
			}
			return JsonSerializer.Serialize(new { success = true, wasOpen });
		}

		[DevFlowAction("od.addin-manager.options-panel", Description = "Construct the real Trusted repositories options panel exactly as the options dialog does, load it, and report the repositories it shows.")]
		public static string OptionsPanel()
		{
			var panel = new TrustedRepositoriesView();
			panel.LoadOptions();
			var viewModel = panel.DataContext as TrustedRepositoriesViewModel;
			return JsonSerializer.Serialize(new {
				success = viewModel != null,
				repositories = viewModel?.Repositories.ToList(),
			});
		}
	}
}
