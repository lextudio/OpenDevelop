using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The XSG fixture (tests/fixtures/XsgLocalFeedFixture) is compiled only by the XAML source generator, from XAML in both styles the generator serves in a
/// project that enabled it: old style (xmlns declared: MainWindow, SecondWindow) and new style, Simpler XAML (no xmlns at all: ThirdWindow). Both go
/// through the same generator, so every window must have its generated InitializeComponent, and the new-style one its typed x:Name field. The
/// fixture is built by the developer (dotnet build tests/fixtures/XsgLocalFeedFixture -c Debug); CI does not build it, so the test skips where it is not built.
/// </summary>
public sealed class XsgFixtureCompilationTests
{
	static string FixtureAssembly()
	{
		var path = Path.Combine(ProductCopyrightTests.RepositoryRoot(), "tests", "fixtures", "XsgLocalFeedFixture", "bin", "Debug", "net10.0-windows", "XsgLocalFeedFixture.dll");
		if (!File.Exists(path))
			Assert.Skip("Build tests/fixtures/XsgLocalFeedFixture (Debug) first: " + path);
		return path;
	}

	/// <summary>The methods and fields of each type, read from metadata only (the fixture's WPF references are not loaded).</summary>
	static Dictionary<string, (HashSet<string> Methods, HashSet<string> Fields)> Members()
	{
		using var stream = File.OpenRead(FixtureAssembly());
		using var pe = new PEReader(stream);
		var reader = pe.GetMetadataReader();
		var types = new Dictionary<string, (HashSet<string>, HashSet<string>)>();
		foreach (var handle in reader.TypeDefinitions)
		{
			var type = reader.GetTypeDefinition(handle);
			var name = reader.GetString(type.Name);
			types[name] = (type.GetMethods().Select(m => reader.GetString(reader.GetMethodDefinition(m).Name)).ToHashSet(),
				type.GetFields().Select(f => reader.GetString(reader.GetFieldDefinition(f).Name)).ToHashSet());
		}
		return types;
	}

	[Theory]
	[InlineData("MainWindow")]
	[InlineData("SecondWindow")]
	[InlineData("ThirdWindow")]
	public void EveryWindowOfTheFixtureHasItsGeneratedInitializeComponent(string window)
	{
		var members = Members();

		Assert.True(members.ContainsKey(window), window + " is not in the fixture assembly");
		Assert.Contains("InitializeComponent", members[window].Methods);
	}

	[Fact]
	public void TheNewStyleWindowHasItsTypedNameField()
	{
		// ThirdWindow.xaml declares no namespaces at all; the generator supplied them, so x:Name still became a field.
		Assert.Contains("Greeting", Members()["ThirdWindow"].Fields);
	}

	[Fact]
	public void TheOldStyleWindowsKeepTheirTypedNameFields()
	{
		Assert.Contains("Status", Members()["MainWindow"].Fields);
	}
}
