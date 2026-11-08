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
using System.Collections.Specialized;
using System.Linq;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using NuGet;
using Modern = NuGet.Configuration;

namespace ICSharpCode.PackageManagement
{
	public class RegisteredPackageSourceSettings
	{
		public static readonly string PackageSourcesSectionName = "packageSources";
		public static readonly string ActivePackageSourceSectionName = "activePackageSource";
		public static readonly string DisabledPackageSourceSectionName = "disabledPackageSources";
		
		public static readonly PackageSource AggregatePackageSource = 
			new PackageSource("(Aggregate source)", "All");
		
		ISettings settings;
		ISettingsProvider settingsProvider;
		// Sources are read and saved by NuGet.Configuration's own provider (the same one Visual Studio and
		// the dotnet CLI use): it merges the configuration hierarchy and saves each change to the file
		// that NuGet says it belongs in. The legacy ISettings above is kept for the other settings.
		Modern.ISettings nugetSettings;
		Modern.IPackageSourceProvider packageSourceProvider;
		// What NuGet gave us for each name, so saving does not lose what the legacy PackageSource cannot
		// carry (credentials, protocol version, ...).
		Dictionary<string, Modern.PackageSource> loadedSources = new Dictionary<string, Modern.PackageSource>(StringComparer.OrdinalIgnoreCase);
		PackageSource defaultPackageSource;
		RegisteredPackageSources packageSources;
		PackageSource activePackageSource;
		
		public RegisteredPackageSourceSettings(ISettingsProvider settingsProvider)
			: this(
				settingsProvider,
				RegisteredPackageSources.DefaultPackageSource)
		{
		}
		
		public RegisteredPackageSourceSettings(
			ISettingsProvider settingsProvider,
			PackageSource defaultPackageSource)
		{
			this.settingsProvider = settingsProvider;
			this.defaultPackageSource = defaultPackageSource;
			
			settings = settingsProvider.LoadSettings();
			LoadNuGetSettings();
			
			ReadActivePackageSource();
			RegisterSolutionEvents();
		}

		void RegisterSolutionEvents()
		{
			settingsProvider.SettingsChanged += SettingsChanged;
		}
		
		void LoadNuGetSettings()
		{
			try {
				nugetSettings = settingsProvider.LoadNuGetSettings();
			} catch (Exception ex) {
				LoggingService.Warn("Unable to read the NuGet configuration.", ex);
				// Fall back to nuget.org only and treat the configuration as read-only.
				nugetSettings = Modern.NullSettings.Instance;
			}
			packageSourceProvider = new Modern.PackageSourceProvider(nugetSettings);
		}
		
		void ReadActivePackageSource()
		{
			string name = packageSourceProvider.ActivePackageSourceName;
			if (String.IsNullOrEmpty(name)) {
				activePackageSource = null;
			} else if (string.Equals(name, AggregatePackageSource.Name, StringComparison.OrdinalIgnoreCase)) {
				activePackageSource = AggregatePackageSource;
			} else {
				// Resolved against the registered sources by ActivePackageSource's getter.
				activePackageSource = new PackageSource(name, name);
			}
		}
		
		public RegisteredPackageSources PackageSources {
			get {
				if (packageSources == null) {
					TryReadPackageSources();
				}
				return packageSources;
			}
		}
		
		void TryReadPackageSources()
		{
			try {
				ReadPackageSources();
			} catch (Exception ex) {
				LoggingService.Warn("Unable to read the NuGet configuration.", ex);

				// Fallback to using the default package source only (nuget.org)
				// and treat the configuration as read-only.
				nugetSettings = Modern.NullSettings.Instance;
				packageSourceProvider = new Modern.PackageSourceProvider(nugetSettings);
				ReadPackageSources();
			}
		}
		
		void ReadPackageSources()
		{
			loadedSources.Clear();
			var savedPackageSources = new List<PackageSource>();
			foreach (Modern.PackageSource source in packageSourceProvider.LoadPackageSources()) {
				loadedSources[source.Name] = source;
				savedPackageSources.Add(new PackageSource(source.Source, source.Name, source.IsEnabled));
			}
			packageSources = new RegisteredPackageSources(savedPackageSources, defaultPackageSource);
			packageSources.CollectionChanged += PackageSourcesChanged;
		}
		
		void PackageSourcesChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			UpdatePackageSourceSettingsWithChanges();
		}
		
		void UpdatePackageSourceSettingsWithChanges()
		{
			if (nugetSettings is Modern.NullSettings) {
				// The configuration could not be read, so it is not written either.
				return;
			}
			// NuGet decides where each change goes (a removal edits the file that defines the source, an
			// addition goes where NuGet puts new sources, as for `dotnet nuget add source`).
			packageSourceProvider.SavePackageSources(packageSources.Select(ToNuGetSource).ToList());
		}

		Modern.PackageSource ToNuGetSource(PackageSource source)
		{
			if (loadedSources.TryGetValue(source.Name, out Modern.PackageSource loaded) && loaded.Source == source.Source) {
				var copy = loaded.Clone();
				copy.IsEnabled = source.IsEnabled;
				return copy;
			}
			return new Modern.PackageSource(source.Source, source.Name, source.IsEnabled);
		}
		
		public PackageSource ActivePackageSource {
			get {
				if (activePackageSource != null) {
					if (activePackageSource.IsAggregate()) {
						return activePackageSource;
					}
					// The configuration stores only the name; the registered source has the rest.
					PackageSource registered = PackageSources.FirstOrDefault(source => string.Equals(source.Name, activePackageSource.Name, StringComparison.OrdinalIgnoreCase));
					if (registered != null) {
						return registered;
					}
				}
				return null;
			}
			set {
				activePackageSource = value;
				
				if (nugetSettings is Modern.NullSettings) {
					// NuGet failed to load settings so do not try to update them since this will fail.
					return;
				}
				
				if (activePackageSource == null) {
					RemoveActivePackageSourceSetting();
				} else {
					packageSourceProvider.SaveActivePackageSource(new Modern.PackageSource(activePackageSource.Source, activePackageSource.Name));
				}
			}
		}
		
		void RemoveActivePackageSourceSetting()
		{
			Modern.SettingSection section = nugetSettings.GetSection(ActivePackageSourceSectionName);
			if (section == null) {
				return;
			}
			foreach (Modern.SettingItem item in section.Items.ToList()) {
				nugetSettings.Remove(ActivePackageSourceSectionName, item);
			}
			nugetSettings.SaveToDisk();
		}
		
		void SettingsChanged(object sender, EventArgs e)
		{
			settings = settingsProvider.LoadSettings();
			LoadNuGetSettings();
			ReadActivePackageSource();
			ResetPackageSources();
		}
		
		void ResetPackageSources()
		{
			if (packageSources != null) {
				packageSources.CollectionChanged -= PackageSourcesChanged;
				packageSources = null;
			}
		}
		
		public ISettings Settings {
			get { return settings; }
		}
	}
}
