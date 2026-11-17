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
	/// One add-in of a trusted GitHub repository in the Available and Updates lists. Installed state is
	/// derived from the repository recorded in the installed add-in's manifest, never from its name.
	/// </summary>
	public class GitHubReleaseViewModel : AddInPackageViewModelBase
	{
		private readonly AddInCatalogEntry _entry;
		private ManagedAddIn _managed;
		private bool _isInstalling;

		public GitHubReleaseViewModel(IAddInManagerServices services, AddInCatalogEntry entry)
			: base(services)
		{
			_entry = entry ?? throw new ArgumentNullException(nameof(entry));
			_managed = FindManaged();
		}

		public AddInCatalogEntry Entry
		{
			get
			{
				return _entry;
			}
		}

		private ManagedAddIn FindManaged()
		{
			return AddInManager.Setup.AddInsWithMarkedForInstallation
				.FirstOrDefault(m => _entry.Repository.Equals(m.InstalledFromRepository));
		}

		public override void UpdateInstallationState()
		{
			// One scan of the add-ins per state change, not one per bound property.
			_managed = FindManaged();
			base.UpdateInstallationState();
			OnPropertyChanged(m => m.Version);
			OnPropertyChanged(m => m.OldVersion);
			OnPropertyChanged(m => m.ShowSplittedVersions);
		}

		private AddInRelease Latest
		{
			get
			{
				return _entry.Latest;
			}
		}

		public override string Name
		{
			get
			{
				return _entry.Name;
			}
		}

		public override string Id
		{
			get
			{
				return _entry.Repository.FullName;
			}
		}

		public override Uri LicenseUrl
		{
			get
			{
				return null;
			}
		}

		public override Uri ProjectUrl
		{
			get
			{
				return new Uri("https://github.com/" + _entry.Repository.FullName);
			}
		}

		public override Uri ReportAbuseUrl
		{
			get
			{
				return null;
			}
		}

		public override bool IsOffline
		{
			get
			{
				return false;
			}
		}

		public override bool IsPreinstalled
		{
			get
			{
				return false;
			}
		}

		public override bool IsEnabled
		{
			get
			{
				return true;
			}
		}

		public override bool IsAdded
		{
			get
			{
				return (_managed != null)
					&& ((_managed.AddIn.Action == AddInAction.Install) || (_managed.AddIn.Action == AddInAction.Update));
			}
		}

		public override bool IsInstalled
		{
			get
			{
				return (_managed != null) && AddInManager.Setup.IsAddInInstalled(_managed.AddIn);
			}
		}

		public override bool IsUpdate
		{
			get
			{
				return IsInstalled && ReleaseVersioning.IsNewer(_managed.AddIn.Version, Latest);
			}
		}

		public override bool IsInstallable
		{
			get
			{
				return (Latest != null) && !_isInstalling;
			}
		}

		public override bool IsUninstallable
		{
			get
			{
				return IsInstalled;
			}
		}

		public override bool IsDisablingPossible
		{
			get
			{
				return false;
			}
		}

		public override bool IsRemoved
		{
			get
			{
				return (_managed != null) && (_managed.AddIn.Action == AddInAction.Uninstall);
			}
		}

		public override bool HasDependencyConflicts
		{
			get
			{
				return false;
			}
		}

		public override IEnumerable<AddInDependency> Dependencies
		{
			get
			{
				return null;
			}
		}

		public override IEnumerable<string> Authors
		{
			get
			{
				return new[] { _entry.Repository.Owner };
			}
		}

		public override bool HasDownloadCount
		{
			get
			{
				return false;
			}
		}

		public override int DownloadCount
		{
			get
			{
				return 0;
			}
		}

		public override Uri IconUrl
		{
			get
			{
				return null;
			}
		}

		public override Version Version
		{
			get
			{
				return (Latest != null) ? Latest.Version : null;
			}
		}

		public override Version OldVersion
		{
			get
			{
				return IsUpdate ? _managed.AddIn.Version : null;
			}
		}

		public override bool ShowSplittedVersions
		{
			get
			{
				return IsUpdate;
			}
		}

		public override string Description
		{
			get
			{
				return (Latest != null) ? Latest.Notes : _entry.List.Message;
			}
		}

		public override DateTime? LastUpdated
		{
			get
			{
				return (Latest != null) && Latest.PublishedAt.HasValue ? Latest.PublishedAt.Value.UtcDateTime : (DateTime?)null;
			}
		}

		public override string Summary
		{
			get
			{
				if (IsAdded)
				{
					if (IsUpdate)
					{
						return SD.ResourceService.GetString("AddInManager.AddInUpdated");
					}
					return SurroundWithParantheses(SD.ResourceService.GetString("AddInManager.AddInInstalled"));
				}
				if (IsRemoved)
				{
					return SurroundWithParantheses(SD.ResourceService.GetString("AddInManager.AddInRemoved"));
				}
				if (Latest == null)
				{
					return String.IsNullOrEmpty(_entry.List.Message)
						? _entry.Repository.FullName
						: _entry.List.Message;
				}
				return _entry.Repository.FullName + " - " + Latest.Tag;
			}
		}

		public override void AddPackage()
		{
			StartInstallation();
		}

		public override void UpdatePackage()
		{
			StartInstallation();
		}

		private void StartInstallation()
		{
			_ = InstallAsync();
		}

		private async Task InstallAsync()
		{
			AddInRelease release = Latest;
			if ((release == null) || _isInstalling)
			{
				return;
			}
			_isInstalling = true;
			UpdateInstallationState();
			try
			{
				// Failures reach the user through the AddInOperationError event; this only guards the unexpected.
				await AddInManager.Releases.InstallAsync(release, CancellationToken.None);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				LoggingService.Error("Error when trying to install " + release, ex);
				AddInManager.Events.OnAddInOperationError(new AddInOperationErrorEventArgs(AddInMessage.Neutralize(ex.Message)));
			}
			finally
			{
				_isInstalling = false;
				UpdateInstallationState();
			}
		}

		public override void RemovePackage()
		{
			AddInManager.Events.OnOperationStarted();
			if (_managed != null)
			{
				AddInManager.Setup.UninstallAddIn(_managed.AddIn);
			}
		}

		public override void CancelInstallation()
		{
			AddInManager.Events.OnOperationStarted();
			if (_managed != null)
			{
				AddInManager.Setup.CancelInstallation(_managed.AddIn);
			}
		}

		public override void CancelUpdate()
		{
			AddInManager.Events.OnOperationStarted();
			if (_managed != null)
			{
				AddInManager.Setup.CancelUpdate(_managed.AddIn);
			}
		}

		public override void CancelUninstallation()
		{
			AddInManager.Events.OnOperationStarted();
			if (_managed != null)
			{
				AddInManager.Setup.CancelUninstallation(_managed.AddIn);
			}
		}
	}
}
