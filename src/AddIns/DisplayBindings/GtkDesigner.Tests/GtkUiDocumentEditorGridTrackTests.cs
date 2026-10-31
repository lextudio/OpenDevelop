using System.Linq;
using ICSharpCode.GtkDesigner;
using Xunit;

public sealed class GtkUiDocumentEditorGridTrackTests
{
	const string Source = """
<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <requires lib="gtk" version="4.0" />
  <object class="GtkWindow" id="w">
    <child><object class="GtkGrid" id="grid">
      <child><object class="GtkLabel" id="top"><layout><property name="row">0</property><property name="row-span">2</property></layout></object></child>
      <child><object class="GtkLabel" id="second"><layout><property name="row">1</property></layout></object></child>
      <child><object class="GtkLabel" id="third"><layout><property name="row">2</property></layout></object></child>
    </object></child>
  </object>
</interface>
""";

	static string Layout(GtkUiDocumentEditor editor, string id, string name) => editor.Roots[0].Children[0].Children.Single(c => c.Id == id).Layout![name];

	[Fact] public void InsertRowShiftsLaterChildrenStretchesSpannersAndUndoesAsOneStep()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		var before = editor.Text;
		Assert.True(editor.InsertGridTrack("second", row: true), editor.Error);
		Assert.Equal("0", Layout(editor, "top", "row"));
		Assert.Equal("3", Layout(editor, "top", "row-span"));
		Assert.Equal("2", Layout(editor, "second", "row"));
		Assert.Equal("3", Layout(editor, "third", "row"));
		Assert.True(editor.Undo());
		Assert.Equal(before, editor.Text);
	}

	[Fact] public void DeleteRowRemovesItsChildrenShrinksSpannersAndShiftsLaterOnesInOneStep()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		var before = editor.Text;
		Assert.True(editor.DeleteGridTrack("second", row: true), editor.Error);
		Assert.Equal(new[] { "top", "third" }, editor.Roots[0].Children[0].Children.Select(c => c.Id).ToArray());
		Assert.Equal("1", Layout(editor, "top", "row-span"));
		Assert.Equal("1", Layout(editor, "third", "row"));
		Assert.True(editor.Undo());
		Assert.Equal(before, editor.Text);
	}

	[Fact] public void BatchCollapsesEditsIntoOneUndoStepAndRollsBackOnFailure()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		var before = editor.Text;
		Assert.True(editor.Batch(() => editor.SetProperty("top", "halign", "center") && editor.SetProperty("second", "halign", "center")));
		Assert.True(editor.Undo());
		Assert.Equal(before, editor.Text);
		Assert.False(editor.CanUndo);
		Assert.True(editor.Redo());

		var afterBatch = editor.Text;
		Assert.False(editor.Batch(() => editor.SetProperty("third", "halign", "end") && editor.SetProperty("missing", "halign", "end")));
		Assert.Equal(afterBatch, editor.Text);
		Assert.False(editor.CanRedo);
		Assert.True(editor.Undo());
		Assert.Equal(before, editor.Text);
	}

	[Fact] public void BatchRollsBackAndRethrowsWhenAnEditThrows()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		Assert.True(editor.SetProperty("top", "halign", "start"));
		Assert.True(editor.Undo());
		var before = editor.Text;
		Assert.True(editor.CanRedo);
		Assert.Throws<InvalidOperationException>(() => editor.Batch(() => {
			Assert.True(editor.SetProperty("second", "halign", "center"));
			throw new InvalidOperationException("invalid value");
		}));
		Assert.Equal(before, editor.Text);
		Assert.False(editor.CanUndo);
		Assert.True(editor.CanRedo);
	}

	[Fact] public void PasteFirstInsertsBeforeTheContainersFirstChild()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		var snippets = editor.CopySnippets(new[] { "third" });
		Assert.NotNull(snippets);
		Assert.True(editor.Paste("grid", snippets!, out var created, GtkUiDocumentEditor.PasteFirst), editor.Error);
		Assert.Equal(created[0], editor.Roots[0].Children[0].Children[0].Id);
		Assert.Equal(4, editor.Roots[0].Children[0].Children.Count);
	}

	[Fact] public void RefusesAChildOfANonGrid()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source), editor.Error);
		Assert.False(editor.InsertGridTrack("grid", row: true));
	}
}
