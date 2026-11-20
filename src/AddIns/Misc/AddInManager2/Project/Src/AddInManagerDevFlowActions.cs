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

		static object Describe(AddInsViewModelBase list) => new {
			type = list.GetType().Name,
			title = list.TitleWithHighlight,
			reading = list.IsReadingPackages,
			hasError = list.HasError,
			message = list.ErrorMessage,
			entries = list.AddInPackages.Select(p => new {
				name = p.Name,
				id = p.Id,
				version = p.Version?.ToString(),
				oldVersion = p.OldVersion?.ToString(),
				installable = p.IsInstallable,
				installed = p.IsInstalled,
				update = p.IsUpdate,
				preinstalled = p.IsPreinstalled,
				uninstallable = p.IsUninstallable,
				uninstall = p.RemovePackageCommand.CanExecute(null),
			}).ToList(),
		};

		[DevFlowAction("od.addin-manager.state", Description = "Report every list of the window opened by od.addin-manager.show: reading flag, message and each entry's name, version, and installable/installed/update flags.")]
		public static string State()
		{
			var viewModel = shown?.ViewModel;
			return JsonSerializer.Serialize(new {
				success = viewModel != null,
				lists = viewModel?.ViewModels.Select(Describe).ToList(),
			});
		}

		[DevFlowAction("od.addin-manager.show-preinstalled", Description = "Tick or untick the Installed list's 'show preinstalled add-ins' filter, through the same command the checkbox runs, and report the names it lists afterwards.")]
		public static string ShowPreinstalled(bool show)
		{
			var installed = shown?.ViewModel?.InstalledAddInsViewModel;
			if (installed == null)
				return JsonSerializer.Serialize(new { success = false });
			installed.ShowPreinstalledAddIns = show;
			installed.UpdatePreinstalledFilterCommand.Execute(null);
			return JsonSerializer.Serialize(new { success = true, show, entries = installed.AddInPackages.Select(p => p.Name).ToList() });
		}

		[DevFlowAction("od.addin-manager.trust", Description = "Trust a repository the way the options panel does: type owner/repo into the real Trusted repositories panel, press Add, then save.")]
		public static string Trust(string repository)
		{
			var panel = new TrustedRepositoriesView();
			panel.LoadOptions();
			var viewModel = panel.DataContext as TrustedRepositoriesViewModel;
			if (viewModel == null)
				return JsonSerializer.Serialize(new { success = false });
			viewModel.NewRepository = repository;
			viewModel.AddCommand.Execute(null);
			panel.SaveOptions();
			return JsonSerializer.Serialize(new { success = viewModel.Repositories.Contains(repository), repositories = viewModel.Repositories.ToList(), message = viewModel.Message });
		}

		[DevFlowAction("od.addin-manager.refresh", Description = "Press Refresh on the Available list of the window opened by od.addin-manager.show (asks GitHub again); poll od.addin-manager.state until reading is false.")]
		public static string Refresh()
		{
			var viewModel = shown?.ViewModel;
			viewModel?.AvailableAddInsViewModel.RefreshCommand.Execute(null);
			return JsonSerializer.Serialize(new { success = viewModel != null });
		}

		[DevFlowAction("od.addin-manager.install", Description = "Press Install (or Update) on the entry with this name in the named list (Installed, Available or Updated) of the window opened by od.addin-manager.show.")]
		public static string Install(string list, string name)
		{
			var viewModel = shown?.ViewModel;
			AddInsViewModelBase target = list switch {
				"Available" => viewModel?.AvailableAddInsViewModel,
				"Updated" => viewModel?.UpdatedAddInsViewModel,
				_ => viewModel?.InstalledAddInsViewModel,
			};
			var entry = target?.AddInPackages.FirstOrDefault(p => p.Name == name);
			if (entry == null)
				return JsonSerializer.Serialize(new { success = false, reason = "no such entry" });
			if (entry.IsUpdate)
				entry.UpdatePackage();
			else
				entry.AddPackage();
			return JsonSerializer.Serialize(new { success = true });
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
