using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.AddInManager2.ViewModel
{
	/// <summary>
	/// A list built from the release lists of the trusted GitHub repositories (Available and Updates).
	/// The list is rebuilt synchronously from what the release service knows; reaching GitHub is a
	/// separate, explicit <see cref="RefreshAsync"/>, so showing the window never waits for the network.
	/// </summary>
	public abstract class ReleaseAddInsViewModelBase : AddInsViewModelBase
	{
		protected ReleaseAddInsViewModelBase()
			: base()
		{
			Initialize();
		}

		protected ReleaseAddInsViewModelBase(IAddInManagerServices services)
			: base(services)
		{
			Initialize();
		}

		private void Initialize()
		{
			IsSearchable = true;
			AllowRefresh = true;
			HasFilterForPrereleases = true;
			ShowPrereleases = AddInManager.Settings.ShowPrereleases;
			AddInManager.Releases.ReleasesRefreshed += Releases_Changed;
			AddInManager.Releases.TrustedRepositoriesChanged += Releases_TrustedRepositoriesChanged;
			AddInManager.Events.AddInInstalled += AddIn_StateChanged;
			AddInManager.Events.AddInUninstalled += AddIn_StateChanged;
			AddInManager.Events.AddInStateChanged += AddIn_StateChanged;
		}

		protected override void OnDispose()
		{
			AddInManager.Releases.ReleasesRefreshed -= Releases_Changed;
			AddInManager.Releases.TrustedRepositoriesChanged -= Releases_TrustedRepositoriesChanged;
			AddInManager.Events.AddInInstalled -= AddIn_StateChanged;
			AddInManager.Events.AddInUninstalled -= AddIn_StateChanged;
			AddInManager.Events.AddInStateChanged -= AddIn_StateChanged;
		}

		/// <summary>The entries this list shows, from all entries of the trusted repositories.</summary>
		protected abstract IEnumerable<AddInCatalogEntry> SelectEntries(IReadOnlyList<AddInCatalogEntry> all);

		/// <summary>The number shown in the list's title (for example the number of updates).</summary>
		protected virtual int CountHighlight(IReadOnlyList<AddInCatalogEntry> all)
		{
			return 0;
		}

		public override void ReadPackages()
		{
			IReadOnlyList<AddInCatalogEntry> all = AddInManager.Releases.GetEntries();
			var shown = SelectEntries(all)
				.Where(e => MatchesSearch(e.Name, e.Repository.FullName))
				.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
				.Select(e => (AddInPackageViewModelBase)new GitHubReleaseViewModel(AddInManager, e))
				.ToList();
			UpdatePackageViewModels(shown);
			HighlightCount = CountHighlight(all);
			SetMessage(DescribeProblems(all));
			OnPropertyChanged(null);
		}

		/// <summary>What the user should know about the release lists: no repositories, rate limit, unreachable GitHub, repositories without releases.</summary>
		private string DescribeProblems(IReadOnlyList<AddInCatalogEntry> all)
		{
			if (AddInManager.Releases.TrustedRepositories.Count == 0)
			{
				return SD.ResourceService.GetString("AddInManager2.AddInsView.NoTrustedRepositories");
			}
			var lines = new List<string>();
			foreach (AddInCatalogEntry entry in all)
			{
				ReleaseListResult list = entry.List;
				if ((list.Status == ReleaseListStatus.Failed) || (list.Status == ReleaseListStatus.RateLimited) || (list.Status == ReleaseListStatus.Stale))
				{
					lines.Add(entry.Repository.FullName + ": " + list.Message);
				}
				else if ((list.Releases.Count == 0) && list.Skipped.Count > 0)
				{
					lines.Add(entry.Repository.FullName + ": " + String.Join("; ", list.Skipped.Take(3)));
				}
			}
			return String.Join(Environment.NewLine, lines);
		}

		/// <summary>Reads the release lists of all trusted repositories (from the cache when it is recent, unless <paramref name="force"/>).</summary>
		public async Task RefreshAsync(bool force)
		{
			IsReadingPackages = true;
			try
			{
				// Awaiting here returns to the UI thread, where ReleasesRefreshed rebuilds the lists.
				await AddInManager.Releases.RefreshAsync(force, CancellationToken.None);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				LoggingService.Warn("Reading the add-in release lists failed: " + ex.Message);
				SetMessage(ex.Message);
			}
			finally
			{
				IsReadingPackages = false;
			}
		}

		protected override void Refresh()
		{
			_ = RefreshAsync(true);
		}

		protected override void UpdatePrereleaseFilter()
		{
			AddInManager.Settings.ShowPrereleases = ShowPrereleases;
			ReadPackages();
		}

		private void Releases_Changed(object sender, EventArgs e)
		{
			if (!IsDisposed)
			{
				ReadPackages();
			}
		}

		private void Releases_TrustedRepositoriesChanged(object sender, EventArgs e)
		{
			if (!IsDisposed)
			{
				// A repository was added or removed: show that at once, then ask GitHub for the new one.
				ReadPackages();
				_ = RefreshAsync(false);
			}
		}

		private void AddIn_StateChanged(object sender, AddInInstallationEventArgs e)
		{
			if (!IsDisposed)
			{
				ReadPackages();
			}
		}
	}
}
