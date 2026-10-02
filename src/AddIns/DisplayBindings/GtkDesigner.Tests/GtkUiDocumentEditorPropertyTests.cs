using ICSharpCode.GtkDesigner;
using Xunit;

namespace GtkDesigner.Tests;

public sealed class GtkUiDocumentEditorPropertyTests
{
	const string Document = """
		<?xml version="1.0" encoding="UTF-8"?>
		<interface>
		  <requires lib="gtk" version="4.0"/>
		  <object class="GtkWindow" id="window">
		    <child>
		      <object class="GtkButton" id="button">
		        <property name="label" translatable="yes" context="toolbar">Run</property>
		        <property name="margin_start">6</property>
		      </object>
		    </child>
		  </object>
		</interface>
		""";

	static GtkUiDocumentEditor Load() { var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Document), editor.Error); return editor; }

	[Fact]
	public void Reset_RemovesTheProperty_SoGtkDefaultApplies()
	{
		var editor = Load();
		Assert.True(editor.SetProperty("button", "margin-start", null));
		Assert.DoesNotContain("margin_start", editor.Text);
		Assert.True(editor.CanUndo);
		Assert.True(editor.Undo());
		Assert.Contains("<property name=\"margin_start\">6</property>", editor.Text);
	}

	[Fact]
	public void ResetOfAnUnsetProperty_AndSameValueEdit_AreNotUndoSteps()
	{
		var editor = Load();
		Assert.True(editor.SetProperty("button", "hexpand", null));
		Assert.True(editor.SetProperty("button", "label", "Run"));
		Assert.False(editor.CanUndo);
	}

	[Fact]
	public void UnderscoreAndDashSpellings_FindTheSameProperty()
	{
		var editor = Load();
		Assert.True(editor.SetProperty("button", "margin-start", "12"));
		Assert.Contains("<property name=\"margin_start\">12</property>", editor.Text);
		Assert.DoesNotContain("name=\"margin-start\"", editor.Text);
	}

	[Fact]
	public void LayoutProperty_CreatesLayoutElement_UpdatesAndResetsIt()
	{
		var editor = Load();
		Assert.True(editor.SetLayoutProperty("button", "column", "1"));
		Assert.True(editor.SetLayoutProperty("button", "row", "2"));
		Assert.Contains("<layout><property name=\"column\">1</property><property name=\"row\">2</property></layout>", editor.Text.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("propertyname", "property name"));
		var button = editor.Roots[0].Children[0];
		Assert.Equal("1", button.Layout!["column"]);
		Assert.Equal("2", button.Layout!["row"]);
		Assert.True(editor.SetLayoutProperty("button", "column", "3"));
		Assert.Equal("3", editor.Roots[0].Children[0].Layout!["column"]);
		// Resetting the last layout property removes the now-empty <layout> element.
		Assert.True(editor.SetLayoutProperty("button", "column", null));
		Assert.True(editor.SetLayoutProperty("button", "row", null));
		Assert.DoesNotContain("<layout", editor.Text);
		Assert.True(editor.Undo());
		Assert.Contains("<layout", editor.Text);
	}

	[Fact]
	public void AddingToAGrid_AppendsInColumnZeroOfTheFirstFreeRow()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkWindow" id="window">
			    <child>
			      <object class="GtkGrid" id="grid">
			        <child><object class="GtkLabel" id="tall"><layout><property name="row">1</property><property name="row-span">2</property></layout></object></child>
			      </object>
			    </child>
			  </object>
			</interface>
			"""), editor.Error);
		Assert.True(editor.Add("grid", "GtkButton"));
		var added = editor.Roots[0].Children[0].Children[1];
		Assert.Equal("GtkButton", added.ClassName);
		Assert.Equal("0", added.Layout!["column"]);
		Assert.Equal("3", added.Layout!["row"]);   // below row 1 + span 2
		Assert.Contains("translatable=\"yes\"", editor.Text);
	}

	[Fact]
	public void NewUserVisibleString_IsMarkedTranslatable_ExistingAttributesAreKept()
	{
		var editor = Load();
		Assert.True(editor.SetProperty("window", "title", "Main", translatable: true));
		Assert.Contains("<property name=\"title\" translatable=\"yes\">Main</property>", editor.Text);
		Assert.True(editor.SetProperty("button", "label", "Execute", translatable: true));
		Assert.Contains("<property name=\"label\" translatable=\"yes\" context=\"toolbar\">Execute</property>", editor.Text);
	}
}
