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

	[Fact] public void Paned_TakesAStartAndAnEndChildAndRejectsAThird()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkPaned" id="paned"/></child></object></interface>"""), editor.Error);
		Assert.True(editor.Add("paned", "GtkLabel"), editor.Error);
		Assert.True(editor.Add("paned", "GtkButton", index: 1), editor.Error);
		Assert.False(editor.Add("paned", "GtkLabel"));
		Assert.Contains("start and an end", editor.Refusal);
		Assert.Equal("", editor.Error); // a refusal is not a parse error: the document stays accepted
		Assert.Equal(2, editor.Text.Split("<child>").Length - 1 - 1); // the window's own <child> is not the paned's
	}

	[Fact] public void NavigationContainers_UseTheirGtkBuilderChildShapes()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="AdwNavigationView" id="navigation"/><object class="AdwNavigationSplitView" id="split"/><object class="AdwNavigationPage" id="page"/></interface>"""), editor.Error);
		Assert.False(editor.Add("navigation", "GtkLabel"));
		Assert.True(editor.Add("navigation", "AdwNavigationPage"));
		Assert.True(editor.Add("split", "AdwNavigationPage"));
		Assert.True(editor.Add("split", "AdwNavigationPage"));
		Assert.False(editor.Add("split", "AdwNavigationPage"));
		Assert.True(editor.Add("page", "GtkBox"));
		Assert.False(editor.Add("page", "GtkLabel"));
		var doc = System.Xml.Linq.XDocument.Parse(editor.Text);
		var split = doc.Descendants("object").Single(x => (string?)x.Attribute("id") == "split");
		Assert.Equal(new[] { "sidebar", "content" }, split.Elements("child").Select(x => (string?)x.Attribute("type")));
		var page = doc.Descendants("object").Single(x => (string?)x.Attribute("id") == "page");
		Assert.Equal("GtkBox", (string?)page.Elements("property").Single(x => (string?)x.Attribute("name") == "child").Element("object")?.Attribute("class"));
	}

	[Fact]
	public void SingleChildContainers_RejectASecondChild()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkFrame" id="frame"><child><object class="GtkLabel" id="only"/></child></object></interface>"""), editor.Error);
		Assert.False(editor.Add("frame", "GtkButton"));
		Assert.Contains("single child", editor.Refusal);
		Assert.Equal("", editor.Error); // a refusal is not a parse error: the document stays accepted
		Assert.DoesNotContain("button1", editor.Text);
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkFrame" id="frame"/></interface>"""), editor.Error);
		Assert.True(editor.Add("frame", "GtkButton"), editor.Error);
	}

	[Fact]
	public void Stack_Add_WritesANamedTitledPage()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkStack" id="stack"/></interface>"""), editor.Error);
		Assert.True(editor.Add("stack", "GtkButton"), editor.Error);
		var page = editor.Roots[0].Children[0];
		Assert.Equal("page1", page.Id);
		Assert.Equal("GtkStackPage", page.ClassName);
		Assert.Equal("page1", page.Properties["name"]);
		Assert.Equal("GtkButton", page.Children[0].ClassName);
		Assert.Contains("<object class=\"GtkStackPage\" id=\"page1\">", editor.Text);
		Assert.Contains("<property name=\"title\" translatable=\"yes\">Page 1</property>", editor.Text);
		Assert.True(editor.Add("stack", "GtkLabel"), editor.Error);
		Assert.Equal(new[] { "page1", "page2" }, editor.Roots[0].Children.Select(c => c.Id));
	}

	[Fact]
	public void Notebook_Add_WritesAPlainPageChild()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkNotebook" id="notebook"/></interface>"""), editor.Error);
		Assert.True(editor.Add("notebook", "GtkButton"), editor.Error);
		Assert.Equal("GtkButton", editor.Roots[0].Children[0].ClassName);
		Assert.DoesNotContain("GtkNotebookPage", editor.Text);
	}

	[Fact]
	public void ListViews_GetAStarterModelAndFactory()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkBox" id="box"/></child></object></interface>"""), editor.Error);
		Assert.True(editor.Add("box", "GtkListView"), editor.Error);
		var list = editor.Roots[0].Children[0].Children[0];
		Assert.Equal("GtkListView", list.ClassName);
		Assert.Equal(new[] { "Item 1", "Item 2" }, editor.GetItems(list.Id));
		Assert.Contains("GtkBuilderListItemFactory", editor.Text);
		Assert.Contains("<property name=\"bytes\">&lt;interface&gt;", editor.Text);

		Assert.True(editor.Add("box", "GtkColumnView"), editor.Error);
		var columnView = editor.Roots[0].Children[0].Children[1];
		Assert.Equal("GtkColumnView", columnView.ClassName);
		var column = columnView.Children.Single(child => child.ClassName == "GtkColumnViewColumn");
		Assert.Equal("Column 1", column.Properties["title"]);
		Assert.Contains("<object class=\"GtkBuilderListItemFactory\">", editor.Text);
	}

	[Fact]
	public void ColumnViews_TakeColumnsAndRefuseWidgets()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkColumnView" id="columns"/></interface>"""), editor.Error);
		Assert.False(editor.Add("columns", "GtkButton"));
		Assert.Contains("holds columns", editor.Refusal);
		Assert.True(editor.Add("columns", "GtkColumnViewColumn"), editor.Error);
		Assert.Equal("column1", editor.Roots[0].Children[0].Id);
		Assert.Equal("Column 1", editor.Roots[0].Children[0].Properties["title"]);
		Assert.Contains("<object class=\"GtkBuilderListItemFactory\">", editor.Text);
		Assert.True(editor.Add("columns", "GtkColumnViewColumn", index: 0), editor.Error);
		Assert.Equal(new[] { "column2", "column1" }, editor.Roots[0].Children.Select(child => child.Id));
	}

	[Fact]
	public void ToolbarView_PlacesTopBottomBarsAndOneContent()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="AdwToolbarView" id="toolbar"/></interface>"""), editor.Error);
		Assert.True(editor.Add("toolbar", "GtkLabel", index: 2), editor.Error);
		Assert.Contains("<property name=\"content\">", editor.Text);
		Assert.True(editor.Add("toolbar", "GtkButton", index: 0), editor.Error);
		Assert.Contains("<child type=\"top\">", editor.Text);
		Assert.True(editor.Add("toolbar", "GtkButton", index: 1), editor.Error);
		Assert.Contains("<child type=\"bottom\">", editor.Text);
		Assert.False(editor.Add("toolbar", "GtkLabel", index: 2));
		Assert.Contains("one content widget", editor.Refusal);
	}

	[Theory]
	[InlineData("GtkHeaderBar")]
	[InlineData("AdwHeaderBar")]
	public void HeaderBar_PacksAtStartAndEndAndHoldsOneTitle(string headerBar)
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset($"""<interface><requires lib="gtk" version="4.0"/><object class="{headerBar}" id="bar"/></interface>"""), editor.Error);
		Assert.True(editor.Add("bar", "GtkButton", index: 0), editor.Error);
		Assert.Contains("<child type=\"start\">", editor.Text);
		Assert.True(editor.Add("bar", "GtkButton", index: 1), editor.Error);
		Assert.Contains("<child type=\"end\">", editor.Text);
		Assert.True(editor.Add("bar", "GtkLabel", index: 2), editor.Error);
		Assert.Contains("<child type=\"title\">", editor.Text);
		Assert.False(editor.Add("bar", "GtkLabel", index: 2));
		Assert.Contains("one title widget", editor.Refusal);
		// A toolbox click (no slot) packs at the start.
		Assert.True(editor.Add("bar", "GtkButton"), editor.Error);
		Assert.Equal(2, editor.Text.Split("<child type=\"start\">").Length - 1);
	}

	[Fact]
	public void HeaderBar_SourceDropPacksAtTheStartInsteadOfReadingTheChildCountAsASlot()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkHeaderBar" id="bar"><child type="end"><object class="GtkButton" id="a"/></child><child type="end"><object class="GtkButton" id="b"/></child></object></interface>"""), editor.Error);
		// A drop after two existing children is child index 2, which the slot convention would read as
		// the title; a point in the markup has no slot, so it packs at the start instead.
		Assert.True(editor.AddAt(editor.Text.IndexOf("</object></interface>", StringComparison.Ordinal), "GtkLabel"), editor.Error);
		Assert.Contains("<child type=\"start\">", editor.Text);
		Assert.DoesNotContain("<child type=\"title\">", editor.Text);
	}

	[Fact]
	public void Notebook_PageGetsATabLabelAndTakesItAlongWhenRemoved()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkNotebook" id="book"/></interface>"""), editor.Error);
		Assert.True(editor.Add("book", "GtkBox"), editor.Error);
		var first = editor.LastCreatedId!;
		Assert.True(editor.Add("book", "GtkBox"), editor.Error);
		var second = editor.LastCreatedId!;
		// A page inserted at index 1 lands between the two, directly followed by its own tab.
		Assert.True(editor.Add("book", "GtkGrid", index: 1), editor.Error);
		var middle = editor.LastCreatedId!;
		Assert.Equal(3, editor.Text.Split("<child type=\"tab\">").Length - 1);
		var text = editor.Text;
		Assert.True(text.IndexOf($"id=\"{first}\"") < text.IndexOf("id=\"tab1\"") && text.IndexOf("id=\"tab1\"") < text.IndexOf($"id=\"{middle}\""));
		Assert.True(text.IndexOf($"id=\"{middle}\"") < text.IndexOf($"id=\"{second}\""));
		Assert.Contains("Page 3", text);
		Assert.True(editor.Remove(middle), editor.Error);
		Assert.Equal(2, editor.Text.Split("<child type=\"tab\">").Length - 1);
		Assert.DoesNotContain($"id=\"{middle}\"", editor.Text);
		// A tab label can be removed alone; its page stays.
		Assert.True(editor.Remove("tab1"), editor.Error);
		Assert.Contains($"id=\"{first}\"", editor.Text);
	}

	[Fact]
	public void Notebook_ReorderMovesAPageTogetherWithItsTab()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkNotebook" id="book"/></interface>"""), editor.Error);
		Assert.True(editor.Add("book", "GtkBox"), editor.Error);
		var first = editor.LastCreatedId!;
		Assert.True(editor.Add("book", "GtkGrid"), editor.Error);
		var second = editor.LastCreatedId!;
		static int At(string text, string id) => text.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
		Assert.True(editor.Reorder(first, 1), editor.Error);
		var text = editor.Text;
		// second, its tab2, first, its tab1: each tab still directly follows its own page.
		Assert.True(At(text, second) < At(text, "tab2") && At(text, "tab2") < At(text, first) && At(text, first) < At(text, "tab1"));
		Assert.False(editor.Reorder("tab1", -1));
		Assert.True(editor.Reorder(first, -1), editor.Error);
		text = editor.Text;
		Assert.True(At(text, first) < At(text, "tab1") && At(text, "tab1") < At(text, second) && At(text, second) < At(text, "tab2"));
	}

	[Fact]
	public void Notebook_IndentedDocumentKeepsEachTabWithItsPageAcrossRemoveAndReorder()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkNotebook" id="book">
			    <child>
			      <object class="GtkBox" id="p1"/>
			    </child>
			    <child type="tab">
			      <object class="GtkLabel" id="t1"/>
			    </child>
			    <child>
			      <object class="GtkBox" id="p2"/>
			    </child>
			    <child type="tab">
			      <object class="GtkLabel" id="t2"/>
			    </child>
			    <child>
			      <object class="GtkBox" id="p3"/>
			    </child>
			    <child type="tab">
			      <object class="GtkLabel" id="t3"/>
			    </child>
			  </object>
			</interface>
			"""), editor.Error);
		static int At(string text, string id) => text.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
		static bool Order(string text, params string[] ids) => ids.Zip(ids.Skip(1), (a, b) => At(text, a) < At(text, b)).All(x => x);
		// One step down among three pages moves p1 past p2 and p2's tab, not back to where it was.
		Assert.True(editor.Reorder("p1", 1), editor.Error);
		Assert.True(Order(editor.Text, "p2", "t2", "p1", "t1", "p3", "t3"), editor.Text);
		Assert.True(editor.Reorder("p3", -1), editor.Error);
		Assert.True(Order(editor.Text, "p2", "t2", "p3", "t3", "p1", "t1"), editor.Text);
		// Removing a page removes the tab that follows it, even with indentation between them.
		Assert.True(editor.Remove("p3"), editor.Error);
		Assert.DoesNotContain("id=\"t3\"", editor.Text);
		Assert.True(Order(editor.Text, "p2", "t2", "p1", "t1"), editor.Text);
	}

	[Fact]
	public void Reorder_OneStepDownAmongThreeChildrenActuallyMoves()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkBox" id="box"><child><object class="GtkButton" id="a"/></child><child><object class="GtkButton" id="b"/></child><child><object class="GtkButton" id="c"/></child></object></interface>"""), editor.Error);
		Assert.True(editor.Reorder("a", 1), editor.Error);
		Assert.Equal(new[] { "b", "a", "c" }, editor.Roots[0].Children.Select(child => child.Id));
		Assert.True(editor.Reorder("a", 1), editor.Error);
		Assert.Equal(new[] { "b", "c", "a" }, editor.Roots[0].Children.Select(child => child.Id));
		Assert.True(editor.Reorder("a", -2), editor.Error);
		Assert.Equal(new[] { "a", "b", "c" }, editor.Roots[0].Children.Select(child => child.Id));
	}

	[Fact]
	public void Notebook_SourceDropIndexesPagesNotTabs()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkNotebook" id="book">
			    <child>
			      <object class="GtkButton" id="p1"/>
			    </child>
			    <child type="tab">
			      <object class="GtkLabel" id="t1"/>
			    </child>
			    <child>
			      <object class="GtkButton" id="p2"/>
			    </child>
			    <child type="tab">
			      <object class="GtkLabel" id="t2"/>
			    </child>
			    <child>
			      <object class="GtkButton" id="p3"/>
			    </child>
			    <child type="tab">
			      <object class="GtkLabel" id="t3"/>
			    </child>
			  </object>
			</interface>
			"""), editor.Error);
		// A drop on the second page's markup goes after that page and its tab, before p3. Counting every
		// preceding <child> (tabs included) would give index 4, past the three pages: the end instead.
		Assert.True(editor.AddAt(editor.Text.IndexOf("id=\"p2\"", StringComparison.Ordinal), "GtkLabel"), editor.Error);
		var text = editor.Text;
		var created = editor.LastCreatedId!;
		static int At(string text, string id) => text.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
		Assert.True(At(text, "t2") < At(text, created) && At(text, created) < At(text, "tab1") && At(text, "tab1") < At(text, "p3"), text);
	}

	[Fact]
	public void FactoryTemplate_RoundTripsAndRejectsInvalidXml()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkBox" id="box"/></child></object></interface>"""), editor.Error);
		Assert.True(editor.Add("box", "GtkListView"), editor.Error);
		var id = editor.LastCreatedId!;
		var template = editor.GetFactoryTemplate(id);
		Assert.Contains("GtkListItem", template);
		Assert.True(editor.SetFactoryTemplate(id, template!.Replace("GtkLabel", "GtkButton")), editor.Error);
		Assert.Contains("GtkButton", editor.GetFactoryTemplate(id));
		Assert.False(editor.SetFactoryTemplate(id, "<not xml"));
		Assert.Contains("not valid XML", editor.Refusal);
		Assert.True(editor.Undo());
		Assert.Contains("GtkLabel", editor.GetFactoryTemplate(id));
	}

	[Fact]
	public void MenuItems_EditTopLevelItemsAndRefuseSections()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkPopoverMenu" id="popover"><property name="menu-model"><menu><item><attribute name="label">New</attribute><attribute name="action">app.new</attribute></item></menu></property></object></interface>"""), editor.Error);
		Assert.Equal(new[] { ("New", "app.new") }, editor.GetMenuItems("popover"));
		Assert.True(editor.SetMenuItems("popover", new[] { (0, "Newer", "app.newer"), (-1, "Open", "app.open") }), editor.Error);
		Assert.Equal(new[] { ("Newer", "app.newer"), ("Open", "app.open") }, editor.GetMenuItems("popover"));
		Assert.Contains("<attribute name=\"label\">Open</attribute>", editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal(new[] { ("New", "app.new") }, editor.GetMenuItems("popover"));

		// A sectioned menu is source-only: rewriting it here would drop the section structure.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkPopoverMenu" id="popover"><property name="menu-model"><menu><section><item><attribute name="label">New</attribute></item></section></menu></property></object></interface>"""), editor.Error);
		Assert.Null(editor.GetMenuItems("popover"));
		Assert.False(editor.SetMenuItems("popover", new[] { (-1, "Open", "") }));
	}
}
