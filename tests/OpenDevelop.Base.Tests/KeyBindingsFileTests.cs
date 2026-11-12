using ICSharpCode.Core;
using ICSharpCode.Core.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>KeyBindings.xml: the user's overrides of keyboard shortcuts, and the command keys they are filed under (doc/technotes/keyboard-shortcuts.md).</summary>
public sealed class KeyBindingsFileTests : IDisposable
{
	readonly string directory = Path.Combine(Path.GetTempPath(), "od-keybindings-" + Guid.NewGuid().ToString("N"));
	string FilePath => Path.Combine(directory, "KeyBindings.xml");

	public KeyBindingsFileTests() => Directory.CreateDirectory(directory);
	public void Dispose() => Directory.Delete(directory, true);

	[Fact]
	public void AMissingFile_IsAnEmptyOne()
	{
		var file = KeyBindingsFile.Load(FilePath);

		Assert.Empty(file.Overrides);
		Assert.Equal(KeyBindingsFile.CurrentVersion, file.Version);
		Assert.False(file.IsFromNewerVersion);
	}

	[Fact]
	public void OverridesRoundTrip_IncludingTheExplicitNoShortcut()
	{
		var file = new KeyBindingsFile();
		file.Set("class:Foo.Clean", "Ctrl+Shift+F8");
		file.Set("class:Foo.Save", "");
		file.Save(FilePath);

		var read = KeyBindingsFile.Load(FilePath).Overrides;

		Assert.Equal("Ctrl+Shift+F8", read["class:Foo.Clean"]);
		Assert.True(read.ContainsKey("class:Foo.Save"));
		Assert.Equal("", read["class:Foo.Save"]);
	}

	[Fact]
	public void SetReplacesAnExistingEntry_AndFoldsDuplicates()
	{
		File.WriteAllText(FilePath, "<KeyBindings version='1'><Command key='k' gesture='F1'/><Command key='k' gesture='F2'/></KeyBindings>");
		var file = KeyBindingsFile.Load(FilePath);
		Assert.Equal("F2", file.Overrides["k"]);

		file.Set("k", "F3");
		file.Save(FilePath);

		var xml = System.Xml.Linq.XDocument.Load(FilePath);
		Assert.Equal("F3", Assert.Single(xml.Root!.Elements("Command")).Attribute("gesture")!.Value);
	}

	[Fact]
	public void WhatThisVersionDoesNotUnderstand_IsWrittenBackAsItWas()
	{
		File.WriteAllText(FilePath,
			"<KeyBindings version='2' extra='x'><Command key='k' gesture='F1' scope='Editor'/><Scheme name='mine'><Chord/></Scheme></KeyBindings>");
		var messages = new List<string>();
		var file = KeyBindingsFile.Load(FilePath, messages.Add);
		Assert.True(file.IsFromNewerVersion);
		Assert.Single(messages);

		file.Set("other", "F5");
		file.Save(FilePath);

		var root = System.Xml.Linq.XDocument.Load(FilePath).Root!;
		Assert.Equal("2", root.Attribute("version")!.Value);
		Assert.Equal("x", root.Attribute("extra")!.Value);
		Assert.Equal("Editor", root.Elements("Command").First().Attribute("scope")!.Value);
		Assert.NotNull(root.Element("Scheme")!.Element("Chord"));
		Assert.Equal(2, root.Elements("Command").Count());
	}

	[Fact]
	public void RemoveAndClear_LeaveWhatIsNotAnOverride()
	{
		File.WriteAllText(FilePath, "<KeyBindings version='1'><Command key='a' gesture='F1'/><Command key='b' gesture='F2'/><Note>keep</Note></KeyBindings>");
		var file = KeyBindingsFile.Load(FilePath);

		Assert.True(file.Remove("a"));
		Assert.False(file.Remove("a"));
		Assert.Equal(new[] { "b" }, file.Overrides.Keys);
		file.Clear();
		file.Save(FilePath);

		var root = System.Xml.Linq.XDocument.Load(FilePath).Root!;
		Assert.Empty(root.Elements("Command"));
		Assert.Equal("keep", root.Element("Note")!.Value);
	}

	[Fact]
	public void AFileThatCannotBeRead_IsReportedTreatedAsEmpty_AndKeptWhenItIsOverwritten()
	{
		File.WriteAllText(FilePath, "<KeyBindings><Command key='a' gesture='F1'");
		var messages = new List<string>();

		var file = KeyBindingsFile.Load(FilePath, messages.Add);
		Assert.Empty(file.Overrides);
		Assert.Contains("could not be read", Assert.Single(messages));
		file.Set("a", "F2");
		file.Save(FilePath);

		Assert.Equal("F2", KeyBindingsFile.Load(FilePath).Overrides["a"]);
		Assert.Contains("<KeyBindings><Command key='a' gesture='F1'", File.ReadAllText(FilePath + ".corrupt"));
	}

	[Fact]
	public void AnEntryWithoutAKeyOrAGesture_IsNotAnOverride()
	{
		File.WriteAllText(FilePath, "<KeyBindings version='1'><Command gesture='F1'/><Command key='a'/><Command key=' ' gesture='F2'/><Command key='ok' gesture='F3'/></KeyBindings>");

		Assert.Equal(new[] { "ok" }, KeyBindingsFile.Load(FilePath).Overrides.Keys);
	}

	[Fact]
	public void SavingLeavesNoTemporaryFileBehind()
	{
		var file = new KeyBindingsFile();
		file.Set("a", "F1");
		file.Save(FilePath);
		file.Set("a", "F2");
		file.Save(FilePath);

		Assert.Equal(new[] { "KeyBindings.xml" }, Directory.GetFiles(directory).Select(Path.GetFileName));
	}

	static Codon Codon(string name, string id, params (string Key, string Value)[] properties)
	{
		var tree = new AddInTreeImpl(null);
		var addIn = AddIn.Load(tree, new StringReader("<AddIn name='T'><Manifest><Identity name='Test.AddIn'/></Manifest></AddIn>"));
		var props = new Properties();
		props.Set("id", id);
		foreach (var (key, value) in properties)
			props.Set(key, value);
		return new Codon(addIn, name, props, null);
	}

	[Fact]
	public void TheCommandKey_FollowsWhatTheItemRuns()
	{
		Assert.Equal("class:ICSharpCode.Foo.Build", CommandKey.FromCodon(Codon("MenuItem", "Build", ("class", "ICSharpCode.Foo.Build"))));
		Assert.Equal("routed:Undo", CommandKey.FromCodon(Codon("MenuItem", "Undo", ("command", "Undo"))));
		Assert.Equal("pad:PropertyPad", CommandKey.FromCodon(Codon("Pad", "PropertyPad", ("class", "ICSharpCode.Foo.PropertyPad"))));
		Assert.Equal("item:Test.AddIn/Help", CommandKey.FromCodon(Codon("MenuItem", "Help", ("link", "https://example.org"))));
		// The class wins over a command, as in the feature name of the item
		Assert.Equal("class:X", CommandKey.FromCodon(Codon("MenuItem", "a", ("class", "X"), ("command", "Undo"))));
	}

	[Fact]
	public void APadWithoutAnId_IsKeyedByItsClass()
	{
		Assert.Equal("pad:ICSharpCode.Foo.Pad", CommandKey.ForPad(null, "ICSharpCode.Foo.Pad"));
		Assert.Equal("pad:Id", CommandKey.ForPad("Id", "ICSharpCode.Foo.Pad"));
		Assert.Throws<ArgumentException>(() => CommandKey.ForPad(null, null));
	}
}
