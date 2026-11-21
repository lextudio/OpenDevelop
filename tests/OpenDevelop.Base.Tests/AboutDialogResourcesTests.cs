using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// ResourceService.GetString throws ResourceNotFoundException for a key the active language does not
/// have, and the About box reads its strings while it is being constructed, so one missing or
/// misspelled key would crash the dialog. These tests read the keys from the dialog's own sources.
/// Only English, Simplified and Traditional Chinese are maintained for this dialog.
/// </summary>
public sealed class AboutDialogResourcesTests
{
	static readonly string[] Languages = {
		"StringResources.resx", "StringResources.zh-Hans.resx", "StringResources.zh-Hant.resx"
	};

	const string DialogDirectory = "src/Main/Base/Project/Src/Gui/Dialogs/";

	[Fact]
	public void DialogSourcesUseAtLeastTheKnownKeys()
	{
		// Guards the extraction below: a regex that silently matched nothing would make the other tests pass vacuously.
		var keys = KeysUsedByTheDialog();
		Assert.Contains("AboutDialog.Title", keys);
		Assert.Contains("AboutDialog.Description", keys);
		Assert.Contains("AboutDialog.GcHeapMemory", keys);
		Assert.Contains("AboutDialog.VersionLine", keys);
		Assert.Contains("Global.OKButtonText", keys);
		Assert.True(keys.Count >= 18, "Expected the dialog to use at least 18 resource keys, found " + keys.Count);
	}

	[Theory]
	[InlineData("StringResources.resx")]
	[InlineData("StringResources.zh-Hans.resx")]
	[InlineData("StringResources.zh-Hant.resx")]
	public void EveryKeyTheDialogUses_ExistsInTheLanguage(string language)
	{
		var resources = ReadResources(language);
		var missing = KeysUsedByTheDialog().Where(key => !resources.ContainsKey(key)).OrderBy(key => key).ToList();
		Assert.True(missing.Count == 0, language + " is missing: " + string.Join(", ", missing));
	}

	[Fact]
	public void EveryTranslation_HasTheSamePlaceholdersAsEnglish()
	{
		var english = ReadResources(Languages[0]);
		foreach (string language in Languages.Skip(1)) {
			var translated = ReadResources(language);
			foreach (string key in KeysUsedByTheDialog()) {
				Assert.Equal(Placeholders(english[key]), Placeholders(translated[key]));
			}
		}
	}

	[Fact]
	public void EveryTranslation_IsNotEmpty()
	{
		foreach (string language in Languages) {
			var resources = ReadResources(language);
			foreach (string key in KeysUsedByTheDialog())
				Assert.False(string.IsNullOrWhiteSpace(resources[key]), key + " is empty in " + language);
		}
	}

	static List<string> Placeholders(string value)
	{
		return Regex.Matches(value, @"\{(\d+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
	}

	static HashSet<string> KeysUsedByTheDialog()
	{
		string root = ProductCopyrightTests.RepositoryRoot();
		string xaml = File.ReadAllText(Path.Combine(root, DialogDirectory + "AboutDialog.xaml"));
		string code = File.ReadAllText(Path.Combine(root, DialogDirectory + "AboutDialog.xaml.cs"));

		var keys = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match m in Regex.Matches(xaml, @"\{core:Localize\s+([A-Za-z0-9_.]+)\s*\}"))
			keys.Add(m.Groups[1].Value);
		// The code passes keys through small helpers (Row, Format), so match key-shaped string literals, not one call form.
		foreach (Match m in Regex.Matches(code, @"""((?:AboutDialog|Global)\.[A-Za-z0-9_.]+)"""))
			keys.Add(m.Groups[1].Value);
		return keys;
	}

	static Dictionary<string, string> ReadResources(string fileName)
	{
		string path = Path.Combine(ProductCopyrightTests.RepositoryRoot(), "data", "resources", fileName);
		var document = XDocument.Load(path);
		return document.Root!.Elements("data")
			.ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");
	}
}
