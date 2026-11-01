using ICSharpCode.GtkDesigner;
using ICSharpCode.Translations;
using Xunit;

namespace GtkDesigner.Tests;

/// <summary>What the GTK designer contributes to the Translations addin: the strings a .ui marks translatable.
/// The catalogue, merge and project workflow are the addin's (see its own tests).</summary>
public sealed class GtkTranslationsTests
{
	const string Ui = """
		<interface>
		  <object class="GtkWindow" id="w">
		    <property name="title" translatable="yes">Settings</property>
		    <property name="name">not-translatable</property>
		    <child>
		      <object class="GtkButton" id="b">
		        <property name="label" translatable="yes" context="button" comments="Shown on the save button">Save</property>
		      </object>
		    </child>
		    <child>
		      <object class="GtkMenuButton" id="m">
		        <property name="menu-model"><menu><item><attribute name="label" translatable="yes">Open "recent"
		files</attribute></item></menu></property>
		      </object>
		    </child>
		    <child>
		      <object class="GtkStringList" id="s"><items><item translatable="yes">One</item><item>Raw</item></items></object>
		    </child>
		    <child><object class="GtkLabel" id="e"><property name="label" translatable="yes"></property></object></child>
		  </object>
		</interface>
		""";

	[Fact]
	public void Extract_FindsEveryTranslatableElementWithContextCommentAndLine()
	{
		var entries = GtkTranslations.Extract(Ui, "Windows/Main.ui");
		Assert.Equal(new[] { "Settings", "Save", "Open \"recent\"\n\t\tfiles", "One" }.Select(s => s.Replace("\t", "")), entries.Select(e => e.MsgId.Replace("\t", "")));
		var save = entries.Single(e => e.MsgId == "Save");
		Assert.Equal("button", save.Context);
		Assert.Equal(new[] { "Shown on the save button" }, save.ExtractedComments);
		Assert.Matches(@"^Windows/Main\.ui:\d+$", save.References.Single());
		Assert.Empty(GtkTranslations.Extract("<not xml", "x.ui"));
	}

	[Fact]
	public void Source_HandlesUiFilesOnly()
	{
		ITranslatableStringSource source = new GtkTranslatableSource();
		Assert.True(source.Handles("Windows/Main.ui"));
		Assert.True(source.Handles("MAIN.UI"));
		Assert.False(source.Handles("Main.cs"));
		Assert.Single(source.Extract(Ui.Replace("translatable=\"yes\"", "translatable=\"no\"").Replace("<property name=\"title\" translatable=\"no\">", "<property name=\"title\" translatable=\"yes\">"), "Main.ui"));
	}
}
