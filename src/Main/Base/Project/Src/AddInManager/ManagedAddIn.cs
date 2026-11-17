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
using ICSharpCode.Core;

namespace ICSharpCode.AddInManager2.Model
{
	public enum AddInInstallationSource
	{
		Offline,
		/// <summary>Downloaded from a trusted GitHub repository's release.</summary>
		GitHubRelease
	}
	
	/// <summary>
	/// Extension of AddIn class used in AddInManager internally.
	/// </summary>
	public class ManagedAddIn
	{
		private AddIn _addIn;
		
		public ManagedAddIn(AddIn addIn)
		{
			_addIn = addIn;
			InstallationSource = (InstalledFromRepository != null)
				? AddInInstallationSource.GitHubRelease
				: AddInInstallationSource.Offline;
		}
		
		public bool IsTemporary
		{
			get;
			set;
		}
		
		public bool IsUpdate
		{
			get;
			set;
		}
		
		public Version OldVersion
		{
			get;
			set;
		}
		
		public AddInInstallationSource InstallationSource
		{
			get;
			set;
		}
		
		public AddIn AddIn
		{
			get
			{
				return _addIn;
			}
		}
		
		/// <summary>
		/// The GitHub repository this add-in was installed from, recorded in its manifest when it was
		/// downloaded; null for an add-in installed from a file, or one whose record is not a valid repository.
		/// </summary>
		public RepositoryReference InstalledFromRepository
		{
			get
			{
				if ((_addIn != null) && _addIn.Properties.Contains(OdAddinPackage.RepositoryManifestAttribute))
				{
					if (RepositoryReference.TryParse(_addIn.Properties[OdAddinPackage.RepositoryManifestAttribute], out RepositoryReference repository, out string error))
					{
						return repository;
					}
				}
				return null;
			}
		}

		/// <summary>The release tag (for example v1.2.3) the add-in was installed from, or null.</summary>
		public string InstalledFromReleaseTag
		{
			get
			{
				if ((_addIn != null) && _addIn.Properties.Contains(OdAddinPackage.ReleaseManifestAttribute))
				{
					return _addIn.Properties[OdAddinPackage.ReleaseManifestAttribute];
				}
				return null;
			}
		}
	}
}
