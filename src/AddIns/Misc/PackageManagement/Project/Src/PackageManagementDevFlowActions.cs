// DevFlow actions used by tests/OpenDevelop.IntegrationTests to drive the real "Manage NuGet
// Packages" dialog (ManagePackagesView) end-to-end: set the active package source to a local,
// offline feed, open the dialog, type into the real search box, trigger the real SearchCommand,
// and install via the real per-row AddPackageCommand - the same bindings the UI itself uses. See
// doc/technotes/integration-testing.md for the DevFlow action pattern.

using System;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;

using ICSharpCode.SharpDevelop;
using LeXtudio.DevFlow.Agent.Core;
using NuGet;

namespace ICSharpCode.PackageManagement
{
	[DevFlowUIThread]
	public static class PackageManagementDevFlowActions
	{
		static ManagePackagesView currentView;

		[DevFlowAction("od.nuget.set-local-feed", Description = "Add (and activate) a local folder as the only NuGet package source, so package search/install doesn't need network access")]
		public static string SetLocalFeed(string path)
		{
			var repositories = PackageManagementServices.RegisteredPackageRepositories;
			var source = repositories.PackageSources.FirstOrDefault(s => s.Source == path)
				?? new PackageSource(path, "LocalTestFeed");
			if (!repositories.PackageSources.Contains(source))
				repositories.PackageSources.Add(source);
			repositories.ActivePackageSource = source;

			return JsonSerializer.Serialize(new { success = true, source = source.Source, name = source.Name });
		}

		[DevFlowAction("od.nuget.open-dialog", Description = "Open the real Manage NuGet Packages dialog (non-modally, so the DevFlow action returns immediately instead of blocking on ShowDialog())")]
		public static string OpenDialog()
		{
			currentView?.Close();
			currentView = new ManagePackagesView { Owner = SD.Workbench.MainWindow };
			// ShowDialog() would block this UI-thread-marshaled call until the window closes;
			// Show() keeps the dialog non-modal so later od.nuget.* calls can keep driving it.
			currentView.Show();
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.nuget.close-dialog", Description = "Close the Manage NuGet Packages dialog opened by od.nuget.open-dialog")]
		public static string CloseDialog()
		{
			currentView?.Close();
			currentView = null;
			return JsonSerializer.Serialize(new { success = true });
		}

		static ManagePackagesViewModel CurrentViewModel()
		{
			return currentView?.DataContext as ManagePackagesViewModel;
		}

		[DevFlowAction("od.nuget.set-search-text", Description = "Set the search box text in the currently open Manage NuGet Packages dialog's Available tab")]
		public static string SetSearchText(string text)
		{
			var vm = CurrentViewModel();
			if (vm == null)
				return JsonSerializer.Serialize(new { success = false, error = "No Manage NuGet Packages dialog is open (call od.nuget.open-dialog first)" });

			vm.AvailablePackagesViewModel.SearchTerms = text;
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.nuget.search", Description = "Execute the real SearchCommand on the Available tab (same command the Enter key/magnifier icon invoke)")]
		public static string Search()
		{
			var vm = CurrentViewModel();
			if (vm == null)
				return JsonSerializer.Serialize(new { success = false, error = "No Manage NuGet Packages dialog is open (call od.nuget.open-dialog first)" });

			vm.AvailablePackagesViewModel.SearchCommand.Execute(null);
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.nuget.status", Description = "Inspect the Available tab's current state: whether it's still searching, any error, and the result rows (Id/Version/IsAdded) as bound to the real PackageViewModels shown in the list")]
		public static string Status()
		{
			var vm = CurrentViewModel();
			if (vm == null)
				return JsonSerializer.Serialize(new { open = false });

			var available = vm.AvailablePackagesViewModel;
			return JsonSerializer.Serialize(new {
				open = true,
				isReadingPackages = available.IsReadingPackages,
				hasError = available.HasError,
				errorMessage = available.ErrorMessage,
				packages = available.PackageViewModels.Select(p => new {
					id = p.Id,
					version = p.Version?.ToString(),
					isAdded = p.IsAdded
				}).ToArray()
			});
		}

		[DevFlowAction("od.nuget.install", Description = "Execute the real per-row AddPackageCommand for the package with the given Id in the Available tab's results - the same command the row's Add button is bound to")]
		public static string Install(string packageId)
		{
			var vm = CurrentViewModel();
			if (vm == null)
				return JsonSerializer.Serialize(new { success = false, error = "No Manage NuGet Packages dialog is open (call od.nuget.open-dialog first)" });

			var package = vm.AvailablePackagesViewModel.PackageViewModels.FirstOrDefault(p => p.Id == packageId);
			if (package == null)
				return JsonSerializer.Serialize(new { success = false, error = $"No package '{packageId}' in the current search results" });
			if (!package.AddPackageCommand.CanExecute(null))
				return JsonSerializer.Serialize(new { success = false, error = $"AddPackageCommand.CanExecute returned false for '{packageId}' (already added/managed?)" });

			package.AddPackageCommand.Execute(null);
			return JsonSerializer.Serialize(new { success = true });
		}
	}
}
