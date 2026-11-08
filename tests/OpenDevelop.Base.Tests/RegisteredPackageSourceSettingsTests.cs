using ICSharpCode.Core;
using ICSharpCode.PackageManagement;
using Xunit;
using Modern = NuGet.Configuration;

namespace OpenDevelop.Base.Tests;

// The package source list of the add-in is read and saved by NuGet.Configuration's own provider, so what
// these check is the add-in's use of it: sources come from every file NuGet merges, changes are saved the
// way NuGet saves them (where the source is defined), the active source round-trips, and merely looking
// at the default does not write anything. The settings are loaded from exactly the given files, never from
// the user's own configuration.
public sealed class RegisteredPackageSourceSettingsTests : IDisposable
{
	readonly string root = Directory.CreateTempSubdirectory("package-source-settings-").FullName;

	public void Dispose() => Directory.Delete(root, recursive: true);

	string ParentFile => Path.Combine(root, "NuGet.Config");
	string WorkFile => Path.Combine(root, "work", "NuGet.Config");

	void Write(string file, string body)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllText(file, "<configuration>\n" + body + "</configuration>\n");
	}

	static string Add(string key, string value, string extra = "") => "    <add key=\"" + key + "\" value=\"" + value + "\"" + extra + " />\n";

	void ParentDefinesInheritedAndOther()
	{
		Write(ParentFile,
			"  <packageSources>\n    <clear />\n" + Add("Inherited", "https://parent.example/index.json") + Add("Other", "https://other.example/index.json") + "  </packageSources>\n" +
			"  <disabledPackageSources>\n" + Add("Other", "true") + "  </disabledPackageSources>\n");
		Write(WorkFile, "  <packageSources>\n" + Add("Own", "https://own.example/index.json") + "  </packageSources>\n");
	}

	sealed class IsolatedSettingsProvider : ISettingsProvider
	{
		readonly string[] files;
		readonly string directory;

		public IsolatedSettingsProvider(string directory, params string[] files)
		{
			this.directory = directory;
			this.files = files;
		}

		public event EventHandler? SettingsChanged { add { } remove { } }

		public NuGet.ISettings LoadSettings() => new PortableNuGetSettings(directory);

		public Modern.ISettings LoadNuGetSettings() => Modern.Settings.LoadSettingsGivenConfigPaths(files);
	}

	RegisteredPackageSourceSettings Load() =>
		new RegisteredPackageSourceSettings(new IsolatedSettingsProvider(Path.Combine(root, "work"), WorkFile, ParentFile));

	static string[] Names(RegisteredPackageSourceSettings settings) =>
		settings.PackageSources.Select(s => s.Name).OrderBy(n => n).ToArray();

	[Fact]
	public void ListsTheSourcesOfEveryFileNuGetMerges()
	{
		ParentDefinesInheritedAndOther();

		var settings = Load();

		Assert.Equal(new[] { "Inherited", "Other", "Own" }, Names(settings));
		Assert.False(settings.PackageSources.Single(s => s.Name == "Other").IsEnabled);
		Assert.True(settings.PackageSources.Single(s => s.Name == "Inherited").IsEnabled);
	}

	[Fact]
	public void AnAddedSourceIsSavedByNuGetAndFoundAgain()
	{
		ParentDefinesInheritedAndOther();
		var settings = Load();

		settings.PackageSources.Add(new NuGet.PackageSource("https://added.example/index.json", "Added"));

		Assert.Equal(new[] { "Added", "Inherited", "Other", "Own" }, Names(Load()));
		var files = File.ReadAllText(ParentFile) + File.ReadAllText(WorkFile);
		Assert.Contains("key=\"Added\"", files);
		Assert.Equal(1, files.Split("key=\"Added\"").Length - 1);   // once, not copied into every file
	}

	[Fact]
	public void AnInheritedSourceCanBeRemovedAndTheFileThatDefinesItIsEdited()
	{
		ParentDefinesInheritedAndOther();
		var settings = Load();

		settings.PackageSources.Remove(settings.PackageSources.Single(s => s.Name == "Inherited"));

		Assert.Equal(new[] { "Other", "Own" }, Names(Load()));
		Assert.DoesNotContain("Inherited", File.ReadAllText(ParentFile));   // NuGet's rule: the defining file changes
		Assert.Contains("key=\"Own\"", File.ReadAllText(WorkFile));
	}

	[Fact]
	public void EnablingAndDisablingAreSavedInTheDisabledList()
	{
		ParentDefinesInheritedAndOther();
		var settings = Load();

		// "Other" is disabled by the parent file: enable it, and disable "Own".
		settings.PackageSources.Single(s => s.Name == "Other").IsEnabled = true;
		settings.PackageSources.Single(s => s.Name == "Own").IsEnabled = false;
		settings.PackageSources.Add(new NuGet.PackageSource("https://added.example/index.json", "Added"));   // a change is what triggers the save

		var reloaded = Load();
		Assert.True(reloaded.PackageSources.Single(s => s.Name == "Other").IsEnabled);
		Assert.False(reloaded.PackageSources.Single(s => s.Name == "Own").IsEnabled);
	}

	[Fact]
	public void TheActiveSourceRoundTripsIncludingAggregateAndNone()
	{
		ParentDefinesInheritedAndOther();
		var settings = Load();
		Assert.Null(settings.ActivePackageSource);   // nothing chosen yet

		settings.ActivePackageSource = settings.PackageSources.Single(s => s.Name == "Own");
		Assert.Equal("Own", Load().ActivePackageSource!.Name);

		settings.ActivePackageSource = RegisteredPackageSourceSettings.AggregatePackageSource;
		Assert.True(Load().ActivePackageSource!.IsAggregate());

		settings.ActivePackageSource = null;
		Assert.Null(Load().ActivePackageSource);
	}

	[Fact]
	public void ASavedActiveSourceThatNoLongerExistsIsNotActive()
	{
		ParentDefinesInheritedAndOther();
		var settings = Load();
		settings.ActivePackageSource = settings.PackageSources.Single(s => s.Name == "Own");
		Write(WorkFile, "  <packageSources>\n" + Add("Different", "https://different.example/index.json") + "  </packageSources>\n" +
			"  <activePackageSource>\n" + Add("Own", "https://own.example/index.json") + "  </activePackageSource>\n");

		Assert.Null(Load().ActivePackageSource);
	}

	// ---- the Sources dialog's OK button: RegisteredPackageRepositories.UpdatePackageSources ----

	sealed class UnusedRepositoryCache : IPackageRepositoryCache
	{
		public IRecentPackageRepository RecentPackageRepository => throw new NotSupportedException();
		public NuGet.IPackageRepository CreateAggregateRepository() => throw new NotSupportedException();
		public NuGet.IPackageRepository CreateRepository(string packageSource) => throw new NotSupportedException();
		public NuGet.ISharedPackageRepository CreateSharedRepository(NuGet.IPackagePathResolver pathResolver, NuGet.IFileSystem fileSystem, NuGet.IFileSystem configSettingsFileSystem) => throw new NotSupportedException();
		public IRecentPackageRepository CreateRecentPackageRepository(IList<RecentPackageInfo> recentPackages, NuGet.IPackageRepository aggregateRepository) => throw new NotSupportedException();
		public NuGet.IPackageRepository CreateAggregateRepository(IEnumerable<NuGet.IPackageRepository> repositories) => throw new NotSupportedException();
	}

	RegisteredPackageRepositories LoadRepositories()
	{
		var options = new PackageManagementOptions(new Properties(), new IsolatedSettingsProvider(Path.Combine(root, "work"), WorkFile, ParentFile));
		return new RegisteredPackageRepositories(new UnusedRepositoryCache(), options);
	}

	[Fact]
	public void ConfirmingTheSourcesDialogWithoutChangesLeavesEveryFileByteIdentical()
	{
		// UpdatePackageSources used to clear the list and add the sources back one by one; each step was saved,
		// the first one as an empty list, which tells NuGet to delete every source from the file defining it.
		ParentDefinesInheritedAndOther();
		var parentBefore = File.ReadAllBytes(ParentFile);
		var workBefore = File.ReadAllBytes(WorkFile);
		var repositories = LoadRepositories();

		repositories.UpdatePackageSources(repositories.PackageSources.ToList());

		Assert.Equal(parentBefore, File.ReadAllBytes(ParentFile));
		Assert.Equal(workBefore, File.ReadAllBytes(WorkFile));
		Assert.Equal(new[] { "Inherited", "Other", "Own" }, Names(Load()));
	}

	[Fact]
	public void ConfirmingTheDialogAfterRemovingOneSourceTouchesOnlyTheFileThatDefinedIt()
	{
		ParentDefinesInheritedAndOther();
		var parentBefore = File.ReadAllBytes(ParentFile);
		var repositories = LoadRepositories();

		repositories.UpdatePackageSources(repositories.PackageSources.Where(s => s.Name != "Own").ToList());

		Assert.Equal(parentBefore, File.ReadAllBytes(ParentFile));
		Assert.DoesNotContain("Own", File.ReadAllText(WorkFile));
		Assert.Equal(new[] { "Inherited", "Other" }, Names(Load()));
	}

	[Fact]
	public void ConfirmingTheDialogWithANewSourceDoesNotCopyInheritedOnesAnywhere()
	{
		ParentDefinesInheritedAndOther();
		var repositories = LoadRepositories();
		var edited = repositories.PackageSources.ToList();
		edited.Add(new NuGet.PackageSource("https://added.example/index.json", "Added"));

		repositories.UpdatePackageSources(edited);

		Assert.Equal(new[] { "Added", "Inherited", "Other", "Own" }, Names(Load()));
		var files = File.ReadAllText(ParentFile) + File.ReadAllText(WorkFile);
		Assert.Equal(1, files.Split("key=\"Inherited\"").Length - 1);
		Assert.Equal(1, files.Split("key=\"Own\"").Length - 1);
		Assert.Equal(1, files.Split("key=\"Added\"").Length - 1);
	}

	[Fact]
	public void TheActiveSourceIsFoundWhenTheConfigurationSpellsItsNameInAnotherCase()
	{
		ParentDefinesInheritedAndOther();
		Write(WorkFile, "  <packageSources>\n" + Add("Own", "https://own.example/index.json") + "  </packageSources>\n" +
			"  <activePackageSource>\n" + Add("OWN", "https://own.example/index.json") + "  </activePackageSource>\n");

		Assert.Equal("Own", Load().ActivePackageSource!.Name);
	}
}
