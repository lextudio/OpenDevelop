using ICSharpCode.PackageManagement;
using Xunit;

namespace OpenDevelop.Base.Tests;

// NuGet's definition of the configuration that applies to a solution: its directory and the ones above it
// (the user's and the machine's too, which a test cannot control, so a parent <clear/> fences them off).
public sealed class NuGetConfigHierarchyTests : IDisposable
{
	readonly string root = Directory.CreateTempSubdirectory("nuget-hierarchy-").FullName;

	public void Dispose() => Directory.Delete(root, recursive: true);

	string Config(string directory, string sources)
	{
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "NuGet.Config");
		File.WriteAllText(path, "<configuration>\n  <packageSources>\n" + sources + "  </packageSources>\n</configuration>\n");
		return path;
	}

	[Fact]
	public void ReadsTheSolutionDirectoryAndTheOnesAboveIt()
	{
		var parentFile = Config(root, "    <clear />\n    <add key=\"Parent\" value=\"https://parent.example/v3/index.json\" />\n");
		var work = Path.Combine(root, "work");
		var childFile = Config(work, "    <add key=\"Child\" value=\"https://child.example/v3/index.json\" />\n");

		var entries = NuGetConfigHierarchy.Read(work, "packageSources", isPath: false);

		Assert.Equal(new[] { "Child", "Parent" }, entries.Select(e => e.Key).OrderBy(k => k).ToArray());
		Assert.Equal(Path.GetFullPath(parentFile), Path.GetFullPath(entries.Single(e => e.Key == "Parent").ConfigFile!));
		Assert.Equal(Path.GetFullPath(childFile), Path.GetFullPath(entries.Single(e => e.Key == "Child").ConfigFile!));
	}

	[Fact]
	public void AClearInTheNearerFileHidesTheFilesAbove()
	{
		Config(root, "    <clear />\n    <add key=\"Parent\" value=\"https://parent.example/v3/index.json\" />\n");
		var work = Path.Combine(root, "work");
		Config(work, "    <clear />\n    <add key=\"Only\" value=\"https://only.example/v3/index.json\" />\n");

		var entries = NuGetConfigHierarchy.Read(work, "packageSources", isPath: false);

		Assert.Equal("Only", Assert.Single(entries).Key);
	}

	[Fact]
	public void ARelativePathIsResolvedAgainstTheFileThatDefinesIt()
	{
		Config(root, "    <clear />\n    <add key=\"Feed\" value=\"local-feed\" />\n");
		var work = Path.Combine(root, "work");
		Directory.CreateDirectory(work);

		var entry = Assert.Single(NuGetConfigHierarchy.Read(work, "packageSources", isPath: true));

		Assert.Equal(Path.Combine(root, "local-feed"), Path.GetFullPath(entry.Value));
	}

	[Fact]
	public void OnlyEntriesNoOtherFileProvidesAreWrittenToTheSolutionFile()
	{
		Config(root, "    <clear />\n    <add key=\"Inherited\" value=\"https://parent.example/v3/index.json\" />\n");
		var work = Path.Combine(root, "work");
		var ownFile = Config(work, "    <add key=\"Own\" value=\"https://own.example/v3/index.json\" />\n");
		var effective = NuGetConfigHierarchy.Read(work, "packageSources", isPath: false);

		var toWrite = NuGetConfigHierarchy.OwnEntries(ownFile, effective, new[] {
			new KeyValuePair<string, string>("Inherited", "https://parent.example/v3/index.json"),
			new KeyValuePair<string, string>("Own", "https://own.example/v3/index.json"),
			new KeyValuePair<string, string>("Added", "https://added.example/v3/index.json"),
		});

		Assert.Equal(new[] { "Added", "Own" }, toWrite.Select(p => p.Key).OrderBy(k => k).ToArray());
	}

	[Fact]
	public void AnInheritedKeyWithADifferentValueIsAnOverrideAndIsWritten()
	{
		Config(root, "    <clear />\n    <add key=\"Feed\" value=\"https://parent.example/v3/index.json\" />\n");
		var work = Path.Combine(root, "work");
		var ownFile = Path.Combine(work, "NuGet.Config");
		Directory.CreateDirectory(work);
		var effective = NuGetConfigHierarchy.Read(work, "packageSources", isPath: false);

		var toWrite = NuGetConfigHierarchy.OwnEntries(ownFile, effective, new[] {
			new KeyValuePair<string, string>("Feed", "https://elsewhere.example/v3/index.json"),
		});

		Assert.Equal("https://elsewhere.example/v3/index.json", Assert.Single(toWrite).Value);
	}
}
