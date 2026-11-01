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
		// GtkListView takes a GtkSelectionModel, so the starter list sits inside a GtkSingleSelection.
		Assert.Matches("<object class=\"GtkListView\"[^>]*>\\s*<property name=\"model\">\\s*<object class=\"GtkSingleSelection\" id=\"selection1\">\\s*<property name=\"model\">\\s*<object class=\"GtkStringList\" id=\"listModel1\">", editor.Text);
		Assert.Equal("GtkSingleSelection", editor.GetSelectionModel(list.Id));
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
	public void TabView_Add_WritesAnAdwTabPageHoldingTheWidget()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><requires lib="libadwaita" version="1.0"/><object class="AdwTabView" id="tabs"/></interface>"""), editor.Error);
		Assert.True(editor.Add("tabs", "GtkLabel"), editor.Error);
		Assert.Equal("label1", editor.LastCreatedId); // the widget the user asked for, not the page wrapper
		Assert.True(editor.Add("tabs", "GtkButton"), editor.Error);
		var doc = System.Xml.Linq.XDocument.Parse(editor.Text);
		var pages = doc.Descendants("object").Where(o => (string?)o.Attribute("class") == "AdwTabPage").ToList();
		Assert.Equal(new[] { "page1", "page2" }, pages.Select(p => (string?)p.Attribute("id")));
		Assert.Equal(new[] { "Page 1", "Page 2" }, pages.Select(p => p.Elements("property").Single(x => (string?)x.Attribute("name") == "title").Value));
		Assert.Equal(new[] { "GtkLabel", "GtkButton" }, pages.Select(p => (string?)p.Elements("property").Single(x => (string?)x.Attribute("name") == "child").Element("object")!.Attribute("class")));
		Assert.Contains("label1", editor.Roots[0].Children.Select(c => c.Id).Concat(editor.Roots[0].Children.SelectMany(c => c.Children.Select(g => g.Id))));
	}

	[Fact]
	public void TabBar_ViewReferenceFollowsARename()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><requires lib="libadwaita" version="1.0"/><object class="GtkBox" id="root"><child><object class="AdwTabBar" id="bar"><property name="view">tabs</property></object></child><child><object class="AdwTabView" id="tabs"/></child></object></interface>"""), editor.Error);
		Assert.True(editor.Rename("tabs", "documents"), editor.Error);
		Assert.Contains("<property name=\"view\">documents</property>", editor.Text);
	}

	const string WrappedListDocument = """<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="view"><property name="model"><object class="GtkSortListModel" id="sorted"><property name="model"><object class="GtkFilterListModel" id="filtered"><property name="model"><object class="GtkMultiSelection" id="picks"><property name="model"><object class="GtkStringList" id="strings"><items><item>One</item><item>Two</item></items></object></property></object></property></object></property></object></property></object></child></object></interface>""";

	[Fact]
	public void Items_AreEditableThroughSelectionSortAndFilterWrappers()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset(WrappedListDocument), editor.Error);
		Assert.Equal("GtkStringList", editor.ItemsModelClass("view"));
		Assert.Equal(new[] { "One", "Two" }, editor.GetItems("view"));
		Assert.True(editor.SetItems("view", new[] { (0, "Uno"), (1, "Dos"), (-1, "Tres") }), editor.Error);
		Assert.Equal(new[] { "Uno", "Dos", "Tres" }, editor.GetItems("view"));
		Assert.Contains("<object class=\"GtkSortListModel\" id=\"sorted\">", editor.Text); // the wrappers are untouched
		// A wrapper around a model that is not a string list has no Items row.
		Assert.True(editor.Reset(WrappedListDocument.Replace("GtkStringList", "GListStore")), editor.Error);
		Assert.Null(editor.GetItems("view"));
	}

	[Fact]
	public void SelectionModel_SwitchesClassKeepsTheModelAndDropsSingleOnlyProperties()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkGridView" id="view"><property name="model"><object class="GtkSingleSelection" id="pick"><property name="autoselect">false</property><property name="can-unselect">true</property><property name="selected">1</property><property name="model"><object class="GtkStringList" id="strings"><items><item>One</item></items></object></property></object></property></object></child></object></interface>"""), editor.Error);
		Assert.Equal("GtkSingleSelection", editor.GetSelectionModel("view"));
		Assert.True(editor.SetSelectionModel("view", "GtkMultiSelection"), editor.Error);
		Assert.Equal("GtkMultiSelection", editor.GetSelectionModel("view"));
		Assert.DoesNotContain("autoselect", editor.Text);
		Assert.DoesNotContain("can-unselect", editor.Text);
		Assert.DoesNotContain("\"selected\"", editor.Text);
		Assert.Equal(new[] { "One" }, editor.GetItems("view"));
		Assert.True(editor.SetSelectionModel("view", "GtkNoSelection"), editor.Error);
		Assert.Equal("GtkNoSelection", editor.GetSelectionModel("view"));
		Assert.True(editor.SetSelectionModel("view", "GtkSingleSelection"), editor.Error);
		Assert.Equal("GtkSingleSelection", editor.GetSelectionModel("view"));
		Assert.True(editor.Undo());
		Assert.Equal("GtkNoSelection", editor.GetSelectionModel("view"));
		// Asking for what is already there changes nothing, not even the undo history.
		var before = editor.Text;
		Assert.True(editor.SetSelectionModel("view", "GtkNoSelection"), editor.Error);
		Assert.Equal(before, editor.Text);
	}

	[Fact]
	public void SelectionModel_WrapsABareListOrAReferenceAndRefusesWhatItCannot()
	{
		var editor = new GtkUiDocumentEditor();
		// An older document: the view holds a bare GtkStringList, which GTK rejects for a GtkSelectionModel property.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="view"><property name="model"><object class="GtkStringList" id="strings"><items><item>One</item></items></object></property></object></child></object></interface>"""), editor.Error);
		Assert.Null(editor.GetSelectionModel("view"));
		Assert.True(editor.SetSelectionModel("view", "GtkMultiSelection"), editor.Error);
		Assert.Equal("GtkMultiSelection", editor.GetSelectionModel("view"));
		Assert.Equal(new[] { "One" }, editor.GetItems("view"));
		Assert.Matches("<object class=\"GtkMultiSelection\" id=\"selection1\">\\s*<property name=\"model\">\\s*<object class=\"GtkStringList\" id=\"strings\">", editor.Text);
		Assert.True(editor.Undo());
		Assert.Null(editor.GetSelectionModel("view"));

		// A reference to a model defined elsewhere is wrapped too, keeping the reference.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkStringList" id="shared"/><object class="GtkWindow" id="w"><child><object class="GtkColumnView" id="view"><property name="model">shared</property></object></child></object></interface>"""), editor.Error);
		Assert.True(editor.SetSelectionModel("view", "GtkSingleSelection"), editor.Error);
		Assert.Matches("<object class=\"GtkSingleSelection\" id=\"selection1\">\\s*<property name=\"model\">shared</property>", editor.Text);

		// A reference to an existing selection model is that selection model: its class changes, and nothing is nested.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkSingleSelection" id="picks"><property name="model"><object class="GtkStringList" id="strings"/></property></object><object class="GtkWindow" id="w"><child><object class="GtkListView" id="view"><property name="model">picks</property></object></child></object></interface>"""), editor.Error);
		Assert.Equal("GtkSingleSelection", editor.GetSelectionModel("view"));
		Assert.True(editor.SetSelectionModel("view", "GtkMultiSelection"), editor.Error);
		Assert.Equal("GtkMultiSelection", editor.GetSelectionModel("view"));
		Assert.Contains("<object class=\"GtkMultiSelection\" id=\"picks\">", editor.Text);
		Assert.DoesNotContain("selection1", editor.Text);

		// No model to select from, an unknown class, and an object that is not a list view are refused, each with its own reason.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="empty"/></child></object></interface>"""), editor.Error);
		Assert.False(editor.SetSelectionModel("empty", "GtkSingleSelection"));
		Assert.Contains("no model", editor.Refusal);
		Assert.False(editor.SetSelectionModel("empty", "GtkSortListModel"));
		Assert.Contains("not a selection model", editor.Refusal);
		Assert.False(editor.SetSelectionModel("w", "GtkSingleSelection"));
		Assert.Contains("Only a GtkListView", editor.Refusal);
		Assert.Null(editor.GetSelectionModel("w"));
	}

	static GtkUiDocumentEditor ViewEditor(string viewClass)
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkBox" id="box"/></child></object></interface>"""), editor.Error);
		Assert.True(editor.Add("box", viewClass), editor.Error);
		return editor;
	}

	[Fact]
	public void ModelWrappers_FilterSitsAboveTheDataAndSortAboveTheFilterWhateverTheOrder()
	{
		foreach (var sortFirst in new[] { true, false }) {
			var editor = ViewEditor("GtkListView");
			var view = editor.LastCreatedId!;
			Assert.Equal(new[] { "GtkSingleSelection", "GtkStringList" }, editor.GetModelChain(view));
			Assert.True(editor.AddModelWrapper(view, sortFirst ? GtkUiDocumentEditor.Sorting : GtkUiDocumentEditor.Filtering), editor.Error);
			Assert.True(editor.AddModelWrapper(view, sortFirst ? GtkUiDocumentEditor.Filtering : GtkUiDocumentEditor.Sorting), editor.Error);
			Assert.Equal(new[] { "GtkSingleSelection", "GtkSortListModel", "GtkFilterListModel", "GtkStringList" }, editor.GetModelChain(view));
			// The Items row still reaches the strings through all of it.
			Assert.Equal(new[] { "Item 1", "Item 2" }, editor.GetItems(view));
			Assert.Matches("<object class=\"GtkSortListModel\" id=\"sortModel1\">[\\s\\S]*?<property name=\"sorter\">\\s*<object class=\"GtkStringSorter\" id=\"stringSorter1\">\\s*<property name=\"expression\">\\s*<lookup type=\"GtkStringObject\" name=\"string\"", editor.Text);
			Assert.Matches("<object class=\"GtkFilterListModel\" id=\"filterModel1\">[\\s\\S]*?<property name=\"filter\">\\s*<object class=\"GtkStringFilter\" id=\"stringFilter1\">[\\s\\S]*?<property name=\"ignore-case\">true</property>", editor.Text);
		}
	}

	[Fact]
	public void ModelWrappers_AColumnViewsSortModelIsDrivenByItsColumnHeaders()
	{
		var editor = ViewEditor("GtkColumnView");
		var view = editor.LastCreatedId!;
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.Equal(new[] { "GtkSingleSelection", "GtkSortListModel", "GtkStringList" }, editor.GetModelChain(view));
		Assert.Matches("<object class=\"GtkSortListModel\"[^>]*>[\\s\\S]*?<binding name=\"sorter\">\\s*<lookup name=\"sorter\">" + view + "</lookup>", editor.Text);
		Assert.DoesNotContain("GtkStringSorter", editor.Text);
	}

	[Fact]
	public void ModelWrappers_AddRefuseDuplicatesAndWhatCannotBeWrapped()
	{
		var editor = ViewEditor("GtkGridView");
		var view = editor.LastCreatedId!;
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Filtering), editor.Error);
		Assert.False(editor.AddModelWrapper(view, GtkUiDocumentEditor.Filtering));
		Assert.Contains("already has a GtkFilterListModel", editor.Refusal);
		Assert.False(editor.AddModelWrapper(view, "slice"));
		Assert.Contains("not a model wrapper", editor.Refusal);
		Assert.False(editor.AddModelWrapper("box", GtkUiDocumentEditor.Sorting));
		Assert.Contains("Only a GtkListView", editor.Refusal);

		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkStringList" id="shared"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="byReference"><property name="model">shared</property></object></child><child><object class="GtkListView" id="empty"/></child></object></interface>"""), editor.Error);
		Assert.False(editor.AddModelWrapper("byReference", GtkUiDocumentEditor.Sorting));
		Assert.Contains("no inline model", editor.Refusal);
		Assert.False(editor.AddModelWrapper("empty", GtkUiDocumentEditor.Sorting));
		Assert.Contains("no inline model", editor.Refusal);
		Assert.Null(editor.GetModelChain("empty"));
		// A selection model around a reference: the reference is not wrapped through the selection model either.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkStringList" id="shared"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="view"><property name="model"><object class="GtkSingleSelection" id="pick"><property name="model">shared</property></object></property></object></child></object></interface>"""), editor.Error);
		Assert.False(editor.AddModelWrapper("view", GtkUiDocumentEditor.Sorting));
		Assert.Contains("no model to wrap", editor.Refusal);

		// A selection model with no model of its own: refused, and the refusal must not leave an empty property behind
		// for the next edit to serialise.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="view"><property name="model"><object class="GtkSingleSelection" id="pick"/></property></object></child></object></interface>"""), editor.Error);
		Assert.False(editor.AddModelWrapper("view", GtkUiDocumentEditor.Filtering));
		Assert.Contains("no model to wrap", editor.Refusal);
		Assert.True(editor.SetSelectionModel("view", "GtkMultiSelection"), editor.Error); // any edit serialises the tree
		Assert.DoesNotContain("<property name=\"model\"", editor.Text.Substring(editor.Text.IndexOf("id=\"pick\"", StringComparison.Ordinal)));
	}

	[Fact]
	public void ModelWrappers_MenuCommandsMapToTheirKindAndDirection()
	{
		Assert.Equal((GtkUiDocumentEditor.Sorting, true), GtkUiDocumentEditor.ModelCommand("model-add-sort"));
		Assert.Equal((GtkUiDocumentEditor.Sorting, false), GtkUiDocumentEditor.ModelCommand("model-remove-sort"));
		Assert.Equal((GtkUiDocumentEditor.Filtering, true), GtkUiDocumentEditor.ModelCommand("model-add-filter"));
		Assert.Equal((GtkUiDocumentEditor.Filtering, false), GtkUiDocumentEditor.ModelCommand("model-remove-filter"));
		Assert.Null(GtkUiDocumentEditor.ModelCommand("model-add-slice"));
		Assert.Null(GtkUiDocumentEditor.ModelCommand("selection-GtkNoSelection"));
	}

	[Fact]
	public void ModelWrappers_ABareListIsWrappedDirectly()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="view"><property name="model"><object class="GtkStringList" id="strings"><items><item>One</item></items></object></property></object></child></object></interface>"""), editor.Error);
		Assert.True(editor.AddModelWrapper("view", GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.Equal(new[] { "GtkSortListModel", "GtkStringList" }, editor.GetModelChain("view"));
	}

	[Fact]
	public void ModelWrappers_RemoveLeavesWhatTheyWrappedAndRefusesAMissingOne()
	{
		var editor = ViewEditor("GtkListView");
		var view = editor.LastCreatedId!;
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Filtering), editor.Error);
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.True(editor.RemoveModelWrapper(view, GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.Equal(new[] { "GtkSingleSelection", "GtkFilterListModel", "GtkStringList" }, editor.GetModelChain(view));
		Assert.DoesNotContain("GtkStringSorter", editor.Text);
		Assert.False(editor.RemoveModelWrapper(view, GtkUiDocumentEditor.Sorting));
		Assert.Contains("no GtkSortListModel to remove", editor.Refusal);
		Assert.True(editor.RemoveModelWrapper(view, GtkUiDocumentEditor.Filtering), editor.Error);
		Assert.Equal(new[] { "GtkSingleSelection", "GtkStringList" }, editor.GetModelChain(view));
		Assert.Equal(new[] { "Item 1", "Item 2" }, editor.GetItems(view));
		Assert.True(editor.Undo());
		Assert.Equal(new[] { "GtkSingleSelection", "GtkFilterListModel", "GtkStringList" }, editor.GetModelChain(view));

		// A wrapper with nothing inside takes its property with it, not leaving an empty model property on the view.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="hollow"><property name="model"><object class="GtkSortListModel" id="s"><property name="model"/></object></property></object></child><child><object class="GtkListView" id="bare"><property name="model"><object class="GtkSortListModel" id="t"/></property></object></child></object></interface>"""), editor.Error);
		Assert.True(editor.RemoveModelWrapper("hollow", GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.True(editor.RemoveModelWrapper("bare", GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.DoesNotContain("<property name=\"model\"", editor.Text);
		Assert.DoesNotContain("GtkSortListModel", editor.Text);

		// A wrapper around a reference gives the reference back to the property that held the wrapper.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkStringList" id="shared"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="byRef"><property name="model"><object class="GtkSortListModel" id="sorted"><property name="model">shared</property></object></property></object></child></object></interface>"""), editor.Error);
		Assert.True(editor.RemoveModelWrapper("byRef", GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.Matches("<object class=\"GtkListView\" id=\"byRef\">\\s*<property name=\"model\">shared</property>", editor.Text);
	}

	[Fact]
	public void ColumnSort_MakingAColumnSortableAddsASorterAndTheViewsSortModelInOneUndoStep()
	{
		var editor = ViewEditor("GtkColumnView");
		var view = editor.LastCreatedId!;
		Assert.Equal(false, editor.GetColumnSortable("column1"));
		Assert.Equal(new[] { "GtkSingleSelection", "GtkStringList" }, editor.GetModelChain(view));
		Assert.True(editor.SetColumnSortable("column1", true), editor.Error);
		Assert.Equal(true, editor.GetColumnSortable("column1"));
		Assert.Equal(new[] { "GtkSingleSelection", "GtkSortListModel", "GtkStringList" }, editor.GetModelChain(view));
		Assert.Matches("<object class=\"GtkColumnViewColumn\" id=\"column1\">[\\s\\S]*?<property name=\"sorter\">\\s*<object class=\"GtkStringSorter\" id=\"stringSorter1\">\\s*<property name=\"expression\">\\s*<lookup type=\"GtkStringObject\" name=\"string\"", editor.Text);
		Assert.Matches("<binding name=\"sorter\">\\s*<lookup name=\"sorter\">" + view + "</lookup>", editor.Text);
		Assert.True(editor.Undo());
		Assert.Equal(false, editor.GetColumnSortable("column1"));
		Assert.Equal(new[] { "GtkSingleSelection", "GtkStringList" }, editor.GetModelChain(view));
		Assert.DoesNotContain("GtkStringSorter", editor.Text);
	}

	[Fact]
	public void ColumnSort_AnExistingSortModelIsKeptAndEachColumnGetsItsOwnSorter()
	{
		var editor = ViewEditor("GtkColumnView");
		var view = editor.LastCreatedId!;
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.True(editor.Add(view, "GtkColumnViewColumn"), editor.Error);
		var second = editor.LastCreatedId!;
		Assert.True(editor.SetColumnSortable("column1", true), editor.Error);
		Assert.True(editor.SetColumnSortable(second, true), editor.Error);
		Assert.Equal(new[] { "GtkSingleSelection", "GtkSortListModel", "GtkStringList" }, editor.GetModelChain(view));
		Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(editor.Text, "<object class=\"GtkStringSorter\"").Count);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(editor.Text, "<object class=\"GtkSortListModel\""));
		// Making a sortable column sortable again is not an edit.
		var before = editor.Text;
		Assert.True(editor.SetColumnSortable("column1", true), editor.Error);
		Assert.Equal(before, editor.Text);
		// Disabling removes only the column's sorter and leaves the sort model (the other column still uses it).
		Assert.True(editor.SetColumnSortable("column1", false), editor.Error);
		Assert.Equal(false, editor.GetColumnSortable("column1"));
		Assert.Equal(true, editor.GetColumnSortable(second));
		Assert.Equal(new[] { "GtkSingleSelection", "GtkSortListModel", "GtkStringList" }, editor.GetModelChain(view));
		Assert.True(editor.SetColumnSortable("column1", false), editor.Error); // already not sortable: nothing to do
	}

	[Fact]
	public void ColumnSort_NeedsStringItemsOrAModelItCannotSeeAndAColumnOfAColumnView()
	{
		var editor = new GtkUiDocumentEditor();
		// An inline model that is not a string list: the item type is unknown, so no string sorter is invented.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkColumnView" id="view"><property name="model"><object class="GtkSingleSelection" id="pick"><property name="model"><object class="GListStore" id="store"/></property></object></property><child><object class="GtkColumnViewColumn" id="col"/></child></object></child></object></interface>"""), editor.Error);
		var before = editor.Text;
		Assert.False(editor.SetColumnSortable("col", true));
		Assert.Contains("not strings", editor.Refusal);
		Assert.Equal(before, editor.Text);
		Assert.Equal(false, editor.GetColumnSortable("col"));
		// A model reference cannot be inspected or wrapped: the sorter is added and the model is assumed to be wired where it is defined.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkSortListModel" id="shared"/><object class="GtkWindow" id="w"><child><object class="GtkColumnView" id="view"><property name="model">shared</property><child><object class="GtkColumnViewColumn" id="col"/></child></object></child></object></interface>"""), editor.Error);
		Assert.True(editor.SetColumnSortable("col", true), editor.Error);
		Assert.Equal(true, editor.GetColumnSortable("col"));
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(editor.Text, "<object class=\"GtkSortListModel\""));
		// Anything that is not a column of a column view is refused.
		Assert.False(editor.SetColumnSortable("w", true));
		Assert.Contains("Only a GtkColumnViewColumn", editor.Refusal);
		Assert.Null(editor.GetColumnSortable("w"));
		Assert.Null(editor.GetColumnSortable("nothing"));
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="list"/></child></object></interface>"""), editor.Error);
		Assert.Null(editor.GetColumnSortable("list"));
	}

	[Fact]
	public void ColumnSort_MenuCommandsMapToTheirDirection()
	{
		Assert.Equal(true, GtkUiDocumentEditor.ColumnSortCommand("column-sortable-on"));
		Assert.Equal(false, GtkUiDocumentEditor.ColumnSortCommand("column-sortable-off"));
		Assert.Null(GtkUiDocumentEditor.ColumnSortCommand("model-add-sort"));
		Assert.Null(GtkUiDocumentEditor.ColumnSortCommand("column-sortable"));
	}

	[Fact]
	public void Factory_ASignalFactoryReplacesTheTemplateAndSwitchingBackDropsItsSignals()
	{
		var editor = ViewEditor("GtkListView");
		var view = editor.LastCreatedId!;
		Assert.Equal(GtkUiDocumentEditor.BuilderFactory, editor.GetFactoryClass(view));
		Assert.True(editor.SetFactoryClass(view, GtkUiDocumentEditor.SignalFactory), editor.Error);
		Assert.Equal(GtkUiDocumentEditor.SignalFactory, editor.GetFactoryClass(view));
		Assert.Matches("<property name=\"factory\">\\s*<object class=\"GtkSignalListItemFactory\" id=\"signalFactory1\">\\s*<signal name=\"setup\" handler=\"signalFactory1_setup\" />\\s*<signal name=\"bind\" handler=\"signalFactory1_bind\" />", editor.Text);
		Assert.DoesNotContain("GtkBuilderListItemFactory", editor.Text);
		// The factory object is in the tree, so its own events are editable in the Properties pad.
		Assert.Contains(editor.Roots[0].Children[0].Children[0].Children, node => node.ClassName == "GtkSignalListItemFactory" && node.Id == "signalFactory1");
		Assert.True(editor.Undo());
		Assert.Equal(GtkUiDocumentEditor.BuilderFactory, editor.GetFactoryClass(view));
		Assert.True(editor.Redo());
		Assert.True(editor.SetFactoryClass(view, GtkUiDocumentEditor.BuilderFactory), editor.Error);
		Assert.Equal(GtkUiDocumentEditor.BuilderFactory, editor.GetFactoryClass(view));
		Assert.DoesNotContain("signalFactory1", editor.Text);
		Assert.Contains("<property name=\"bytes\">&lt;interface&gt;", editor.Text);
		// Choosing the class it already has is not an edit.
		var before = editor.Text;
		Assert.True(editor.SetFactoryClass(view, GtkUiDocumentEditor.BuilderFactory), editor.Error);
		Assert.Equal(before, editor.Text);
	}

	[Fact]
	public void Factory_AColumnHasItsOwnAndTheColumnViewRefusesAndWhatCannotBeSwitchedIsRefused()
	{
		var editor = ViewEditor("GtkColumnView");
		var view = editor.LastCreatedId!;
		Assert.Equal(GtkUiDocumentEditor.BuilderFactory, editor.GetFactoryClass("column1"));
		Assert.Null(editor.GetFactoryClass(view)); // the column view has no factory of its own
		Assert.False(editor.SetFactoryClass(view, GtkUiDocumentEditor.SignalFactory));
		Assert.Contains("select a column", editor.Refusal);
		Assert.True(editor.SetFactoryClass("column1", GtkUiDocumentEditor.SignalFactory), editor.Error);
		Assert.Equal(GtkUiDocumentEditor.SignalFactory, editor.GetFactoryClass("column1"));
		Assert.False(editor.SetFactoryClass("column1", "GtkListItemFactory"));
		Assert.Contains("is not a list-item factory the designer switches to", editor.Refusal);
		Assert.False(editor.SetFactoryClass("w", GtkUiDocumentEditor.SignalFactory));
		Assert.Contains("Only a GtkListView", editor.Refusal);
		Assert.Null(editor.GetFactoryClass("nothing"));

		// A factory that is a reference to an object defined elsewhere is not replaced; a missing factory is created.
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkSignalListItemFactory" id="shared"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="byReference"><property name="factory">shared</property></object></child><child><object class="GtkGridView" id="bare"/></child></object></interface>"""), editor.Error);
		var before = editor.Text;
		Assert.False(editor.SetFactoryClass("byReference", GtkUiDocumentEditor.BuilderFactory));
		Assert.Contains("reference", editor.Refusal);
		Assert.Equal(before, editor.Text);
		Assert.Null(editor.GetFactoryClass("byReference"));
		Assert.True(editor.SetFactoryClass("bare", GtkUiDocumentEditor.SignalFactory), editor.Error);
		Assert.Equal(GtkUiDocumentEditor.SignalFactory, editor.GetFactoryClass("bare"));
		Assert.Matches("<object class=\"GtkSignalListItemFactory\" id=\"signalFactory1\">", editor.Text);
	}

	[Fact]
	public void Factory_MenuCommandsMapToTheirClass()
	{
		Assert.Equal("GtkBuilderListItemFactory", GtkUiDocumentEditor.FactoryCommand("factory-GtkBuilderListItemFactory"));
		Assert.Equal("GtkSignalListItemFactory", GtkUiDocumentEditor.FactoryCommand("factory-GtkSignalListItemFactory"));
		Assert.Null(GtkUiDocumentEditor.FactoryCommand("factory-GtkListItemFactory"));
		Assert.Null(GtkUiDocumentEditor.FactoryCommand("edit-factory-template"));
	}

	[Fact]
	public void ListStore_TheDataModelSwitchesKeepingItsIdAndTheWrappersAround()
	{
		var editor = ViewEditor("GtkListView");
		var view = editor.LastCreatedId!;
		Assert.Equal("GtkStringList", editor.GetModelData(view));
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Filtering), editor.Error);
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Sorting), editor.Error);
		Assert.True(editor.SetModelData(view, GtkUiDocumentEditor.ListStoreModel), editor.Error);
		Assert.Equal(new[] { "GtkSingleSelection", "GtkSortListModel", "GtkFilterListModel", "GListStore" }, editor.GetModelChain(view));
		Assert.Equal("GListStore", editor.GetModelData(view));
		Assert.Matches("<object class=\"GListStore\" id=\"listModel1\">\\s*<property name=\"item-type\">GtkStringObject</property>", editor.Text); // the id is kept
		Assert.DoesNotContain("<item", editor.Text.Replace("<items>", ""));
		Assert.Null(editor.GetItems(view)); // a store has no Items row: the code fills it
		Assert.True(editor.Undo());
		Assert.Equal(new[] { "Item 1", "Item 2" }, editor.GetItems(view));
		Assert.True(editor.Redo());
		Assert.True(editor.SetModelData(view, GtkUiDocumentEditor.StringListModel), editor.Error);
		Assert.Equal(new[] { "GtkSingleSelection", "GtkSortListModel", "GtkFilterListModel", "GtkStringList" }, editor.GetModelChain(view));
		Assert.Equal(new[] { "Item 1", "Item 2" }, editor.GetItems(view));
		Assert.Contains("id=\"listModel1\"", editor.Text);
		var before = editor.Text;
		Assert.True(editor.SetModelData(view, GtkUiDocumentEditor.StringListModel), editor.Error); // already: not an edit
		Assert.Equal(before, editor.Text);
	}

	[Fact]
	public void ListStore_ItemTypeIsSetFromTheStoreOrItsViewAndValidated()
	{
		var editor = ViewEditor("GtkListView");
		var view = editor.LastCreatedId!;
		Assert.Null(editor.GetItemType(view)); // a string list is not a store
		Assert.False(editor.SetItemType(view, "MyApp_Item"));
		Assert.Contains("no GListStore", editor.Refusal);
		Assert.True(editor.SetModelData(view, GtkUiDocumentEditor.ListStoreModel), editor.Error);
		Assert.Equal("GtkStringObject", editor.GetItemType(view));
		Assert.True(editor.SetItemType(view, "MyApp_Item"), editor.Error);
		Assert.Equal("MyApp_Item", editor.GetItemType("listModel1")); // read from the store itself too
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(editor.Text, "name=\"item-type\""));
		Assert.True(editor.SetItemType("listModel1", "Other_Type"), editor.Error); // and set through it
		Assert.Equal("Other_Type", editor.GetItemType(view));
		Assert.False(editor.SetItemType(view, "not a type"));
		Assert.Contains("not a GType name", editor.Refusal);
		Assert.Equal("Other_Type", editor.GetItemType(view));
		Assert.True(editor.SetItemType(view, ""), editor.Error); // empty: the default, GObject
		Assert.Equal("", editor.GetItemType(view));
		Assert.DoesNotContain("item-type", editor.Text);
		var before = editor.Text;
		Assert.True(editor.SetItemType(view, ""), editor.Error);
		Assert.Equal(before, editor.Text);
	}

	[Fact]
	public void ListStore_StringSortersAndFiltersNeedStringItems()
	{
		var editor = ViewEditor("GtkListView");
		var view = editor.LastCreatedId!;
		Assert.True(editor.SetModelData(view, GtkUiDocumentEditor.ListStoreModel), editor.Error);
		// A store of GtkStringObject items reads like a string list.
		Assert.True(editor.AddModelWrapper(view, GtkUiDocumentEditor.Filtering), editor.Error);
		Assert.True(editor.RemoveModelWrapper(view, GtkUiDocumentEditor.Filtering), editor.Error);
		// Items of an application type: no string expression is invented.
		Assert.True(editor.SetItemType(view, "MyApp_Item"), editor.Error);
		Assert.False(editor.AddModelWrapper(view, GtkUiDocumentEditor.Sorting));
		Assert.Contains("not strings", editor.Refusal);
		Assert.False(editor.AddModelWrapper(view, GtkUiDocumentEditor.Filtering));

		var columns = ViewEditor("GtkColumnView");
		var columnView = columns.LastCreatedId!;
		Assert.True(columns.SetModelData(columnView, GtkUiDocumentEditor.ListStoreModel), columns.Error);
		Assert.True(columns.SetItemType(columnView, "MyApp_Item"), columns.Error);
		Assert.False(columns.SetColumnSortable("column1", true));
		Assert.Contains("not strings", columns.Refusal);
		Assert.False(columns.AddModelWrapper(columnView, GtkUiDocumentEditor.Filtering));
		// The column view's own sort model reads nothing, so it can still be added.
		Assert.True(columns.AddModelWrapper(columnView, GtkUiDocumentEditor.Sorting), columns.Error);
		Assert.True(columns.SetItemType(columnView, "GtkStringObject"), columns.Error);
		Assert.True(columns.SetColumnSortable("column1", true), columns.Error);
	}

	[Fact]
	public void ListStore_RefusesWhatItCannotSwitch()
	{
		var editor = new GtkUiDocumentEditor();
		Assert.True(editor.Reset("""<interface><requires lib="gtk" version="4.0"/><object class="GtkStringList" id="shared"/><object class="GtkWindow" id="w"><child><object class="GtkListView" id="byReference"><property name="model">shared</property></object></child><child><object class="GtkListView" id="empty"/></child><child><object class="GtkListView" id="odd"><property name="model"><object class="GtkSingleSelection" id="pick"><property name="model"><object class="GtkSliceListModel" id="slice"/></property></object></property></object></child></object></interface>"""), editor.Error);
		var before = editor.Text;
		Assert.False(editor.SetModelData("byReference", GtkUiDocumentEditor.ListStoreModel));
		Assert.Contains("no inline model", editor.Refusal);
		Assert.False(editor.SetModelData("empty", GtkUiDocumentEditor.ListStoreModel));
		Assert.False(editor.SetModelData("w", GtkUiDocumentEditor.ListStoreModel));
		Assert.Contains("Only a GtkListView", editor.Refusal);
		Assert.False(editor.SetModelData("byReference", "GtkSortListModel"));
		Assert.Contains("not a data model", editor.Refusal);
		Assert.Equal(before, editor.Text);
		Assert.Null(editor.GetModelData("byReference"));
		Assert.Null(editor.GetModelData("w"));
		// A wrapper with no model of its own ends the chain: its innermost model is the wrapper itself... which is not a data model.
		Assert.Null(editor.GetModelData("odd"));
	}

	[Fact]
	public void ListStore_MenuCommandsMapToTheirClass()
	{
		Assert.Equal("GtkStringList", GtkUiDocumentEditor.ModelDataCommand("model-data-GtkStringList"));
		Assert.Equal("GListStore", GtkUiDocumentEditor.ModelDataCommand("model-data-GListStore"));
		Assert.Null(GtkUiDocumentEditor.ModelDataCommand("model-data-GtkSortListModel"));
		Assert.Null(GtkUiDocumentEditor.ModelDataCommand("model-add-sort"));
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
