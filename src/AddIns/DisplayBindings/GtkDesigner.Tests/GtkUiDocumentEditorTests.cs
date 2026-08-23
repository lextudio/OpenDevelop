using ICSharpCode.GtkDesigner;
using Xunit;

public sealed class GtkUiDocumentEditorTests
{
	const string Source = """
<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <requires lib="gtk" version="4.0" />
  <object class="GtkApplicationWindow" id="mainWindow">
    <property name="title">Example</property>
    <child><object class="GtkBox" id="contentBox">
      <child><object class="GtkButton" id="runButton"><property name="label">Run</property></object></child>
    </object></child>
  </object>
</interface>
""";

	[Fact] public void ParseBuildsGtkBuilderTree()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		Assert.Equal("mainWindow", editor.Roots[0].Id); Assert.Equal("contentBox", editor.Roots[0].Children[0].Id);
		Assert.Equal("Run", editor.Roots[0].Children[0].Children[0].Properties["label"]);
	}

	[Fact] public void PropertyInsertRenameDeleteAndHistoryRoundTrip()
	{
		var editor = new GtkUiDocumentEditor(); editor.Reset(Source);
		Assert.True(editor.SetProperty("runButton", "label", "Execute")); Assert.Contains(">Execute</property>", editor.Text);
		Assert.True(editor.Add("contentBox", "GtkEntry")); Assert.Contains("id=\"entry1\"", editor.Text);
		Assert.True(editor.Rename("entry1", "searchEntry")); Assert.Contains("id=\"searchEntry\"", editor.Text);
		Assert.True(editor.Remove("searchEntry")); Assert.DoesNotContain("searchEntry", editor.Text);
		Assert.True(editor.Undo()); Assert.Contains("searchEntry", editor.Text); Assert.True(editor.Redo());
	}

[Fact] public void Rename_FollowsIdReferences_ButLeavesDisplayTextAlone()
	{
		const string withRefs = """
<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <requires lib="gtk" version="4.0" />
  <object class="GtkBox" id="contentBox">
    <child><object class="GtkCheckButton" id="runButton">
      <property name="label">runButton</property>
      <property name="member-name">runButton</property>
    </object></child>
  </object>
</interface>
""";
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(withRefs), editor.Error);
		Assert.True(editor.Rename("runButton", "executeButton"), editor.Error);
		Assert.Contains("id=\"executeButton\"", editor.Text);
		Assert.Contains("<property name=\"member-name\">executeButton</property>", editor.Text);
		Assert.Contains(">runButton</property>", editor.Text); // display text untouched
	}

	[Fact] public void RejectsNonGtk4Document()
	{
		var editor = new GtkUiDocumentEditor(); Assert.False(editor.Reset("<interface><requires lib=\"gtk\" version=\"3.0\" /></interface>"));
		Assert.Contains("GTK 4", editor.Error);
	}

	// Regression: GtkBuilder <child> is only valid under container widgets; inserting under a
	// leaf (e.g. a button) produced documents libgtk refuses to load. The editor must reject it
	// instead of emitting invalid XML.
	[Fact] public void Add_UnderLeafWidget_IsRejected()
	{
		var editor = new GtkUiDocumentEditor(); editor.Reset(Source);
		Assert.False(editor.Add("runButton", "GtkLabel"));
		Assert.DoesNotContain("label1", editor.Text);
		Assert.True(editor.Add("contentBox", "GtkLabel")); // container still accepts children
	}
}
