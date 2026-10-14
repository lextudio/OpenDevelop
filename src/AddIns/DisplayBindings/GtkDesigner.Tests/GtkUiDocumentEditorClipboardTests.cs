using ICSharpCode.GtkDesigner;
using Xunit;

public sealed class GtkUiDocumentEditorClipboardTests
{
	const string Source = """
<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <requires lib="gtk" version="4.0" />
  <object class="GtkApplicationWindow" id="mainWindow">
    <child><object class="GtkBox" id="contentBox">
      <child><object class="GtkButton" id="runButton"><property name="label">Run</property><signal name="clicked" handler="OnRun" /></object></child>
      <child><object class="GtkBox" id="panel">
        <child><object class="GtkLabel" id="caption"><property name="mnemonic-widget">runButton</property></object></child>
        <child><object class="GtkEntry" id="field" /></child>
        <child><object class="GtkLabel" id="fieldLabel"><property name="mnemonic-widget">field</property></object></child>
      </object></child>
    </object></child>
  </object>
</interface>
""";

	[Fact] public void CopyTakesOuterSelectionsAndRejectsTheRoot()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		Assert.Null(editor.CopySnippets(new[] { "mainWindow" }));
		var snippets = Assert.Single(editor.CopySnippets(new[] { "panel", "field" })!);
		Assert.StartsWith("<child>", snippets);
		Assert.Contains("id=\"field\"", snippets);
	}

	[Fact] public void PasteMakesIdsUniqueAndFollowsInnerReferencesInOneUndoStep()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		var before = editor.Text;
		var snippets = editor.CopySnippets(new[] { "panel" })!;
		// Pasting onto a leaf goes to its container; after "runButton" keeps the copy beside it.
		Assert.True(editor.Paste("runButton", snippets, out var created, afterId: "runButton"), editor.Error);
		Assert.Equal(new[] { "panel1" }, created);
		Assert.Contains("id=\"caption1\"", editor.Text);
		Assert.Contains("id=\"field1\"", editor.Text);
		// A reference to an object inside the paste follows its rename; one to outside does not.
		Assert.Contains("<property name=\"mnemonic-widget\">field1</property>", editor.Text);
		Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(editor.Text, "<property name=\"mnemonic-widget\">runButton</property>").Count);
		Assert.True(editor.Text.IndexOf("id=\"runButton\"", StringComparison.Ordinal) < editor.Text.IndexOf("id=\"panel1\"", StringComparison.Ordinal));
		Assert.True(editor.Text.IndexOf("id=\"panel1\"", StringComparison.Ordinal) < editor.Text.IndexOf("id=\"panel\"", StringComparison.Ordinal));
		Assert.Equal("panel1", editor.Roots[0].Children[0].Children[1].Id);

		Assert.True(editor.Undo());
		Assert.Equal(before, editor.Text);
	}

	[Fact] public void PasteRemapsReferencesBetweenSnippetsAndSkipsIdLessRoots()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		// A label and its mnemonic widget copied as two separate snippets.
		var snippets = editor.CopySnippets(new[] { "field", "fieldLabel" })!;
		Assert.Equal(2, snippets.Count);
		Assert.True(editor.Paste("contentBox", snippets.Append("<child><object class=\"GtkSeparator\" /></child>").ToArray(), out var created));
		Assert.Equal(new[] { "field1", "fieldLabel1" }, created);
		Assert.Contains("<property name=\"mnemonic-widget\">field1</property>", editor.Text);
		Assert.Contains("class=\"GtkSeparator\"", editor.Text);
	}

	[Fact] public void RenameFollowsAMnemonicWidgetReference()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		Assert.True(editor.Rename("field", "nameEntry"));
		Assert.Contains("<property name=\"mnemonic-widget\">nameEntry</property>", editor.Text);
		// The caption's reference to another widget is not repointed.
		Assert.Contains("<property name=\"mnemonic-widget\">runButton</property>", editor.Text);
	}

	[Fact] public void PasteRejectsMarkupThatIsNotAGtkChild()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		Assert.False(editor.Paste("contentBox", new[] { "not xml" }, out _));
		Assert.False(editor.Paste("contentBox", new[] { "<object class=\"GtkLabel\" />" }, out _));
		Assert.False(editor.Paste("contentBox", new[] { "<child><template class=\"X\" parent=\"GtkBox\" /></child>" }, out _));
		Assert.False(editor.CanUndo);
	}
}

public sealed class GtkUiDocumentEditorWrapTests
{
	const string Source = """
<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <requires lib="gtk" version="4.0" />
  <object class="GtkApplicationWindow" id="mainWindow">
    <child><object class="GtkGrid" id="grid">
      <child><object class="GtkLabel" id="caption"><layout><property name="column">0</property><property name="row">1</property></layout></object></child>
      <child><object class="GtkButton" id="ok"><layout><property name="column">1</property><property name="row">1</property></layout></object></child>
      <child><object class="GtkButton" id="cancel"><layout><property name="column">2</property><property name="row">1</property></layout></object></child>
    </object></child>
  </object>
</interface>
""";

	[Fact] public void WrapsSiblingsInABoxThatTakesTheFirstCellInOneUndoStep()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		var before = editor.Text;
		Assert.True(editor.Wrap(new[] { "cancel", "ok" }, "GtkBox", out var created), editor.Error);
		Assert.Equal("box1", created);
		var grid = editor.Roots[0].Children[0];
		Assert.Equal(new[] { "caption", "box1" }, grid.Children.Select(c => c.Id));
		var box = grid.Children[1];
		Assert.Equal(new[] { "ok", "cancel" }, box.Children.Select(c => c.Id));
		Assert.Equal("1", box.Layout!["column"]);
		Assert.Equal("1", box.Layout!["row"]);
		Assert.True(box.Children.All(c => c.Layout == null || c.Layout.Count == 0));
		Assert.Equal("vertical", box.Properties["orientation"]);
		Assert.True(editor.Undo());
		Assert.Equal(before, editor.Text);
	}

	[Fact] public void RefusesTheRootMixedParentsAndSeveralIntoAFrame()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		Assert.False(editor.Wrap(new[] { "mainWindow" }, "GtkBox", out _));
		Assert.False(editor.Wrap(new[] { "ok", "grid" }, "GtkBox", out _));
		Assert.False(editor.Wrap(new[] { "ok", "cancel" }, "GtkFrame", out _));
		Assert.False(editor.Wrap(new[] { "ok" }, "GtkButton", out _));
		Assert.True(editor.Wrap(new[] { "ok" }, "GtkFrame", out var frame));
		Assert.Equal("frame1", frame);
	}
}
