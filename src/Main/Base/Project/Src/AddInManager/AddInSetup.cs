// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.AddInManager2.Model
{
	/// <summary>
	/// Helper class for AddIn setup operations.
	/// </summary>
	public class AddInSetup : IAddInSetup
	{
		private IAddInManagerEvents _events = null;
		private ISDAddInManagement _sdAddInManagement = null;
		
		private List<ManagedAddIn> _addInsMarkedForInstall;
		
		public AddInSetup(IAddInManagerEvents events, ISDAddInManagement sdAddInManagement)
		{
			_events = events;
			_sdAddInManagement = sdAddInManagement;
			
			_addInsMarkedForInstall = new List<ManagedAddIn>();
			
		}
		
		public IEnumerable<ManagedAddIn> AddInsWithMarkedForInstallation
		{
			get
			{
				return _sdAddInManagement.AddIns.Select(a => new ManagedAddIn(a) { IsTemporary = false, IsUpdate = false })
					.GroupJoin(
						_addInsMarkedForInstall,
						installedAddIn => installedAddIn.AddIn.Manifest.PrimaryIdentity,
						markedAddIn => markedAddIn.AddIn.Manifest.PrimaryIdentity,
						(installedAddIn, e) => e.ElementAtOrDefault(0) ?? installedAddIn);
			}
		}

		/// <summary>
		/// Reads the manifest of a <c>.odaddin</c> package without unpacking it. Fires the usual error event
		/// and returns null when the package is not a valid add-in package.
		/// </summary>
		private AddIn LoadAddInFromPackage(string packageFile)
		{
			try
			{
				string manifestText = OdAddinPackage.ReadManifestText(packageFile);
				using (var reader = new StringReader(manifestText))
				{
					return _sdAddInManagement.Load(reader);
				}
			}
			catch (Exception ex) when (ex is InvalidDataException || ex is AddInLoadException || ex is System.Xml.XmlException)
			{
				SD.Log.WarnFormatted("[AddInManager2] Cannot read package {0}: {1}", packageFile, ex.Message);
				_events.OnAddInOperationError(
					new AddInOperationErrorEventArgs(
						SD.ResourceService.GetString("AddInManager2.InvalidPackage")));
				return null;
			}
		}
		
		public AddIn InstallAddIn(string fileName)
		{
			return InstallAddIn(fileName, null);
		}

		/// <summary>
		/// Installs from a file. <paramref name="source"/> names the GitHub release a <c>.odaddin</c> was downloaded
		/// from (recorded in the installed manifest so updates can be found), or is null for a local file.
		/// </summary>
		public AddIn InstallAddIn(string fileName, AddInRelease source)
		{
			if (fileName != null)
			{
				AddIn addIn = null;
				
				bool installAsExternal = false;
				
				switch (Path.GetExtension(fileName).ToLowerInvariant())
				{
					case ".addin":
						
						SD.Log.DebugFormatted("[AddInManager2] Loading {0} as manifest.", fileName);
						
						if (FileUtility.IsBaseDirectory(FileUtility.ApplicationRootPath, fileName))
						{
							// Don't allow to install AddIns from application root path
							_events.OnAddInOperationError(
								new AddInOperationErrorEventArgs(
									SD.ResourceService.GetString("AddInManager.CannotInstallIntoApplicationDirectory")));
							return null;
						}
						
						// Load directly from location
						addIn = _sdAddInManagement.Load(fileName);
						installAsExternal = true;
						
						break;
						
					case ".odaddin":
						SD.Log.DebugFormatted("[AddInManager2] Trying to load {0} as local AddIn package.", fileName);
						addIn = LoadAddInFromPackage(fileName);
						if (addIn == null)
						{
							return null;
						}
						break;

					default:
						// Unknown format of file
						_events.OnAddInOperationError(
							new AddInOperationErrorEventArgs(
								SD.ResourceService.GetString("AddInManager.UnknownFileFormat") + " " + Path.GetExtension(fileName)));
						return null;
				}
				
				if (addIn != null)
				{
					if ((addIn.Manifest == null) || (addIn.Manifest.PrimaryIdentity == null))
					{
						_events.OnAddInOperationError(
							new AddInOperationErrorEventArgs(
								new AddInLoadException(SD.ResourceService.GetString("AddInManager.AddInMustHaveIdentity"))));
						return null;
					}

					// Try to find this AddIn in current registry
					string identity = addIn.Manifest.PrimaryIdentity;
					AddIn foundAddIn = null;
					foreach (AddIn treeAddIn in _sdAddInManagement.AddIns)
					{
						if (treeAddIn.Manifest.Identities.ContainsKey(identity))
						{
							foundAddIn = treeAddIn;
							break;
						}
					}

					// Prevent this AddIn from being uninstalled, if marked for deinstallation
					foreach (string installedIdentity in addIn.Manifest.Identities.Keys)
					{
						_sdAddInManagement.AbortRemoveUserAddInOnNextStart(installedIdentity);
					}
					
					if (!installAsExternal)
					{
						// The identity becomes a directory name (created, and deleted again on update), so it
						// must not be able to name anything else.
						if (!OdAddinPackage.IsSafeIdentity(identity))
						{
							SD.Log.WarnFormatted("[AddInManager2] Refusing package {0}: identity '{1}' cannot be used as a folder name.", fileName, identity);
							_events.OnAddInOperationError(
								new AddInOperationErrorEventArgs(
									SD.ResourceService.GetString("AddInManager2.InvalidPackage")));
							return null;
						}
						// Create target directory for AddIn in user profile & copy package contents there
						if (!CopyAddInFromZip(addIn, fileName, source))
						{
							_events.OnAddInOperationError(
								new AddInOperationErrorEventArgs(
									SD.ResourceService.GetString("AddInManager2.InvalidPackage")));
							return null;
						}
						
						// Install the AddIn using manifest
						if (foundAddIn != null)
						{
							addIn.Action = AddInAction.Update;
						}
						else
						{
							addIn.Action = AddInAction.Install;
							_sdAddInManagement.AddToTree(addIn);
						}
					}
					else
					{
						// Only add a reference to an external manifest
						_sdAddInManagement.AddExternalAddIns(new AddIn[] { addIn });
					}
					
					// Mark this AddIn
					ManagedAddIn markedAddIn = new ManagedAddIn(addIn)
					{
						IsTemporary = true,
						IsUpdate = (foundAddIn != null),
						OldVersion = (foundAddIn != null) ? foundAddIn.Version : null
					};
					_addInsMarkedForInstall.Add(markedAddIn);
					
					// Successful installation
					AddInInstallationEventArgs eventArgs = new AddInInstallationEventArgs(addIn);
					_events.OnAddInInstalled(eventArgs);
					_events.OnAddInStateChanged(eventArgs);
					
					return addIn;
				}
			}

			// In successful cases we should have exited somewhere else, in error cases the error event was already fired.
			return null;
		}
		
		private bool CopyAddInFromZip(AddIn addIn, string zipFile, AddInRelease source)
		{
			if ((addIn == null) || (addIn.Manifest == null))
			{
				return false;
			}
			string targetDir = Path.Combine(_sdAddInManagement.TempInstallDirectory,
			                                addIn.Manifest.PrimaryIdentity);
			try
			{
				if (Directory.Exists(targetDir))
				{
					Directory.Delete(targetDir, true);
				}
				OdAddinPackage.Extract(zipFile, targetDir);
				// Find the .addin file to set it in the AddIn object; it carries the source of the installation.
				var addInFile = OdAddinPackage.FindUnpackedManifest(targetDir);
				if (addInFile == null)
				{
					throw new InvalidDataException("The unpacked package has no .addin manifest.");
				}
				OdAddinPackage.PatchSourceAttributes(addInFile.FullName, source);
				if (addIn.FileName == null)
				{
					addIn.FileName = addInFile.FullName;
				}
				// Also set the properties of the loaded AddIn object, which was read before the manifest was rewritten.
				addIn.Properties.Remove(OdAddinPackage.RepositoryManifestAttribute);
				addIn.Properties.Remove(OdAddinPackage.ReleaseManifestAttribute);
				if (source != null)
				{
					addIn.Properties.Set(OdAddinPackage.RepositoryManifestAttribute, source.Repository.FullName);
					addIn.Properties.Set(OdAddinPackage.ReleaseManifestAttribute, source.Tag);
				}
				return true;
			}
			catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is System.Xml.XmlException)
			{
				SD.Log.WarnFormatted("[AddInManager2] Cannot unpack package {0}: {1}", zipFile, ex.Message);
				try
				{
					if (Directory.Exists(targetDir))
					{
						Directory.Delete(targetDir, true);
					}
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
				return false;
			}
		}
		
		public void CancelUpdate(AddIn addIn)
		{
			if ((addIn != null) && (addIn.Action == AddInAction.Update))
			{
				CancelPendingUpdate(addIn);
				
				AddInInstallationEventArgs eventArgs = new AddInInstallationEventArgs(addIn);
				eventArgs.PreviousVersionRemains = true;
				_events.OnAddInUninstalled(eventArgs);
				_events.OnAddInStateChanged(eventArgs);
			}
		}
		
		private void CancelPendingUpdate(AddIn addIn)
		{
			if ((addIn != null) && (addIn.Manifest != null))
			{
				SD.Log.DebugFormatted("[AddInManager2] Cancelling update for {0}", addIn.Name);
				
				foreach (string identity in addIn.Manifest.Identities.Keys)
				{
					// Delete from installation temp (if installation or update is pending)
					string targetDir = Path.Combine(_sdAddInManagement.TempInstallDirectory, identity);
					if (Directory.Exists(targetDir))
					{
						Directory.Delete(targetDir, true);
					}
				}
				
				_addInsMarkedForInstall.RemoveAll(markedAddIn => markedAddIn.AddIn == addIn);
			}
		}
		
		public void CancelInstallation(AddIn addIn)
		{
			if (addIn != null)
			{
				SD.Log.DebugFormatted("[AddInManager2] Cancelling installation of {0}", addIn.Name);
				
				_addInsMarkedForInstall.RemoveAll(markedAddIn => markedAddIn.AddIn == addIn);
				UninstallAddIn(addIn);
			}
		}
		
		public void CancelUninstallation(AddIn addIn)
		{
			if ((addIn != null) && (addIn.Manifest != null))
			{
				SD.Log.DebugFormatted("[AddInManager2] Cancelling uninstallation of {0}", addIn.Name);
				
				// Abort uninstallation of this AddIn
				foreach (string identity in addIn.Manifest.Identities.Keys)
				{
					_sdAddInManagement.AbortRemoveUserAddInOnNextStart(identity);
				}
				_sdAddInManagement.Enable(new AddIn[] { addIn });
				_events.OnAddInStateChanged(new AddInInstallationEventArgs(addIn));
			}
		}

		public void UninstallAddIn(AddIn addIn)
		{
			if ((addIn != null) && (addIn.Manifest.PrimaryIdentity != null))
			{
				SD.Log.DebugFormatted("[AddInManager2] Uninstalling AddIn {0}", addIn.Name);
				
				List<AddIn> addInList = new List<AddIn>();
				addInList.Add(addIn);
				_sdAddInManagement.RemoveExternalAddIns(addInList);
				
				CancelPendingUpdate(addIn);
				foreach (string identity in addIn.Manifest.Identities.Keys)
				{
					// Remove the user AddIn
					string targetDir = Path.Combine(_sdAddInManagement.UserInstallDirectory, identity);
					if (Directory.Exists(targetDir))
					{
						if (!addIn.Enabled)
						{
							try
							{
								Directory.Delete(targetDir, true);
								continue;
							}
							catch
							{
								// TODO Throw something?
							}
						}
					}
					
					_sdAddInManagement.RemoveUserAddInOnNextStart(identity);
				}
				
				// Successfully uninstalled
				AddInInstallationEventArgs eventArgs = new AddInInstallationEventArgs(addIn);
				_events.OnAddInUninstalled(eventArgs);
				_events.OnAddInStateChanged(eventArgs);
			}
		}
		
		public void SwitchAddInSuspension(AddIn addIn)
		{
			if (addIn == null)
				return;
			if (addIn.Suspended)
				_sdAddInManagement.Resume(new AddIn[] { addIn });
			else
				_sdAddInManagement.Suspend(new AddIn[] { addIn });
		}

		public void SwitchAddInActivation(AddIn addIn)
		{
			if (addIn != null)
			{
				// Decide whether to enable or to disable the AddIn
				bool disable = addIn.Enabled;
				if (addIn.Action == AddInAction.Disable)
				{
					disable = false;
				}
				else if (addIn.Action == AddInAction.Enable)
				{
					disable = true;
				}
				
				if (disable)
				{
					_sdAddInManagement.Disable(new AddIn[] { addIn });
				}
				else
				{
					_sdAddInManagement.Enable(new AddIn[] { addIn });
				}
				
				_events.OnAddInStateChanged(new AddInInstallationEventArgs(addIn));
			}
		}
		
		public AddIn GetInstalledAddInByIdentity(string identity)
		{
			if (!String.IsNullOrEmpty(identity))
			{
				return _sdAddInManagement.AddIns
					.Where(a => (a.Manifest != null) && a.Manifest.Identities.ContainsKey(identity))
					.FirstOrDefault();
			}
			else
			{
				return null;
			}
		}
		
		public bool IsAddInInstalled(AddIn addIn)
		{
			if (addIn == null)
			{
				// Without a valid AddIn instance we can't do anything...
				return false;
			}
			
			string identity = null;
			if (addIn.Manifest != null)
			{
				identity = addIn.Manifest.PrimaryIdentity;
			}
			return (_sdAddInManagement.AddIns
			        .Where(a => (addIn == a) || ((identity != null) && (a.Manifest != null) && a.Manifest.Identities.ContainsKey(identity)))
			        .FirstOrDefault() != null);
		}
		
		public bool IsAddInPreinstalled(AddIn addIn)
		{
			if (addIn != null)
			{
				return
					String.Equals(addIn.Properties["addInManagerHidden"], "preinstalled", StringComparison.OrdinalIgnoreCase)
					&& FileUtility.IsBaseDirectory(FileUtility.ApplicationRootPath, addIn.FileName);
			}
			else
			{
				return false;
			}
		}
		
		public IEnumerable<ManagedAddIn> GetDependentAddIns(AddIn addIn)
		{
			if ((addIn != null) && (addIn.Manifest != null) && (addIn.Manifest.PrimaryIdentity != null))
			{
				// Get all AddIns which are dependent from given AddIn
				var dependentAddIns = AddInsWithMarkedForInstallation.Where(
					a => (a.AddIn.Manifest != null) && a.AddIn.Manifest.Dependencies.Any(
						reference => reference.Name == addIn.Manifest.PrimaryIdentity));
				return dependentAddIns;
			}
			
			return null;
		}
		
	}
}
