using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ResourceReferenceEntriesTests
{
	[Fact]
	public void EntriesListNoneThenStaticThenDynamicForms()
	{
		Assert.Equal(new[] { "(none)", "{StaticResource A}", "{StaticResource B}", "{DynamicResource A}", "{DynamicResource B}" },
			ResourceReferenceEntries.Entries(new[] { "A", "B" }));
		Assert.Equal(new[] { "(none)" }, ResourceReferenceEntries.Entries(System.Array.Empty<string>()));
	}

	[Theory]
	[InlineData("(none)", true, "", "")]
	[InlineData("{StaticResource Primary}", true, "Static", "Primary")]
	[InlineData("{DynamicResource My Key}", true, "Dynamic", "My Key")]
	[InlineData("{StaticResource }", false, "", "")]
	[InlineData("{Binding Path}", false, "", "")]
	[InlineData("Primary", false, "", "")]
	[InlineData(null, false, "", "")]
	public void ParseIsTheInverseOfEntriesAndRejectsOtherText(string? entry, bool ok, string kind, string key)
	{
		Assert.Equal((ok, kind, key), (ResourceReferenceEntries.TryParse(entry, out var parsedKind, out var parsedKey), parsedKind, parsedKey));
	}

	[Fact]
	public void EveryEntryParsesBackToItsKindAndKey()
	{
		foreach (var entry in ResourceReferenceEntries.Entries(new[] { "Accent", "Base" })) {
			Assert.True(ResourceReferenceEntries.TryParse(entry, out var kind, out var key));
			if (entry == ResourceReferenceEntries.NoneEntry)
				Assert.Equal(("", ""), (kind, key));
			else
				Assert.Equal(entry, "{" + kind + "Resource " + key + "}");
		}
	}
}
