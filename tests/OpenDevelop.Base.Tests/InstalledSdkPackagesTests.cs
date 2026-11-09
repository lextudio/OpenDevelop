using ICSharpCode.PackageManagement;
using Xunit;

namespace OpenDevelop.Base.Tests;

// What the Installed tab shows for an SDK-style project: the project's PackageReference versions, the details from
// the nuspec in the global packages folder (read by NuGet's PackageFolderReader), and a placeholder when the package
// has not been restored.
public sealed class InstalledSdkPackagesTests : IDisposable
{
	readonly string packagesFolder = Directory.CreateTempSubdirectory("installed-sdk-packages-").FullName;

	public void Dispose() => Directory.Delete(packagesFolder, recursive: true);

	void Restored(string id, string version, string nuspecBody)
	{
		var directory = Path.Combine(packagesFolder, id.ToLowerInvariant(), version);
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, id.ToLowerInvariant() + ".nuspec"),
			"<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>" + id + "</id><version>" + version + "</version>" + nuspecBody + "</metadata></package>");
	}

	static Dictionary<string, string> References(params (string Id, string Version)[] items) =>
		items.ToDictionary(i => i.Id, i => i.Version, StringComparer.OrdinalIgnoreCase);

	[Fact]
	public void DetailsComeFromTheNuspecInThePackagesFolder()
	{
		Restored("My.Pack", "1.2.3",
			"<authors>Ann, Bob</authors><title>My Pack</title><summary>Short</summary><description>Long text</description><projectUrl>https://example.org/p</projectUrl>" +
			"<dependencies><group targetFramework=\"net8.0\"><dependency id=\"Dep.One\" version=\"1.0.0\" /></group></dependencies>");

		var package = Assert.Single(InstalledSdkPackages.Read(References(("My.Pack", "1.2.3")), packagesFolder));

		Assert.Equal("My.Pack", package.Id);
		Assert.Equal("1.2.3", package.Version.ToString());
		Assert.Equal("My Pack", package.Title);
		Assert.Equal("Short", package.Summary);
		Assert.Equal("Long text", package.Description);
		Assert.Equal(new[] { "Ann", "Bob" }, package.Authors);
		Assert.Equal("https://example.org/p", package.ProjectUrl.ToString());
		Assert.Equal("Dep.One", package.DependencySets.SelectMany(set => set.Dependencies).Single().Id);
	}

	[Fact]
	public void TheTitleDefaultsToTheIdWhenTheNuspecHasNone()
	{
		Restored("No.Title", "1.0.0", "<authors>A</authors><description>d</description>");

		Assert.Equal("No.Title", Assert.Single(InstalledSdkPackages.Read(References(("No.Title", "1.0.0")), packagesFolder)).Title);
	}

	[Fact]
	public void APackageThatWasNotRestoredIsShownByIdAndVersionWithAPlaceholder()
	{
		var package = Assert.Single(InstalledSdkPackages.Read(References(("Not.Restored", "2.0.0")), packagesFolder));

		Assert.Equal("Not.Restored", package.Id);
		Assert.Equal("2.0.0", package.Version.ToString());
		Assert.StartsWith("Not restored yet", package.Description);
	}

	[Fact]
	public void NoPackagesFolderMeansEveryPackageIsAPlaceholder()
	{
		var package = Assert.Single(InstalledSdkPackages.Read(References(("A.Pack", "1.0.0")), null));

		Assert.StartsWith("Not restored yet", package.Description);
	}

	[Fact]
	public void ARangeIsShownByItsLowerBoundWhenNotRestored()
	{
		var package = Assert.Single(InstalledSdkPackages.Read(References(("Ranged.Pack", "[1.5.0, 2.0.0)")), packagesFolder));

		Assert.Equal("1.5.0", package.Version.ToString());
	}

	[Fact]
	public void ARangeIsResolvedToTheInstalledVersionWhenRestored()
	{
		Restored("Ranged.Pack", "1.7.0", "<authors>A</authors><description>d</description>");

		Assert.Equal("1.7.0", Assert.Single(InstalledSdkPackages.Read(References(("Ranged.Pack", "[1.5.0, 2.0.0)")), packagesFolder)).Version.ToString());
	}

	[Fact]
	public void AVersionThatIsNeitherAVersionNorARangeIsSkipped()
	{
		Assert.Empty(InstalledSdkPackages.Read(References(("Odd.Pack", "$(SomeVersion)")), packagesFolder));
	}

	[Fact]
	public void PackagesAreListedById()
	{
		var packages = InstalledSdkPackages.Read(References(("Zeta", "1.0.0"), ("alpha", "1.0.0"), ("Mid", "1.0.0")), packagesFolder);

		Assert.Equal(new[] { "alpha", "Mid", "Zeta" }, packages.Select(p => p.Id).ToArray());
	}
}
