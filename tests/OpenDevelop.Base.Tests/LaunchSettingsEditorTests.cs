using System;
using System.IO;
using System.Text;
using System.Text.Json;

using ICSharpCode.SharpDevelop.Project;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Adding a launch profile edits the original text, so a user's comments and layout survive:
/// System.Text.Json drops comments when it writes a document back, and Newtonsoft.Json re-indents
/// the whole file, which would turn one added profile into a diff of the entire launchSettings.json.
/// </summary>
public sealed class LaunchSettingsEditorTests : IDisposable
{
	readonly string _directory;

	public LaunchSettingsEditorTests()
	{
		_directory = Path.Combine(Path.GetTempPath(), "od-lsedit-" + Guid.NewGuid().ToString("N"));
	}

	public void Dispose()
	{
		try { Directory.Delete(_directory, recursive: true); } catch { }
	}

	static string Profile(string name, string tfm, string indent, string nl = "\n", string unit = "  ")
		=> "\"" + name + "\": {" + nl
		   + indent + unit + "\"commandName\": \"Project\"," + nl
		   + indent + unit + "\"compatibleTargetFramework\": \"" + tfm + "\"" + nl
		   + indent + "}";

	[Fact]
	public void NoFile_CreatesOneWithTheSchemaAndTheProfile()
	{
		string text = LaunchSettingsEditor.AddProfile(null, "App (iOS)", "ios");

		Assert.Equal("{\n  \"$schema\": \"http://json.schemastore.org/launchsettings.json\",\n  \"profiles\": {\n    "
			+ Profile("App (iOS)", "ios", "    ") + "\n  }\n}\n", text);
	}

	[Fact]
	public void AppendsAfterTheLastProfile_AndLeavesEverythingElseByteForByte()
	{
		string original = "{\n"
			+ "  // the team's profiles\n"
			+ "  \"iisSettings\": { \"sslPort\": 0 },\n"
			+ "  \"profiles\": {\n"
			+ "    \"App (Desktop)\": {\n"
			+ "      \"commandName\": \"Project\", /* keep */\n"
			+ "      \"compatibleTargetFramework\": \"desktop\"\n"
			+ "    },\n"
			+ "    \"App (WSL2)\": { \"commandName\": \"WSL2\", \"distributionName\": \"\" }\n"
			+ "  } // end of profiles\n"
			+ "}\n";

		string text = LaunchSettingsEditor.AddProfile(original, "App (Android)", "android");

		string insertion = ",\n    " + Profile("App (Android)", "android", "    ");
		int at = original.IndexOf("\"distributionName\": \"\" }", StringComparison.Ordinal) + "\"distributionName\": \"\" }".Length;
		Assert.Equal(original.Insert(at, insertion), text);
		AssertParsesWithProfiles(text, "App (Desktop)", "App (WSL2)", "App (Android)");
	}

	[Fact]
	public void KeepsATrailingComma_AndTheCommentsAfterTheLastProfile()
	{
		string original = "{\n  \"profiles\": {\n    \"A\": { \"commandName\": \"Project\" }, // last\n  }\n}\n";

		string text = LaunchSettingsEditor.AddProfile(original, "B", "desktop");

		string insertion = ",\n    " + Profile("B", "desktop", "    ");
		Assert.Equal(original.Insert(original.IndexOf("}, // last", StringComparison.Ordinal) + 1, insertion), text);
		AssertParsesWithProfiles(text, "A", "B");
	}

	[Theory]
	[InlineData("{\n  \"profiles\": {}\n}\n")]
	[InlineData("{\n  \"profiles\": {\n  }\n}\n")]
	[InlineData("{ \"profiles\": { } }")]
	public void EmptyProfiles_GetTheFirstProfile(string original)
	{
		string text = LaunchSettingsEditor.AddProfile(original, "A", "desktop");

		AssertParsesWithProfiles(text, "A");
		Assert.Contains("\"compatibleTargetFramework\": \"desktop\"", text);
	}

	[Fact]
	public void EmptyProfilesHoldingOnlyAComment_KeepTheComment()
	{
		string text = LaunchSettingsEditor.AddProfile("{ \"profiles\": { /* none yet */ } }", "A", "desktop");

		Assert.Contains("/* none yet */", text);
		AssertParsesWithProfiles(text, "A");
	}

	[Fact]
	public void MatchesTheFilesCrLfLineEndingsAndTabIndent()
	{
		string original = "{\r\n\t\"profiles\": {\r\n\t\t\"A\": { \"commandName\": \"Project\" }\r\n\t}\r\n}\r\n";

		string text = LaunchSettingsEditor.AddProfile(original, "B", "ios");

		Assert.Equal(original.Insert(original.IndexOf("}\r\n\t}", StringComparison.Ordinal) + 1,
			",\r\n\t\t" + Profile("B", "ios", "\t\t", "\r\n", "\t")), text);
		Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
	}

	[Theory]
	[InlineData("// the team's launch settings\n{ \"profiles\": {} }")]
	[InlineData("/* a */ // b\n{ \"profiles\": {} }")]
	[InlineData("{ \"profiles\": /* c */ // d\n {} }")]
	[InlineData("{ \"profiles\": { \"A\": /* c */ { \"commandName\": \"Project\" } } }")]
	public void CommentsBeforeTheObjectsAreNotMistakenForOtherJson(string original)
	{
		string text = LaunchSettingsEditor.AddProfile(original, "B", "desktop");

		AssertParsesWithProfiles(text, "B");
		Assert.Contains(original.Substring(0, 5), text);
	}

	[Fact]
	public void ABomCharacterInTheText_IsKept()
	{
		string text = LaunchSettingsEditor.AddProfile("\uFEFF{ \"profiles\": {} }", "A", "desktop");

		Assert.StartsWith("\uFEFF{", text);
		AssertParsesWithProfiles(text.Substring(1), "A");
	}

	[Fact]
	public void QuotesANameThatNeedsEscaping()
	{
		string text = LaunchSettingsEditor.AddProfile("{ \"profiles\": {} }", "My \"App\" \\ (é)", null);

		using var document = JsonDocument.Parse(text);
		Assert.True(document.RootElement.GetProperty("profiles").TryGetProperty("My \"App\" \\ (é)", out var profile));
		Assert.False(profile.TryGetProperty("compatibleTargetFramework", out _));
	}

	[Theory]
	[InlineData("{ \"profiles\": { \"A\": { \"commandName\": \"Project\" } } }", "A", "already has a profile named 'A'")]
	[InlineData("{ \"other\": 1 }", "B", "no \"profiles\" object")]
	[InlineData("{ \"profiles\": [] }", "B", "is not an object")]
	[InlineData("[ 1 ]", "B", "not a JSON object")]
	[InlineData("{ \"profiles\": { \"A\": ", "B", "not valid JSON")]
	[InlineData("{ \"profiles\": { \"A\": {} } } }", "B", "not valid JSON")]
	[InlineData("{ \"profiles\": { \"A\": {} }", "B", "not valid JSON")]
	[InlineData("{ \"profiles\": {} } trailing", "B", "not valid JSON")]
	public void RefusesAFileItCannotEditSafely(string original, string name, string expectedMessage)
	{
		var ex = Assert.Throws<LaunchSettingsEditException>(() => LaunchSettingsEditor.AddProfile(original, name, "desktop"));

		Assert.Contains(expectedMessage, ex.Message);
	}

	[Fact]
	public void File_IsCreatedWithItsDirectory()
	{
		string path = LaunchSettingsProfile.GetPath(_directory);

		LaunchSettingsEditor.AddProfileToFile(path, "A", "desktop");

		AssertParsesWithProfiles(File.ReadAllText(path), "A");
	}

	[Fact]
	public void File_KeepsItsByteOrderMark()
	{
		string path = LaunchSettingsProfile.GetPath(_directory);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "{ \"profiles\": {} }", new UTF8Encoding(true));

		LaunchSettingsEditor.AddProfileToFile(path, "A", "desktop");

		byte[] bytes = File.ReadAllBytes(path);
		Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{' }, bytes[..4]);
		AssertParsesWithProfiles(File.ReadAllText(path), "A");
	}

	[Fact]
	public void File_IsLeftUntouchedWhenTheEditIsRefused()
	{
		string path = LaunchSettingsProfile.GetPath(_directory);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		const string broken = "{ \"profiles\": { \"A\": ";
		File.WriteAllText(path, broken);

		Assert.Throws<LaunchSettingsEditException>(() => LaunchSettingsEditor.AddProfileToFile(path, "B", "desktop"));

		Assert.Equal(broken, File.ReadAllText(path));
	}

	static void AssertParsesWithProfiles(string text, params string[] names)
	{
		using var document = JsonDocument.Parse(text, new JsonDocumentOptions {
			CommentHandling = JsonCommentHandling.Skip,
			AllowTrailingCommas = true
		});
		var profiles = document.RootElement.GetProperty("profiles");
		foreach (string name in names)
			Assert.True(profiles.TryGetProperty(name, out _), "profile " + name + " is present in:\n" + text);
	}
}
