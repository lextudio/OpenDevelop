using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.AddInManager2.Model;
using ICSharpCode.SharpDevelop;
using Microsoft.Win32;

namespace ICSharpCode.AddInManager2.ViewModel
{
	/// <summary>The add-ins that are installed, with the ones marked for installation or removal.</summary>
	public class InstalledAddInsViewModel : AddInsViewModelBase
	{
		public InstalledAddInsViewModel()
			: base()
		{
			Initialize();
		}
		
		public InstalledAddInsViewModel(IAddInManagerServices services)
			: base(services)
		{
			Initialize();
		}
		
		private void Initialize()
		{
			IsSearchable = true;
			AllowInstallFromArchive = true;
			HasFilterForPreinstalled = true;
			Title = SD.ResourceService.GetString("AddInManager2.Views.Installed");
			ShowPreinstalledAddIns = AddInManager.Settings.ShowPreinstalledAddIns;
			AddInManager.Events.AddInInstalled += InstalledAddInsChanged;
			AddInManager.Events.AddInUninstalled += InstalledAddInsChanged;
			AddInManager.Events.AddInStateChanged += InstalledAddInStateChanged;
		}
		
		protected override void OnDispose()
		{
			AddInManager.Events.AddInInstalled -= InstalledAddInsChanged;
			AddInManager.Events.AddInUninstalled -= InstalledAddInsChanged;
			AddInManager.Events.AddInStateChanged -= InstalledAddInStateChanged;
		}
		
		public override void ReadPackages()
		{
			var addIns = new List<ManagedAddIn>(AddInManager.Setup.AddInsWithMarkedForInstallation);
			addIns.Sort((a, b) => String.Compare(a.AddIn.Name, b.AddIn.Name, StringComparison.CurrentCultureIgnoreCase));
			var shown = new List<AddInPackageViewModelBase>();
			foreach (ManagedAddIn addIn in addIns)
			{
				if (String.Equals(addIn.AddIn.Properties["addInManagerHidden"], "true", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (!ShowPreinstalledAddIns && AddInManager.Setup.IsAddInPreinstalled(addIn.AddIn))
				{
					continue;
				}
				if (!MatchesSearch(addIn.AddIn.Name, addIn.AddIn.Manifest.PrimaryIdentity, addIn.AddIn.Properties["description"]))
				{
					continue;
				}
				shown.Add(new OfflineAddInsViewModel(AddInManager, addIn));
			}
			UpdatePackageViewModels(shown);
			OnPropertyChanged(null);
		}
		
		protected override void UpdatePreinstalledFilter()
		{
			AddInManager.Settings.ShowPreinstalledAddIns = ShowPreinstalledAddIns;
			ReadPackages();
		}
		
		private void InstalledAddInsChanged(object sender, AddInInstallationEventArgs e)
		{
			ReadPackages();
		}
		
		private void InstalledAddInStateChanged(object sender, AddInInstallationEventArgs e)
		{
			UpdateInstallationState();
		}
		
		protected override void InstallFromArchive()
		{
			AddInManager.Events.OnOperationStarted();
			
			OpenFileDialog dlg = new OpenFileDialog();
			dlg.Filter = SD.ResourceService.GetString("AddInManager2.SDAddInFileFilter");
			dlg.Multiselect = true;
			var showDialogResult = dlg.ShowDialog();
			if (showDialogResult.HasValue && showDialogResult.Value)
			{
				foreach (var file in dlg.FileNames)
				{
					AddInManager.Setup.InstallAddIn(file);
				}
			}
		}
	}
}
