using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>One add-in of a trusted repository, as the AddIn Manager lists it.</summary>
	public sealed class AddInCatalogEntry
	{
		public AddInCatalogEntry(RepositoryReference repository, ReleaseListResult list, AddInRelease latest, ManagedAddIn installed, bool hasUpdate)
		{
			Repository = repository;
			List = list;
			Latest = latest;
			Installed = installed;
			HasUpdate = hasUpdate;
		}

		public RepositoryReference Repository { get; }

		/// <summary>How the repository's releases were obtained (status, message, skipped releases).</summary>
		public ReleaseListResult List { get; }

		/// <summary>The newest release the user may install (pre-releases only when they asked for them); null when there is none.</summary>
		public AddInRelease Latest { get; }

		/// <summary>The add-in installed from this repository, or null.</summary>
		public ManagedAddIn Installed { get; }

		public bool IsInstalled
		{
			get { return Installed != null; }
		}

		/// <summary>True when <see cref="Latest"/> is newer than the installed version.</summary>
		public bool HasUpdate { get; }

		public string Name
		{
			get { return Latest != null ? Latest.DisplayName : Repository.Name; }
		}
	}

	/// <summary>
	/// The AddIn Manager's online side: the repositories the user trusts, what they offer, what has an
	/// update, and downloading and installing a release. Replaces the NuGet gallery.
	/// </summary>
	public interface IAddInReleaseService
	{
		/// <summary>Raised after the trusted repositories were added to or removed from.</summary>
		event EventHandler TrustedRepositoriesChanged;

		/// <summary>Raised after <see cref="RefreshAsync"/> replaced <see cref="LastResults"/>.</summary>
		event EventHandler ReleasesRefreshed;

		IReadOnlyList<RepositoryReference> TrustedRepositories { get; }

		/// <summary>Trusts a repository (<c>owner/repo</c>); false with a message when it is not valid or already trusted.</summary>
		bool AddTrustedRepository(string text, out string error);

		void RemoveTrustedRepository(RepositoryReference repository);

		/// <summary>What the last <see cref="RefreshAsync"/> found, one entry per trusted repository that existed then.</summary>
		IReadOnlyList<ReleaseListResult> LastResults { get; }

		/// <summary>Reads the release list of every trusted repository (from the cache when it is recent, unless forced).</summary>
		Task RefreshAsync(bool forceRefresh, CancellationToken cancellationToken);

		/// <summary>The add-ins of the trusted repositories, from <see cref="LastResults"/> and what is installed now.</summary>
		IReadOnlyList<AddInCatalogEntry> GetEntries();

		/// <summary>The entries that have a newer release than the installed add-in.</summary>
		IReadOnlyList<AddInCatalogEntry> GetUpdates();

		/// <summary>
		/// Downloads <paramref name="release"/>, verifies it, checks it against the running IDE and installs it.
		/// A failure is reported through <see cref="IAddInManagerEvents.AddInOperationError"/> and returns null;
		/// only cancellation throws.
		/// </summary>
		Task<AddIn> InstallAsync(AddInRelease release, CancellationToken cancellationToken);
	}

	public sealed class AddInReleaseService : IAddInReleaseService
	{
		readonly IAddInReleaseSource source;
		readonly IAddInManagerSettings settings;
		readonly IAddInManagerEvents events;
		readonly IAddInSetup setup;
		readonly ISDAddInManagement management;
		readonly object gate = new object();
		List<RepositoryReference> trusted;
		IReadOnlyList<ReleaseListResult> lastResults = Array.Empty<ReleaseListResult>();

		public AddInReleaseService(
			IAddInReleaseSource source, IAddInManagerSettings settings, IAddInManagerEvents events,
			IAddInSetup setup, ISDAddInManagement management)
		{
			this.source = source ?? throw new ArgumentNullException(nameof(source));
			this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
			this.events = events ?? throw new ArgumentNullException(nameof(events));
			this.setup = setup ?? throw new ArgumentNullException(nameof(setup));
			this.management = management ?? throw new ArgumentNullException(nameof(management));
		}

		public event EventHandler TrustedRepositoriesChanged;
		public event EventHandler ReleasesRefreshed;

		public IReadOnlyList<RepositoryReference> TrustedRepositories
		{
			get
			{
				lock (gate)
					return EnsureLoaded().ToArray();
			}
		}

		List<RepositoryReference> EnsureLoaded()
		{
			if (trusted == null) {
				trusted = new List<RepositoryReference>();
				foreach (string entry in settings.TrustedRepositories ?? Array.Empty<string>()) {
					// A stored entry that is no longer valid is dropped rather than trusted.
					if (RepositoryReference.TryParse(entry, out RepositoryReference repository, out string error) && !trusted.Contains(repository))
						trusted.Add(repository);
				}
			}
			return trusted;
		}

		void Save()
		{
			settings.TrustedRepositories = trusted.Select(r => r.FullName).ToArray();
		}

		public bool AddTrustedRepository(string text, out string error)
		{
			if (!RepositoryReference.TryParse(text, out RepositoryReference repository, out error))
				return false;
			lock (gate) {
				List<RepositoryReference> list = EnsureLoaded();
				if (list.Contains(repository)) {
					error = repository.FullName + " is already in the list.";
					return false;
				}
				list.Add(repository);
				Save();
			}
			TrustedRepositoriesChanged?.Invoke(this, EventArgs.Empty);
			return true;
		}

		public void RemoveTrustedRepository(RepositoryReference repository)
		{
			if (repository == null)
				return;
			bool removed;
			lock (gate) {
				removed = EnsureLoaded().Remove(repository);
				if (removed)
					Save();
			}
			if (removed)
				TrustedRepositoriesChanged?.Invoke(this, EventArgs.Empty);
		}

		public IReadOnlyList<ReleaseListResult> LastResults
		{
			get
			{
				lock (gate)
					return lastResults;
			}
		}

		public async Task RefreshAsync(bool forceRefresh, CancellationToken cancellationToken)
		{
			IReadOnlyList<RepositoryReference> repositories = TrustedRepositories;
			// One repository after the other: the quota is 60 requests an hour and there is nothing to gain by bursting.
			// The awaits keep the caller's context (the UI thread), where ReleasesRefreshed and the install events belong.
			var results = new List<ReleaseListResult>();
			foreach (RepositoryReference repository in repositories) {
				cancellationToken.ThrowIfCancellationRequested();
				results.Add(await source.GetReleasesAsync(repository, forceRefresh, cancellationToken));
			}
			lock (gate)
				lastResults = results;
			ReleasesRefreshed?.Invoke(this, EventArgs.Empty);
		}

		public IReadOnlyList<AddInCatalogEntry> GetEntries()
		{
			bool includePrereleases = settings.ShowPrereleases;
			IReadOnlyList<ReleaseListResult> results;
			HashSet<RepositoryReference> stillTrusted;
			lock (gate) {
				results = lastResults;
				stillTrusted = new HashSet<RepositoryReference>(EnsureLoaded());
			}
			List<ManagedAddIn> installed = setup.AddInsWithMarkedForInstallation.ToList();
			var entries = new List<AddInCatalogEntry>();
			foreach (ReleaseListResult result in results) {
				if (!stillTrusted.Contains(result.Repository))
					continue; // removed since the last refresh
				AddInRelease latest = ReleaseVersioning.Latest(result.Releases, includePrereleases);
				ManagedAddIn managed = installed.FirstOrDefault(m => result.Repository.Equals(m.InstalledFromRepository));
				bool hasUpdate = managed != null && ReleaseVersioning.IsNewer(managed.AddIn.Version, latest);
				entries.Add(new AddInCatalogEntry(result.Repository, result, latest, managed, hasUpdate));
			}
			return entries;
		}

		public IReadOnlyList<AddInCatalogEntry> GetUpdates()
		{
			return GetEntries().Where(e => e.HasUpdate).ToList();
		}

		public async Task<AddIn> InstallAsync(AddInRelease release, CancellationToken cancellationToken)
		{
			if (release == null)
				throw new ArgumentNullException(nameof(release));
			events.OnOperationStarted();
			string workDirectory = Path.Combine(Path.GetTempPath(), "od-addin-" + Guid.NewGuid().ToString("N"));
			try {
				Directory.CreateDirectory(workDirectory);
				string file = Path.Combine(workDirectory, Path.GetFileName(release.AssetName));
				await source.DownloadAsync(release, file, cancellationToken);

				string problem = CheckDownloadedPackage(file, release);
				if (problem != null) {
					Fail(release, problem);
					return null;
				}
				// Setup reports its own failures through the error event and returns null.
				return setup.InstallAddIn(file, release);
			} catch (OperationCanceledException) {
				throw;
			} catch (Exception ex) when (ex is HttpRequestException || ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException) {
				Fail(release, "Downloading " + release.Repository + " " + release.Tag + " failed: " + ex.Message);
				return null;
			} finally {
				TryDeleteDirectory(workDirectory);
			}
		}

		/// <summary>Null when the package may be installed; otherwise why not.</summary>
		string CheckDownloadedPackage(string file, AddInRelease release)
		{
			AddIn candidate;
			try {
				using (var reader = new StringReader(OdAddinPackage.ReadManifestText(file)))
					candidate = management.Load(reader);
			} catch (Exception ex) when (ex is InvalidDataException || ex is AddInLoadException || ex is System.Xml.XmlException) {
				return release.Repository + " " + release.Tag + " is not a valid add-in package: " + ex.Message;
			}
			if (candidate == null || candidate.Manifest == null || string.IsNullOrEmpty(candidate.Manifest.PrimaryIdentity))
				return release.Repository + " " + release.Tag + " has no add-in identity in its manifest.";
			if (ReleaseVersioning.Compare(candidate.Version, release.Version) != 0)
				return "The package of " + release.Repository + " " + release.Tag + " says it is version " + candidate.Version
					+ ", which does not match its release tag.";
			IReadOnlyList<string> problems = AddInCompatibility.Problems(candidate, management.AddIns);
			if (problems.Count > 0)
				return release.DisplayName + " " + release.Tag + " cannot be installed. " + string.Join(" ", problems);
			return null;
		}

		void Fail(AddInRelease release, string message)
		{
			// The message is shown through StringParser, which expands ${...}; text that came from a server
			// or a package must not be able to name a property or resource.
			events.OnAddInOperationError(new AddInOperationErrorEventArgs(message.Replace("${", "$ {", StringComparison.Ordinal)));
		}

		static void TryDeleteDirectory(string directory)
		{
			try {
				if (Directory.Exists(directory))
					Directory.Delete(directory, true);
			} catch (IOException) {
			} catch (UnauthorizedAccessException) {
			}
		}
	}
}
