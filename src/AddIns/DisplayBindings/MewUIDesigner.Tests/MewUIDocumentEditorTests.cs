using ICSharpCode.MewUIDesigner;
using Xunit;

public sealed class MewUIDocumentEditorTests
{
	const string Source = """
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
partial class MainWindow
{
    private StackPanel rootPanel = null!;
    private Label title = null!;
    private Button saveButton = null!;
    private void InitializeComponent()
    {
        rootPanel = new StackPanel();
        title = new Label();
        saveButton = new Button();
        Title = "Test";
        title.Text = "Hello";
        saveButton.Content = "Save";
        rootPanel.Children(title, saveButton);
        Content = rootPanel;
    }
}
""";

	[Fact] public void Parse_BuildsFluentVisualTree()
	{
		var editor = new MewUIDocumentEditor();
		Assert.True(editor.Reset(Source), editor.Error);
		Assert.Equal("Window", editor.Roots[0].Type);
		Assert.Equal("MainWindow", editor.WindowClassName);
		Assert.Equal("rootPanel", editor.Roots[0].Children[0].Name);
		Assert.Equal(2, editor.Roots[0].Children[0].Children.Count);
	}

	[Fact] public void SetProperty_RoundTripsAndSupportsUndoRedo()
	{
		var editor = new MewUIDocumentEditor(); editor.Reset(Source);
		var button = editor.Roots[0].Children[0].Children[1];
		Assert.True(editor.SetProperty(button.Id, "Content", "Publish"));
		Assert.Contains("Content = \"Publish\"", editor.Text);
		Assert.True(editor.Undo()); Assert.Contains("Content = \"Save\"", editor.Text);
		Assert.True(editor.Redo()); Assert.Contains("Content = \"Publish\"", editor.Text);
	}

	[Fact] public void AddAndRemove_ProduceValidSource()
	{
		var editor = new MewUIDocumentEditor(); editor.Reset(Source);
		var panel = editor.Roots[0].Children[0];
		Assert.True(editor.AddElement(panel.Id, "TextBox"));
		Assert.Contains("private TextBox textBox1", editor.Text);
		Assert.Contains("textBox1 = new TextBox", editor.Text); Assert.DoesNotContain("this.", editor.Text); Assert.Equal(5, Flatten(editor.Roots));
		var added = editor.Roots[0].Children[0].Children.Single(n => n.Type == "TextBox");
		Assert.True(editor.Rename(added.Id, "searchBox"));
		Assert.Contains("private TextBox searchBox", editor.Text);
		Assert.Contains("searchBox = new TextBox", editor.Text);
		added = editor.Roots[0].Children[0].Children.Single(n => n.Type == "TextBox");
		Assert.True(editor.Remove(added.Id)); Assert.DoesNotContain("new TextBox", editor.Text); Assert.DoesNotContain("TextBox searchBox", editor.Text);
	}

	[Fact] public void InvalidSource_ReportsDiagnosticWithoutThrowing()
	{
		var editor = new MewUIDocumentEditor(); Assert.False(editor.Reset("new Window(")); Assert.NotEmpty(editor.Error);
	}

	// Regression (doc/technotes/addin-sdk.md sibling acceptance, 2026-08): a numeric-looking
	// value on a STRING property must stay a quoted literal - "123" used to be emitted as
	// `title.Text = 123;`, which stopped the generated code from compiling.
	[Fact] public void SetProperty_NumericLookingValueOnTextProperty_StaysStringLiteral()
	{
		var editor = new MewUIDocumentEditor(); editor.Reset(Source);
		var label = editor.Roots[0].Children[0].Children[0];
		Assert.True(editor.SetProperty(label.Id, "Text", "123"), editor.Error);
		Assert.Contains(".Text = \"123\";", editor.Text);
		Assert.DoesNotContain(".Text = 123;", editor.Text);
	}

	[Fact] public void SetProperty_OnLayoutProperty_EmitsNumericLiteral()
	{
		var editor = new MewUIDocumentEditor(); editor.Reset(Source);
		var panel = editor.Roots[0].Children[0];
		Assert.True(editor.SetProperty(panel.Id, "Spacing", "8"), editor.Error);
		Assert.Contains(".Spacing = 8;", editor.Text);
	}

	// Regression: dropping a control "onto" a leaf used to generate `<leaf>.Children(...)`
	// calls that MewUI does not have, breaking compilation. The editor must refuse leaf parents.
	// Regression: property values were read from raw source text with a hand-rolled unquote
	// that only handled \\" - every other escape sequence surfaced as literal backslash noise
	// in the Properties pad. Values now come from the decoded literal token.
	[Fact] public void SetProperty_EscapesAreDecodedInPropertyPad()
	{
		var escaped = Source.Replace("title.Text = \"Hello\";", "title.Text = \"Line\\nBreak\";");
		var editor = new MewUIDocumentEditor(); editor.Reset(escaped);
		var label = editor.Roots[0].Children[0].Children[0];
		Assert.Equal("Line\nBreak", label.Properties["Text"]); // decoded to a REAL newline
	}

	[Fact] public void AddElement_IntoLeafControl_IsRejected()
	{
		var editor = new MewUIDocumentEditor(); editor.Reset(Source);
		var saveButton = editor.Roots[0].Children[0].Children[1];
		var elementsBefore = Flatten(editor.Roots);
		Assert.False(editor.AddElement(saveButton.Id, "TextBox"));
		Assert.Equal(elementsBefore, Flatten(editor.Roots));
	}

	[Fact] public void LegacyNestedFluentShape_IsRejected()
	{
		const string legacy = "partial class MainWindow { void InitializeComponent() { this.Content = this.root = new StackPanel().Children(this.button = new Button()); } }";
		var editor = new MewUIDocumentEditor();
		Assert.False(editor.Reset(legacy));
		Assert.Contains("Content must be assigned to a generated control field", editor.Error);
	}

	static int Flatten(IEnumerable<MewUIElementNode> nodes) => nodes.Sum(n => 1 + Flatten(n.Children));
}
