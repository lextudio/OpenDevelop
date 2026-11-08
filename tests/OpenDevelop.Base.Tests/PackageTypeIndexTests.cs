using ICSharpCode.PackageManagement;
using NuGet.Frameworks;
using Xunit;

namespace OpenDevelop.Base.Tests;

// Which package declares a bare type name, read from the packages in a (here fabricated) global packages
// folder. Real assemblies stand in for package contents: NuGet.Versioning declares NuGetVersion,
// NuGet.Frameworks declares NuGetFramework.
public sealed class PackageTypeIndexTests : IDisposable
{
	readonly string packagesFolder = Directory.CreateTempSubdirectory("package-type-index-").FullName;

	public void Dispose() => Directory.Delete(packagesFolder, recursive: true);

	static string AssemblyOf(Type type) => type.Assembly.Location;

	void Install(string id, string version, string group, string assemblyPath, string folder = "lib")
	{
		var directory = Path.Combine(packagesFolder, id.ToLowerInvariant(), version, folder, group);
		Directory.CreateDirectory(directory);
		File.Copy(assemblyPath, Path.Combine(directory, Path.GetFileName(assemblyPath)));
	}

	static readonly NuGetFramework Net8 = NuGetFramework.Parse("net8.0");

	[Fact]
	public void FindsThePackageThatDeclaresATypeAndItsNamespace()
	{
		Install("Versioning.Pack", "6.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));

		var match = Assert.Single(PackageTypeIndex.Find("NuGetVersion", new Dictionary<string, string> { ["Versioning.Pack"] = "6.0.0" }, packagesFolder, Net8));

		Assert.Equal("Versioning.Pack", match.PackageId);
		Assert.Equal("NuGet.Versioning", match.Namespace);
		Assert.Equal("NuGetVersion", match.Name);
	}

	[Fact]
	public void ANameNoKnownPackageDeclaresFindsNothing()
	{
		Install("Versioning.Pack", "6.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));

		Assert.Empty(PackageTypeIndex.Find("JsonConvert", new Dictionary<string, string> { ["Versioning.Pack"] = "6.0.0" }, packagesFolder, Net8));
	}

	[Fact]
	public void OnlyPublicTopLevelTypesCount()
	{
		Install("Versioning.Pack", "6.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));
		var known = new Dictionary<string, string> { ["Versioning.Pack"] = "6.0.0" };

		// <Module> and compiler-generated types are not public, and a nested type has a declaring type.
		var types = PackageTypeIndex.TypesOf(AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));
		Assert.Contains(("NuGet.Versioning", "NuGetVersion"), types);
		Assert.DoesNotContain(types, type => type.Name == "<Module>" || type.Name.StartsWith("<") || type.Name.Contains('+'));
		Assert.Empty(PackageTypeIndex.Find("<Module>", known, packagesFolder, Net8));
	}

	[Fact]
	public void GenericArityIsIgnoredAndTheMatchIsCaseSensitive()
	{
		var types = PackageTypeIndex.TypesOf(AssemblyOf(typeof(List<int>)));

		Assert.Contains(("System.Collections.Generic", "List"), types);
		Assert.DoesNotContain(("System.Collections.Generic", "list"), types);
	}

	[Fact]
	public void TheGroupNearestTheProjectsFrameworkIsRead()
	{
		// NuGetFramework lives only in the netstandard2.0 group, NuGetVersion only in the net8.0 one.
		Install("Both.Pack", "1.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));
		Install("Both.Pack", "1.0.0", "netstandard2.0", AssemblyOf(typeof(NuGet.Frameworks.NuGetFramework)));
		var known = new Dictionary<string, string> { ["Both.Pack"] = "1.0.0" };

		Assert.Single(PackageTypeIndex.Find("NuGetVersion", known, packagesFolder, Net8));
		Assert.Empty(PackageTypeIndex.Find("NuGetFramework", known, packagesFolder, Net8));
		Assert.Single(PackageTypeIndex.Find("NuGetFramework", known, packagesFolder, NuGetFramework.Parse("netstandard2.0")));
	}

	[Fact]
	public void ARefGroupIsPreferredOverLib()
	{
		Install("Ref.Pack", "1.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)), folder: "ref");
		Install("Ref.Pack", "1.0.0", "net8.0", AssemblyOf(typeof(NuGet.Frameworks.NuGetFramework)), folder: "lib");
		var known = new Dictionary<string, string> { ["Ref.Pack"] = "1.0.0" };

		Assert.Single(PackageTypeIndex.Find("NuGetVersion", known, packagesFolder, Net8));
		Assert.Empty(PackageTypeIndex.Find("NuGetFramework", known, packagesFolder, Net8));
	}

	[Theory]
	[InlineData("[5.0,7.0)")]
	[InlineData("6.*")]
	[InlineData("5.0.0")]   // a lower bound: NuGet picks the nearest installed version that satisfies it
	public void AVersionRangeResolvesToTheBestInstalledVersion(string range)
	{
		Install("Range.Pack", "6.1.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));

		Assert.Single(PackageTypeIndex.Find("NuGetVersion", new Dictionary<string, string> { ["Range.Pack"] = range }, packagesFolder, Net8));
	}

	[Fact]
	public void APackageThatWasNeverRestoredIsSkippedAndTheLimitIsRespected()
	{
		Install("First.Pack", "1.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));
		Install("Second.Pack", "1.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));
		Install("Third.Pack", "1.0.0", "net8.0", AssemblyOf(typeof(NuGet.Versioning.NuGetVersion)));
		var known = new Dictionary<string, string> { ["Missing.Pack"] = "1.0.0", ["First.Pack"] = "1.0.0", ["Second.Pack"] = "1.0.0", ["Third.Pack"] = "1.0.0" };

		var matches = PackageTypeIndex.Find("NuGetVersion", known, packagesFolder, Net8, maxPackages: 2);

		Assert.Equal(new[] { "First.Pack", "Second.Pack" }, matches.Select(m => m.PackageId).ToArray());
	}
}
