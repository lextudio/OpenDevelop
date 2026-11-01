using System;
using System.Linq;
using ICSharpCode.GtkDesigner;
using ICSharpCode.SharpDevelop.Designer.Remote;
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
	public void Notebook_ActionWidgetsAreNotPagesAndDuplicateLandsAfterThePagesTab()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""
			<interface>
			  <requires lib="gtk" version="4.0"/>
			  <object class="GtkNotebook" id="book">
			    <child type="action-start">
			      <object class="GtkButton" id="start"/>
			    </child>
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
			    <child type="action-end">
			      <object class="GtkButton" id="end"/>
			    </child>
			  </object>
			</interface>
			"""), editor.Error);
		static int At(string text, string id) => text.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
		// Action widgets are neither paged nor reordered, and do not shift page indexes.
		Assert.False(editor.Reorder("start", 1));
		Assert.False(editor.Reorder("end", -1));
		Assert.True(editor.Reorder("p1", 1), editor.Error);
		Assert.True(At(editor.Text, "p2") < At(editor.Text, "t2") && At(editor.Text, "t2") < At(editor.Text, "p1") && At(editor.Text, "p1") < At(editor.Text, "t1"), editor.Text);
		Assert.True(editor.Add("book", "GtkLabel", index: 1), editor.Error);
		var inserted = editor.LastCreatedId!;
		Assert.True(At(editor.Text, "t2") < At(editor.Text, inserted) && At(editor.Text, inserted) < At(editor.Text, "p1"), editor.Text);
		// A source-text drop on p2 counts pages only: after p2 and its tab, not shifted by the action widget.
		var beforeDrop = editor.Text;
		Assert.True(editor.AddAt(editor.Text.IndexOf("id=\"p2\"", StringComparison.Ordinal), "GtkSwitch"), editor.Error);
		var dropped = editor.LastCreatedId!;
		Assert.True(At(editor.Text, "p2") < At(editor.Text, "t2") && At(editor.Text, "t2") < At(editor.Text, dropped) && At(editor.Text, dropped) < At(editor.Text, "p1"), editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal(beforeDrop, editor.Text);
		// Removing a page takes its tab, never an action widget; an action widget itself can be deleted.
		Assert.True(editor.Remove("p2"), editor.Error);
		Assert.DoesNotContain("id=\"t2\"", editor.Text);
		Assert.Contains("id=\"start\"", editor.Text);
		Assert.Contains("id=\"end\"", editor.Text);
		Assert.True(editor.Remove("end"), editor.Error);
		Assert.DoesNotContain("id=\"end\"", editor.Text);
		Assert.Contains("id=\"t1\"", editor.Text);
		Assert.True(editor.Undo()); Assert.True(editor.Undo());
		// Duplicating page p2 puts the copy after p2's tab, so p2 keeps its own label.
		var snippets = editor.CopySnippets(new[] { "p2" })!;
		Assert.True(editor.Paste("book", snippets, out var created, afterId: "p2"), editor.Error);
		Assert.True(At(editor.Text, "p2") < At(editor.Text, "t2") && At(editor.Text, "t2") < At(editor.Text, created[0]), editor.Text);
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

	static DesignerMenuNode Item(int source, string label, string action = "") => new() { SourceIndex = source, Label = label, Action = action };
	static DesignerMenuNode Container(string kind, int source, string label, params DesignerMenuNode[] children)
		=> new() { SourceIndex = source, Kind = kind, Label = label, Children = children.ToList() };

	const string MenuDocument = """<interface><requires lib="gtk" version="4.0"/><object class="GtkPopoverMenu" id="popover"><property name="menu-model"><menu>MENU</menu></property></object></interface>""";

	[Fact]
	public void MenuItems_EditTopLevelItems()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset(MenuDocument.Replace("MENU", """<item><attribute name="label">New</attribute><attribute name="action">app.new</attribute></item>""")), editor.Error);
		Assert.Equal("New = app.new", DesignerMenuOutline.Format(editor.GetMenuItems("popover")!));
		Assert.True(editor.SetMenuItems("popover", new[] { Item(0, "Newer", "app.newer"), Item(-1, "Open", "app.open") }), editor.Error);
		Assert.Equal("Newer = app.newer\nOpen = app.open", DesignerMenuOutline.Format(editor.GetMenuItems("popover")!));
		Assert.Contains("<attribute name=\"label\">Open</attribute>", editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal("New = app.new", DesignerMenuOutline.Format(editor.GetMenuItems("popover")!));
	}

	[Fact]
	public void MenuItems_ReadAndRewriteSectionsAndSubmenusKeepingUnmodelledAttributes()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset(MenuDocument.Replace("MENU", """
			<section>
			  <item><attribute name="label">New</attribute><attribute name="action">app.new</attribute><attribute name="icon">document-new</attribute></item>
			</section>
			<submenu>
			  <attribute name="label">Recent</attribute>
			  <item><attribute name="label">A</attribute><attribute name="action">app.a</attribute></item>
			</submenu>
			""")), editor.Error);
		var outline = DesignerMenuOutline.Format(editor.GetMenuItems("popover")!);
		Assert.Equal("[section]\n  New = app.new\n[submenu] Recent\n  A = app.a", outline);

		// Round-trip through the shared text form: the item keeps its icon, a new nested submenu is added.
		var edited = DesignerMenuOutline.Parse(outline + "\n  [submenu] More\n    B = app.b", editor.GetMenuItems("popover")!, out var error);
		Assert.NotNull(edited); Assert.Null(error);
		Assert.True(editor.SetMenuItems("popover", edited!), editor.Error);
		Assert.Contains("document-new", editor.Text);
		Assert.Equal("[section]\n  New = app.new\n[submenu] Recent\n  A = app.a\n  [submenu] More\n    B = app.b", DesignerMenuOutline.Format(editor.GetMenuItems("popover")!));
		Assert.True(editor.Undo());
		Assert.Equal(outline, DesignerMenuOutline.Format(editor.GetMenuItems("popover")!));

		// A new section around existing children is built from the tree, not by editing in place.
		Assert.True(editor.SetMenuItems("popover", new[] { Container("section", -1, "File", Item(-1, "Quit", "app.quit")) }), editor.Error);
		Assert.Equal("[section] File\n  Quit = app.quit", DesignerMenuOutline.Format(editor.GetMenuItems("popover")!));
	}

	[Fact]
	public void MenuItems_SaveWithoutEditsIsANoOpAndAnInsertedLineKeepsLaterEntriesAttributes()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset(MenuDocument.Replace("MENU", """
			<section>
			  <attribute name="label">File</attribute>
			  <item><attribute name="label">New</attribute><attribute name="action">app.new</attribute><attribute name="icon">document-new</attribute></item>
			</section>
			<submenu>
			  <attribute name="label">Recent</attribute>
			  <attribute name="icon">folder</attribute>
			  <item><attribute name="label">A</attribute><attribute name="action">app.a</attribute></item>
			</submenu>
			""")), editor.Error);
		var before = editor.Text;
		var current = editor.GetMenuItems("popover")!;
		var same = DesignerMenuOutline.Parse(DesignerMenuOutline.Format(current), current, out var error);
		Assert.Null(error);
		Assert.True(editor.SetMenuItems("popover", same!), editor.Error);
		Assert.Equal(before, editor.Text);
		Assert.False(editor.Undo()); // nothing was recorded

		// A line inserted above everything shifts positions, but entries are matched by content first,
		// so the section's item and the submenu keep their icons.
		var text = "[section] Top\n  Fresh = app.fresh\n" + DesignerMenuOutline.Format(current);
		var edited = DesignerMenuOutline.Parse(text, current, out error);
		Assert.Null(error);
		Assert.True(editor.SetMenuItems("popover", edited!), editor.Error);
		Assert.Contains("document-new", editor.Text);
		Assert.Contains("<attribute name=\"icon\">folder</attribute>", editor.Text);
		Assert.Equal(text, DesignerMenuOutline.Format(editor.GetMenuItems("popover")!));
	}

	[Fact]
	public void MenuItems_RefuseUnknownKindsRepeatedSourcesAndItemsWithChildren()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset(MenuDocument.Replace("MENU", """<item><attribute name="label">New</attribute></item><item><attribute name="label">Open</attribute></item>""")), editor.Error);
		Assert.False(editor.SetMenuItems("popover", new[] { new DesignerMenuNode { Kind = "link", Label = "x" } }));
		Assert.False(editor.SetMenuItems("popover", new[] { Item(0, "A"), Item(0, "B") }));
		Assert.False(editor.SetMenuItems("popover", new[] { Item(5, "A") }));
		var holder = Item(-1, "Item");
		holder.Children.Add(Item(-1, "Nested"));
		Assert.False(editor.SetMenuItems("popover", new[] { holder }));
	}

	[Fact]
	public void MenuOutline_ParsesDepthAndRejectsIndentedItemChildren()
	{
		var tree = DesignerMenuOutline.Parse("[section] S\n\tA = a\n  B\nC = c", Array.Empty<DesignerMenuNode>(), out var error);
		Assert.Null(error);
		Assert.Equal(new[] { "section", "item" }, tree!.Select(n => n.Kind));
		Assert.Equal(new[] { "A", "B" }, tree![0].Children.Select(n => n.Label));
		Assert.Null(DesignerMenuOutline.Parse("A = a\n  B = b", Array.Empty<DesignerMenuNode>(), out error));
		Assert.Contains("only a section or submenu", error);
		Assert.Null(DesignerMenuOutline.Parse("[section] S\n      Deep = d", Array.Empty<DesignerMenuNode>(), out error));
		Assert.Contains("indented deeper", error);
		// One space is not a level: it must not silently flatten a child into a sibling.
		Assert.Null(DesignerMenuOutline.Parse("[section] S\n Child = c", Array.Empty<DesignerMenuNode>(), out error));
		Assert.Contains("odd number of spaces", error);
	}
}
