using ICSharpCode.GtkDesigner;
using ICSharpCode.SharpDevelop.Designer.Shell;
using Xunit;

/// <summary>A toolbox item dropped onto the .ui source text: the XML-level planner, and the GtkBuilder
/// insertion built on it.</summary>
public sealed class GtkSourceDropTests
{
	const string Source = """
<?xml version="1.0" encoding="UTF-8"?>
<interface>
  <requires lib="gtk" version="4.0" />
  <object class="GtkApplicationWindow" id="mainWindow">
    <property name="title">Example</property>
    <child>
      <object class="GtkBox" id="contentBox">
        <property name="orientation">vertical</property>
        <child>
          <object class="GtkLabel" id="heading"><property name="label">Hello</property></object>
        </child>
        <child>
          <object class="GtkButton" id="runButton"><property name="label">Run</property></object>
        </child>
      </object>
    </child>
  </object>
</interface>
""";

	static int At(string marker, int delta = 0) => Source.IndexOf(marker, StringComparison.Ordinal) + delta;
	static string[] ChildIds(GtkUiDocumentEditor editor, string id)
	{
		static GtkUiNode? Find(IEnumerable<GtkUiNode> nodes, string id) { foreach (var n in nodes) { if (n.Id == id) return n; if (Find(n.Children, id) is { } f) return f; } return null; }
		return Find(editor.Roots, id)!.Children.Select(c => c.Id).ToArray();
	}

	[Fact] public void ParseRecordsTagOffsets()
	{
		var root = XmlToolboxDropPlanner.Parse(Source)!;
		Assert.Equal("interface", root.Name);
		var window = root.Children.Single(c => c.Name == "object");
		Assert.Equal(At("<object class=\"GtkApplicationWindow\""), window.Start);
		Assert.Equal('>', Source[window.StartTagEnd - 1]);
		Assert.EndsWith("</object>", Source[..window.End]);
	}

	[Fact] public void MalformedTextPlansNothing()
		=> Assert.Null(XmlToolboxDropPlanner.Plan("<interface><object>", 5, _ => true));

	[Fact] public void DropOnTheLabelTextGoesAfterTheLabelInsideTheBox()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source));
		Assert.True(editor.AddAt(At(">Hello<", 2), "GtkSwitch"));
		Assert.Equal(new[] { "heading", "switch1", "runButton" }, ChildIds(editor, "contentBox"));
	}

	[Fact] public void DropOnAChildStartTagGoesBeforeThatChild()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source));
		Assert.True(editor.AddAt(At("<object class=\"GtkButton\"", 3), "GtkSwitch"));
		Assert.Equal(new[] { "heading", "switch1", "runButton" }, ChildIds(editor, "contentBox"));
	}

	[Fact] public void DropBetweenChildrenGoesThere()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source));
		// The whitespace just before the second <child>.
		var secondChild = Source.IndexOf("<child>", At("id=\"heading\""), StringComparison.Ordinal);
		Assert.True(editor.AddAt(secondChild - 1, "GtkSwitch"));
		Assert.Equal(new[] { "heading", "switch1", "runButton" }, ChildIds(editor, "contentBox"));
	}

	[Fact] public void DropOnTheBoxsOwnPropertyGoesFirst()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source));
		Assert.True(editor.AddAt(At(">vertical<", 3), "GtkSwitch"));
		// After the <property> - which is not a <child>, so the new widget is still the first child.
		Assert.Equal(new[] { "switch1", "heading", "runButton" }, ChildIds(editor, "contentBox"));
	}

	[Fact] public void DropOutsideEveryContainerIsRefused()
	{
		var editor = new GtkUiDocumentEditor(); Assert.True(editor.Reset(Source));
		Assert.False(editor.AddAt(At("<requires", 2), "GtkSwitch"));
		Assert.Equal(Source, editor.Text);
	}

	[Fact] public void InsertElementIndentsLikeItsSiblings()
	{
		const string text = "<Window>\n  <StackPanel>\n    <Label/>\n  </StackPanel>\n</Window>\n";
		var point = XmlToolboxDropPlanner.Plan(text, text.IndexOf("<Label/>", StringComparison.Ordinal) + 3, e => e.Name == "StackPanel")!;
		var (offset, insertion) = XmlToolboxDropPlanner.InsertElement(text, point, "<Button />");
		Assert.Equal("<Window>\n  <StackPanel>\n    <Label/>\n    <Button />\n  </StackPanel>\n</Window>\n", text.Insert(offset, insertion));
	}

	[Fact] public void InsertElementIntoAnEmptyOneLineContainerKeepsTheEndTagOnItsOwnLine()
	{
		const string text = "<Window>\n  <StackPanel></StackPanel>\n</Window>\n";
		var point = XmlToolboxDropPlanner.Plan(text, text.IndexOf("></StackPanel>", StringComparison.Ordinal) + 1, e => e.Name == "StackPanel")!;
		var (offset, insertion) = XmlToolboxDropPlanner.InsertElement(text, point, "<Button />");
		Assert.Equal("<Window>\n  <StackPanel>\n    <Button />\n  </StackPanel>\n</Window>\n", text.Insert(offset, insertion));
	}
}
