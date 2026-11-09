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
using NuGet;

namespace ICSharpCode.PackageManagement
{
	public class PackageManagementEvents : IPackageManagementEvents
	{
		public event EventHandler PackageOperationsStarting;
		
		public void OnPackageOperationsStarting()
		{
			UiThread.Invoke(() => PackageOperationsStarting?.Invoke(this, new EventArgs()));
		}
		
		public event EventHandler<PackageOperationExceptionEventArgs> PackageOperationError;
		
		public void OnPackageOperationError(Exception ex)
		{
			UiThread.Invoke(() => PackageOperationError?.Invoke(this, new PackageOperationExceptionEventArgs(ex)));
		}
		
		public event EventHandler<AcceptLicensesEventArgs> AcceptLicenses;
		
		public bool OnAcceptLicenses(IEnumerable<IPackage> packages)
		{
			return UiThread.Invoke(() => AcceptLicensesOnUiThread(packages));
		}

		bool AcceptLicensesOnUiThread(IEnumerable<IPackage> packages)
		{
			if (AcceptLicenses != null) {
				var eventArgs = new AcceptLicensesEventArgs(packages);
				AcceptLicenses(this, eventArgs);
				return eventArgs.IsAccepted;
			}
			return true;
		}
		
		public event EventHandler<ParentPackageOperationEventArgs> ParentPackageInstalled;
		
		public void OnParentPackageInstalled(IPackage package)
		{
			UiThread.Invoke(() => ParentPackageInstalled?.Invoke(this, new ParentPackageOperationEventArgs(package)));
		}
		
		public event EventHandler<ParentPackageOperationEventArgs> ParentPackageUninstalled;
		
		public void OnParentPackageUninstalled(IPackage package)
		{
			UiThread.Invoke(() => ParentPackageUninstalled?.Invoke(this, new ParentPackageOperationEventArgs(package)));
		}
		
		public event EventHandler<PackageOperationMessageLoggedEventArgs> PackageOperationMessageLogged;
		
		public void OnPackageOperationMessageLogged(MessageLevel level, string message, params object[] args)
		{
			UiThread.Invoke(() => PackageOperationMessageLogged?.Invoke(this, new PackageOperationMessageLoggedEventArgs(level, message, args)));
		}
		
		public event EventHandler<SelectProjectsEventArgs> SelectProjects;
		
		public bool OnSelectProjects(IEnumerable<IPackageManagementSelectedProject> projects)
		{
			return UiThread.Invoke(() => SelectProjectsOnUiThread(projects));
		}

		bool SelectProjectsOnUiThread(IEnumerable<IPackageManagementSelectedProject> projects)
		{
			if (SelectProjects != null) {
				var eventArgs = new SelectProjectsEventArgs(projects);
				SelectProjects(this, eventArgs);
				return eventArgs.IsAccepted;
			}
			return true;
		}
		
		public event EventHandler<ResolveFileConflictEventArgs> ResolveFileConflict;
		
		public FileConflictResolution OnResolveFileConflict(string message)
		{
			return UiThread.Invoke(() => ResolveFileConflictOnUiThread(message));
		}

		FileConflictResolution ResolveFileConflictOnUiThread(string message)
		{
			if (ResolveFileConflict != null) {
				var eventArgs = new ResolveFileConflictEventArgs(message);
				ResolveFileConflict(this, eventArgs);
				return eventArgs.Resolution;
			}
			return FileConflictResolution.IgnoreAll;
		}
		
		public event EventHandler<ParentPackagesOperationEventArgs> ParentPackagesUpdated;
		
		public void OnParentPackagesUpdated(IEnumerable<IPackage> packages)
		{
			UiThread.Invoke(() => ParentPackagesUpdated?.Invoke(this, new ParentPackagesOperationEventArgs(packages)));
		}
	}
}
