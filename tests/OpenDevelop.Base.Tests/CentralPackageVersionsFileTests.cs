using System.Xml.Linq;
using ICSharpCode.PackageManagement;
using Xunit;

namespace OpenDevelop.Base.Tests;

// The Central Package Management install path edits Directory.Packages.props by hand (XDocument, layout
// preserved). These cover the branches the IDE journey does not: updating, appending, creating an
// ItemGroup, and not touching another configuration's conditional pin.
public sealed class CentralPackageVersionsFileTests : IDisposable
{
	readonly string directory = Directory.CreateTempSubdirectory("cpm-props-").FullName;

	public void Dispose() => Directory.Delete(directory, recursive: true);

	string Write(string content)
	{
		var path = Path.Combine(directory, "Directory.Packages.props");
		File.WriteAllText(path, content.Replace("\r\n", "\n"));
		return path;
	}

	static string Normalize(string text) => text.Replace("\r\n", "\n");

	static IReadOnlyList<(string Include, string Version, bool Conditional)> Versions(string path) =>
		XDocument.Load(path).Descendants("PackageVersion")
			.Select(e => (
				(string)(e.Attribute("Include") ?? e.Attribute("Update")),
				(string)e.Attribute("Version"),
				e.Attribute("Condition") != null || e.Parent.Attribute("Condition") != null))
			.ToList();

	[Fact]
	public void UpdatesAnExistingEntryInPlace()
	{
		var path = Write("<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\n    <PackageVersion Include=\"Other\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");

		CentralPackageVersionsFile.Set(path, "serilog", "3.1.0");

		Assert.Equal(
			"<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"3.1.0\" />\n    <PackageVersion Include=\"Other\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n",
			Normalize(File.ReadAllText(path)));
	}

	[Fact]
	public void AppendsAfterTheLastEntryWithItsIndentation()
	{
		var path = Write("<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\n  </ItemGroup>\n</Project>\n");

		CentralPackageVersionsFile.Set(path, "Newtonsoft.Json", "13.0.3");

		Assert.Equal(
			"<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n  </ItemGroup>\n</Project>\n",
			Normalize(File.ReadAllText(path)));
	}

	[Fact]
	public void CreatesAnItemGroupWhenThereIsNoPackageVersion()
	{
		var path = Write("<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n");

		CentralPackageVersionsFile.Set(path, "Serilog", "3.1.0");

		var versions = Versions(path);
		Assert.Equal(("Serilog", "3.1.0", false), Assert.Single(versions));
		Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", File.ReadAllText(path));
	}

	[Fact]
	public void LeavesAConditionalPinAloneAndAddsAnUnconditionalEntry()
	{
		var path = Write(
			"<Project>\n  <ItemGroup Condition=\"'$(TargetFramework)' == 'net8.0'\">\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\n  </ItemGroup>\n</Project>\n");

		CentralPackageVersionsFile.Set(path, "Serilog", "3.1.0");

		var versions = Versions(path);
		Assert.Contains(("Serilog", "2.0.0", true), versions);   // the net8.0 pin is untouched
		Assert.Contains(("Serilog", "3.1.0", false), versions);  // the new default is unconditional
		Assert.Equal(2, versions.Count);
	}

	[Fact]
	public void UpdatesTheUnconditionalEntryEvenWhenAConditionalOneComesFirst()
	{
		var path = Write(
			"<Project>\n  <ItemGroup Condition=\"'$(TargetFramework)' == 'net8.0'\">\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\n  </ItemGroup>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"2.5.0\" />\n  </ItemGroup>\n</Project>\n");

		CentralPackageVersionsFile.Set(path, "Serilog", "3.1.0");

		var versions = Versions(path);
		Assert.Contains(("Serilog", "2.0.0", true), versions);
		Assert.Contains(("Serilog", "3.1.0", false), versions);
		Assert.DoesNotContain(("Serilog", "2.5.0", false), versions);
	}

	[Fact]
	public void HonoursAnUpdateAttribute()
	{
		var path = Write("<Project>\n  <ItemGroup>\n    <PackageVersion Update=\"Serilog\" Version=\"2.0.0\" />\n  </ItemGroup>\n</Project>\n");

		CentralPackageVersionsFile.Set(path, "Serilog", "3.1.0");

		Assert.Equal(("Serilog", "3.1.0", false), Assert.Single(Versions(path)));
	}

	[Fact]
	public void KeepsCrLfLineEndingsAndAByteOrderMark()
	{
		var path = Path.Combine(directory, "Directory.Packages.props");
		var text = "<Project>\r\n  <ItemGroup>\r\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\r\n  </ItemGroup>\r\n</Project>\r\n";
		File.WriteAllBytes(path, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
			.Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());

		CentralPackageVersionsFile.Set(path, "Other", "1.0.0");

		var bytes = File.ReadAllBytes(path);
		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
		var result = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
		Assert.Equal(
			"<Project>\r\n  <ItemGroup>\r\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\r\n    <PackageVersion Include=\"Other\" Version=\"1.0.0\" />\r\n  </ItemGroup>\r\n</Project>\r\n",
			result);
	}

	[Fact]
	public void KeepsAnExistingXmlDeclaration()
	{
		var path = Write("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"Serilog\" Version=\"2.0.0\" />\n  </ItemGroup>\n</Project>\n");

		CentralPackageVersionsFile.Set(path, "Serilog", "3.1.0");

		Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", File.ReadAllText(path));
	}

	[Fact]
	public void RejectsAMalformedFileWithoutWritingIt()
	{
		var path = Write("<Project><ItemGroup></Project>");
		var before = File.ReadAllText(path);

		Assert.ThrowsAny<Exception>(() => CentralPackageVersionsFile.Set(path, "Serilog", "3.1.0"));

		Assert.Equal(before, File.ReadAllText(path));
	}
}
