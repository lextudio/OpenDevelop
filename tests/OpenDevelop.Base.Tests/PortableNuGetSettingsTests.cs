using ICSharpCode.PackageManagement;
using NuGet;
using Xunit;

namespace OpenDevelop.Base.Tests;

// The add-in's settings adapter, driven through the legacy NuGet 2.x ISettings interface the package
// source provider uses: it lists what NuGet's configuration hierarchy gives the solution, and writes only
// the solution's own entries, in the file NuGet itself reads.
public sealed class PortableNuGetSettingsTests : IDisposable
{
	readonly string root = Directory.CreateTempSubdirectory("nuget-settings-").FullName;

	public void Dispose() => Directory.Delete(root, recursive: true);

	string Work => Path.Combine(root, "work");

	static string Config(string directory, string fileName, string body)
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, fileName);
		File.WriteAllText(path, "<configuration>\n" + body + "</configuration>\n");
		return path;
	}

	static string Sources(string entries) => "  <packageSources>\n" + entries + "  </packageSources>\n";

	static string Add(string key, string value) => "    <add key=\"" + key + "\" value=\"" + value + "\" />\n";

	static string[] Keys(ISettings settings) =>
		settings.GetValues("packageSources", false).Select(v => v.Key).OrderBy(k => k).ToArray();

	[Fact]
	public void ListsTheSourcesOfTheSolutionDirectoryAndTheOnesAbove()
	{
		Config(root, "NuGet.Config", Sources("    <clear />\n" + Add("Parent", "https://parent.example/index.json")));
		Config(Work, "NuGet.Config", Sources(Add("Child", "https://child.example/index.json")));

		Assert.Equal(new[] { "Child", "Parent" }, Keys(new PortableNuGetSettings(Work)));
	}

	[Fact]
	public void SavingDoesNotCopyInheritedSourcesIntoTheSolutionFile()
	{
		Config(root, "NuGet.Config", Sources("    <clear />\n" + Add("Inherited", "https://parent.example/index.json")));
		var ownFile = Config(Work, "NuGet.Config", Sources(Add("Own", "https://own.example/index.json")));
		var settings = new PortableNuGetSettings(Work);
		var merged = settings.GetValues("packageSources", true).ToList();
		merged.Add(new SettingValue("Added", "https://added.example/index.json", false));

		settings.SetValues("packageSources", merged);

		var text = File.ReadAllText(ownFile);
		Assert.Contains("key=\"Own\"", text);
		Assert.Contains("key=\"Added\"", text);
		Assert.DoesNotContain("Inherited", text);
		Assert.Equal(new[] { "Added", "Inherited", "Own" }, Keys(new PortableNuGetSettings(Work)));
	}

	[Fact]
	public void SavingKeepsAClearInTheSolutionFile()
	{
		// <clear/> hides the user's and the machine's sources: losing it on save would switch them back on.
		var ownFile = Config(Work, "NuGet.Config", Sources("    <clear />\n" + Add("Own", "https://own.example/index.json")));
		var settings = new PortableNuGetSettings(Work);
		var values = settings.GetValues("packageSources", true).ToList();
		values.Add(new SettingValue("Added", "https://added.example/index.json", false));

		settings.SetValues("packageSources", values);

		Assert.Contains("<clear", File.ReadAllText(ownFile));
		Assert.Equal(new[] { "Added", "Own" }, Keys(new PortableNuGetSettings(Work)));
	}

	[Fact]
	public void ASourceIsWrittenToTheSolutionDirectory()
	{
		Config(root, "NuGet.Config", Sources("    <clear />\n"));
		Directory.CreateDirectory(Work);
		var settings = new PortableNuGetSettings(Work);

		settings.SetValues("packageSources", new List<SettingValue> { new SettingValue("Added", "https://added.example/index.json", false) });

		Assert.True(File.Exists(Path.Combine(Work, "NuGet.Config")));
		Assert.False(Directory.Exists(Path.Combine(Work, ".nuget")));
		Assert.Equal(new[] { "Added" }, Keys(new PortableNuGetSettings(Work)));
	}

	[Fact]
	public void TheSpellingTheSolutionAlreadyHasIsTheOneEdited()
	{
		Config(root, "NuGet.Config", Sources("    <clear />\n"));
		var existing = Config(Work, "nuget.config", Sources(Add("Own", "https://own.example/index.json")));

		new PortableNuGetSettings(Work).SetValues("packageSources", new List<SettingValue> {
			new SettingValue("Own", "https://own.example/index.json", false),
			new SettingValue("Added", "https://added.example/index.json", false) });

		Assert.Contains("key=\"Added\"", File.ReadAllText(existing));
		Assert.Single(Directory.GetFiles(Work), file => string.Equals(Path.GetFileName(file), "nuget.config", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ARelativeFeedPathStaysRelativeWhenOtherSourcesAreEdited()
	{
		Config(root, "NuGet.Config", Sources("    <clear />\n"));
		var ownFile = Config(Work, "NuGet.Config", Sources(Add("Feed", "local-feed")));
		var settings = new PortableNuGetSettings(Work);
		var values = settings.GetValues("packageSources", true).ToList();
		Assert.Equal(Path.Combine(Work, "local-feed"), Path.GetFullPath(values.Single().Value));   // handed out resolved
		values.Add(new SettingValue("Added", "https://added.example/index.json", false));

		settings.SetValues("packageSources", values);

		var text = File.ReadAllText(ownFile);
		Assert.Contains("key=\"Feed\" value=\"local-feed\"", text);   // written back as the author wrote it
		Assert.DoesNotContain(Work, text);
	}

	[Fact]
	public void DisabledSourcesRoundTripAndInheritedOnesAreNotCopied()
	{
		Config(root, "NuGet.Config", "  <disabledPackageSources>\n    <clear />\n" + Add("Inherited", "true") + "  </disabledPackageSources>\n");
		var ownFile = Config(Work, "NuGet.Config", Sources(Add("Own", "https://own.example/index.json")));
		var settings = new PortableNuGetSettings(Work);
		Assert.Equal(new[] { "Inherited" }, settings.GetValues("disabledPackageSources", false).Select(v => v.Key).ToArray());

		// The user disables "Own" as well: the provider hands back the merged list, inherited entry included.
		var disabled = settings.GetValues("disabledPackageSources", false).ToList();
		disabled.Add(new SettingValue("Own", "true", false));
		settings.SetValues("disabledPackageSources", disabled);

		var text = File.ReadAllText(ownFile);
		Assert.Contains("key=\"Own\" value=\"true\"", text);
		Assert.DoesNotContain("key=\"Inherited\"", text);
		Assert.Equal(new[] { "Inherited", "Own" },
			new PortableNuGetSettings(Work).GetValues("disabledPackageSources", false).Select(v => v.Key).OrderBy(k => k).ToArray());

		// Enabling "Own" again removes it from the file and leaves the inherited one alone.
		var stillDisabled = new PortableNuGetSettings(Work).GetValues("disabledPackageSources", false).Where(v => v.Key != "Own").ToList();
		new PortableNuGetSettings(Work).SetValues("disabledPackageSources", stillDisabled);
		Assert.DoesNotContain("key=\"Own\" value=\"true\"", File.ReadAllText(ownFile));
		Assert.Equal(new[] { "Inherited" },
			new PortableNuGetSettings(Work).GetValues("disabledPackageSources", false).Select(v => v.Key).ToArray());
	}

	[Fact]
	public void OtherSectionsAreWrittenAsBefore()
	{
		var ownFile = Config(Work, "NuGet.Config", Sources(Add("Own", "https://own.example/index.json")));

		new PortableNuGetSettings(Work).SetValues("activePackageSource", new List<SettingValue> { new SettingValue("All", "(Aggregate source)", false) });

		var text = File.ReadAllText(ownFile);
		Assert.Contains("<activePackageSource>", text);
		Assert.Contains("key=\"Own\"", text);
	}
}
