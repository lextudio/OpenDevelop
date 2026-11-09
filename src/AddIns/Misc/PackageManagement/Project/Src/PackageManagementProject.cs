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
using System.Linq;
using System.Runtime.Versioning;

using ICSharpCode.PackageManagement.EnvDTE;
using ICSharpCode.SharpDevelop.Project;
using NuGet;

namespace ICSharpCode.PackageManagement
{
	public class PackageManagementProject : IPackageManagementProject
	{
		ISharpDevelopPackageManager packageManager;
		ISharpDevelopProjectManager projectManager;
		IPackageManagementEvents packageManagementEvents;
		MSBuildBasedProject msbuildProject;
		ProjectTargetFramework targetFramework;
		SdkStylePackageReferenceService sdkStylePackageReferenceService;
		
		public PackageManagementProject(
			IPackageRepository sourceRepository,
			MSBuildBasedProject project,
			IPackageManagementEvents packageManagementEvents,
			IPackageManagerFactory packageManagerFactory)
		{
			SourceRepository = sourceRepository;
			msbuildProject = project;
			if (project.IsSdkStyleProject) {
				sdkStylePackageReferenceService = new SdkStylePackageReferenceService(project);
			}
			this.packageManagementEvents = packageManagementEvents;
			
			packageManager = packageManagerFactory.CreatePackageManager(sourceRepository, project);
			projectManager = packageManager.ProjectManager;
		}
		
		public bool UsesPackageReferences {
			get { return sdkStylePackageReferenceService != null; }
		}

		public IReadOnlyList<NuGet.Frameworks.NuGetFramework> TargetFrameworks {
			get {
				return ProjectTargetFrameworks.Parse(
					msbuildProject.GetEvaluatedProperty("TargetFrameworks"),
					msbuildProject.GetEvaluatedProperty("TargetFramework"));
			}
		}

		/// <summary>packages.config projects are no longer supported: the legacy engine is gone from every operation that changes packages.</summary>
		static Exception PackagesConfigNotSupported()
		{
			return new NotSupportedException("Packages cannot be changed in this project because it uses packages.config, which is no longer supported. Migrate it to PackageReference (right-click the packages.config file > Migrate packages.config to PackageReference), then try again.");
		}

		public string Name {
			get { return msbuildProject.Name; }
		}
		
		public FrameworkName TargetFramework {
			// Evaluated on demand: a project MSBuild cannot evaluate must still open in the dialog (to say it is unsupported).
			get { return (targetFramework ??= new ProjectTargetFramework(msbuildProject)).TargetFrameworkName; }
		}
		
		public IPackageRepository SourceRepository { get; private set; }
		
		public ILogger Logger {
			get { return packageManager.Logger; }
			set {
				packageManager.Logger = value;
				packageManager.FileSystem.Logger = value;
			
				projectManager.Logger = value;
				projectManager.Project.Logger = value;
			}
		}
		
		public event EventHandler<PackageOperationEventArgs> PackageInstalled {
			add { packageManager.PackageInstalled += value; }
			remove { packageManager.PackageInstalled -= value; }
		}
		
		public event EventHandler<PackageOperationEventArgs> PackageUninstalled {
			add { packageManager.PackageUninstalled += value; }
			remove { packageManager.PackageUninstalled -= value; }
		}
		
		public event EventHandler<PackageOperationEventArgs> PackageReferenceAdded {
			add { projectManager.PackageReferenceAdded += value; }
			remove { projectManager.PackageReferenceAdded -= value; }
		}
		
		public event EventHandler<PackageOperationEventArgs> PackageReferenceRemoving {
			add { projectManager.PackageReferenceRemoving += value; }
			remove { projectManager.PackageReferenceRemoving -= value; }
		}
		
		public bool IsPackageInstalled(IPackage package)
		{
			if (sdkStylePackageReferenceService != null) {
				return sdkStylePackageReferenceService.IsInstalled(package.Id);
			}
			return projectManager.IsInstalled(package);
		}
		
		public bool IsPackageInstalled(string packageId)
		{
			if (sdkStylePackageReferenceService != null) {
				return sdkStylePackageReferenceService.IsInstalled(packageId);
			}
			return projectManager.IsInstalled(packageId);
		}
		
		public IQueryable<IPackage> GetPackages()
		{
			if (sdkStylePackageReferenceService != null) {
				// The legacy local repository knows packages.config, not PackageReference items.
				return sdkStylePackageReferenceService.GetInstalledPackages().AsQueryable();
			}
			return projectManager.LocalRepository.GetPackages();
		}
		
		public IEnumerable<PackageOperation> GetInstallPackageOperations(IPackage package, InstallPackageAction installAction)
		{
			if (sdkStylePackageReferenceService != null) {
				return Enumerable.Empty<PackageOperation>();
			}
			throw PackagesConfigNotSupported();
		}
		
		public void InstallPackage(IPackage package, InstallPackageAction installAction)
		{
			if (sdkStylePackageReferenceService != null) {
				sdkStylePackageReferenceService.InstallPackage(package, SourceRepository, Logger);
				return;
			}
			throw PackagesConfigNotSupported();
		}
		
		public void UninstallPackage(IPackage package, UninstallPackageAction uninstallAction)
		{
			if (sdkStylePackageReferenceService != null) {
				sdkStylePackageReferenceService.UninstallPackage(package.Id, Logger);
				return;
			}
			throw PackagesConfigNotSupported();
		}
		
		public void UpdatePackage(IPackage package, UpdatePackageAction updateAction)
		{
			if (sdkStylePackageReferenceService != null) {
				// Replacing the reference with the new version is what an update of a PackageReference is.
				sdkStylePackageReferenceService.UpdatePackages(new[] { package }, Logger);
				return;
			}
			throw PackagesConfigNotSupported();
		}
		
		public InstallPackageAction CreateInstallPackageAction()
		{
			return new InstallPackageAction(this, packageManagementEvents);
		}
		
		public UninstallPackageAction CreateUninstallPackageAction()
		{
			return new UninstallPackageAction(this, packageManagementEvents);
		}
		
		public UpdatePackageAction CreateUpdatePackageAction()
		{
			return new UpdatePackageAction(this, packageManagementEvents);
		}
		
		public Project ConvertToDTEProject()
		{
			return new Project(msbuildProject);
		}
		
		public IEnumerable<IPackage> GetPackagesInReverseDependencyOrder()
		{
			var packageSorter = new PackageSorter(null);
			return packageSorter
				.GetPackagesByDependencyOrder(projectManager.LocalRepository)
				.Reverse();
		}
		
		public void UpdatePackages(UpdatePackagesAction updateAction)
		{
			if (sdkStylePackageReferenceService != null) {
				sdkStylePackageReferenceService.UpdatePackages(updateAction.Packages, Logger);
				return;
			}
			throw PackagesConfigNotSupported();
		}
		
		public UpdatePackagesAction CreateUpdatePackagesAction()
		{
			return new UpdatePackagesAction(this, packageManagementEvents);
		}
		
		public IEnumerable<PackageOperation> GetUpdatePackagesOperations(
			IEnumerable<IPackage> packages,
			IUpdatePackageSettings settings)
		{
			if (sdkStylePackageReferenceService != null) {
				return Enumerable.Empty<PackageOperation>();
			}
			throw PackagesConfigNotSupported();
		}
		
		public void RunPackageOperations(IEnumerable<PackageOperation> operations)
		{
			if (sdkStylePackageReferenceService != null) {
				return; // a PackageReference project has no package operations: restore resolves them
			}
			throw PackagesConfigNotSupported();
		}
		
		public bool HasOlderPackageInstalled(IPackage package)
		{
			if (sdkStylePackageReferenceService != null) {
				return GetPackages().Any(installed => string.Equals(installed.Id, package.Id, StringComparison.OrdinalIgnoreCase)
					&& installed.Version < package.Version);
			}
			return projectManager.HasOlderPackageInstalled(package);
		}
		
		public void UpdatePackageReference(IPackage package, IUpdatePackageSettings settings)
		{
			if (sdkStylePackageReferenceService != null) {
				sdkStylePackageReferenceService.UpdatePackages(new[] { package }, Logger);
				return;
			}
			throw PackagesConfigNotSupported();
		}
		
		public IPackageConstraintProvider ConstraintProvider {
			get {
				var constraintProvider = projectManager.LocalRepository as IPackageConstraintProvider;
				if (constraintProvider != null) {
					return constraintProvider;
				}
				return NullConstraintProvider.Instance;
			}
		}

		public IPackage FindPackage(string packageId, SemanticVersion version)
		{
			IPackage package = projectManager.LocalRepository.FindPackage(packageId, version);

			if (package != null) {
				return package;
			}

			if (version != null) {
				return packageManager.LocalRepository.FindPackage(packageId, version);
			}

			List<IPackage> packages = packageManager.LocalRepository.FindPackagesById(packageId).ToList();
			if (packages.Count > 1) {
				throw CreateAmbiguousPackageException(packageId);
			} 
			
			return packages.FirstOrDefault();
		}

		InvalidOperationException CreateAmbiguousPackageException(string packageId)
		{
			string message = String.Format("Multiple versions of '{0}' found. Please specify the version.", packageId);
			return new InvalidOperationException(message);
		}
		
		public ReinstallPackageAction CreateReinstallPackageAction()
		{
			return new ReinstallPackageAction(this, packageManagementEvents);
		}
	}
}
