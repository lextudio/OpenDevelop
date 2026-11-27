using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using ICSharpCode.SharpDevelop;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Every string the Workspace Trust UI shows comes from StringResources*.resx. English is the neutral file;
/// Simplified and Traditional Chinese are maintained next to it.
/// </summary>
public sealed class WorkspaceTrustResourcesTests
{
	static readonly string[] Languages = {
		"StringResources.resx", "StringResources.zh-Hans.resx", "StringResources.zh-Hant.resx"
	};

	static readonly string[] SourceFiles = {
		"src/Main/Base/Project/Services/WorkspaceTrust/WorkspaceTrust.cs",
		"src/Main/Base/Project/Services/WorkspaceTrust/WorkspaceTrustPrompt.cs",
		"src/Main/Base/Project/Services/WorkspaceTrust/WorkspaceTrustWindow.cs",
		"src/Main/Base/Project/Src/Commands/WorkspaceTrustCommand.cs",
		"src/Main/SharpDevelop/Workbench/SDStatusBar.cs",
		"src/Main/Base/Project/ICSharpCode.SharpDevelop.addin",
	};

	[Fact]
	public void TheExtractionFindsTheKnownKeys()
	{
		// A regex that matched nothing would make the other tests pass vacuously.
		var keys = KeysUsedByTheUi();
		Assert.Contains("WorkspaceTrust.Prompt.Text", keys);
		Assert.Contains("WorkspaceTrust.Badge.ToolTip", keys);
		Assert.Contains("WorkspaceTrust.Denied.ExecuteBuild", keys);
		Assert.Contains("XML.MainMenu.ToolMenu.WorkspaceTrust", keys);
		Assert.True(keys.Count >= 25, "found " + keys.Count);
	}

	[Theory]
	[InlineData("StringResources.resx")]
	[InlineData("StringResources.zh-Hans.resx")]
	[InlineData("StringResources.zh-Hant.resx")]
	public void EveryKeyTheUiUses_ExistsAndIsNotEmpty(string language)
	{
		var resources = ReadResources(language);
		var missing = KeysUsedByTheUi().Where(key => !resources.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)).OrderBy(k => k).ToList();
		Assert.True(missing.Count == 0, language + " is missing: " + string.Join(", ", missing));
	}

	[Fact]
	public void EveryTranslation_HasTheSamePlaceholdersAsEnglish()
	{
		var english = ReadResources(Languages[0]);
		foreach (string language in Languages.Skip(1)) {
			var translated = ReadResources(language);
			foreach (string key in KeysUsedByTheUi())
				Assert.True(Placeholders(english[key]).SequenceEqual(Placeholders(translated[key])), key + " in " + language);
		}
	}

	[Fact]
	public void EveryDenialIsTranslatedForEveryCapability()
	{
		var resources = ReadResources("StringResources.zh-Hans.resx");
		foreach (var capability in Enum.GetValues<TrustCapability>().Where(c => c != TrustCapability.None))
			Assert.True(resources.ContainsKey("WorkspaceTrust.Denied." + capability), capability.ToString());
	}

	[Fact]
	public void TheEnglishFallbackIsUsedWithoutAResourceService()
	{
		SD.InitializeForUnitTests();
		try {
			Assert.Contains("Restricted Mode", WorkspaceTrust.DescribeDenial(TrustCapability.ExecuteBuild));
			Assert.Equal("fallback", WorkspaceTrust.Text("No.Such.Key", "fallback"));
		} finally {
			SD.TearDownForUnitTests();
		}
	}

	static List<string> Placeholders(string value) =>
		Regex.Matches(value, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();

	static HashSet<string> KeysUsedByTheUi()
	{
		string root = ProductCopyrightTests.RepositoryRoot();
		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (string file in SourceFiles) {
			string text = File.ReadAllText(Path.Combine(root, file));
			foreach (Match m in Regex.Matches(text, @"(?:WorkspaceTrust\.Text\(\s*|\$\{res:)""?((?:WorkspaceTrust|XML\.MainMenu|Global)\.[A-Za-z0-9_.]+)"))
				keys.Add(m.Groups[1].Value);
		}
		foreach (var capability in Enum.GetValues<TrustCapability>().Where(c => c != TrustCapability.None))
			keys.Add("WorkspaceTrust.Denied." + capability);
		keys.Add("WorkspaceTrust.Denied.Default");
		return keys;
	}

	static Dictionary<string, string> ReadResources(string fileName)
	{
		string path = Path.Combine(ProductCopyrightTests.RepositoryRoot(), "data", "resources", fileName);
		return XDocument.Load(path).Root!.Elements("data")
			.ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");
	}
}
