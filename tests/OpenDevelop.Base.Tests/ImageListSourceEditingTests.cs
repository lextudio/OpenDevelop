using System.Text;
using ICSharpCode.FormsDesigner.Services;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ImageListSourceEditingTests
{
	const string CSharpWithStream = """
		partial class Form1
		{
		    private void InitializeComponent()
		    {
		        this.components = new System.ComponentModel.Container();
		        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
		        this.imageList1 = new System.Windows.Forms.ImageList(this.components);
		        this.button1 = new System.Windows.Forms.Button();
		        this.imageList1.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageList1.ImageStream")));
		        this.imageList1.TransparentColor = System.Drawing.Color.Transparent;
		        this.imageList1.Images.SetKeyName(0, "old0.png");
		        this.imageList1.Images.SetKeyName(1, "old1.png");
		        this.button1.Name = "button1";
		    }
		}
		""";

	const string CSharpWithoutStream = """
		partial class Form1
		{
		    private void InitializeComponent()
		    {
		        this.imageList1 = new System.Windows.Forms.ImageList();
		        this.button1 = new System.Windows.Forms.Button();
		        this.button1.Name = "button1";
		    }
		}
		""";

	const string VbWithStream = """
		Partial Class Form1
		    Private Sub InitializeComponent()
		        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(Form1))
		        Me.imageList1 = New System.Windows.Forms.ImageList()
		        Me.imageList1.ImageStream = CType(resources.GetObject("imageList1.ImageStream"), System.Windows.Forms.ImageListStreamer)
		        Me.imageList1.Images.SetKeyName(0, "old0.png")
		        Me.button1 = New System.Windows.Forms.Button()
		    End Sub
		End Class
		""";

	const string VbWithoutStream = """
		Partial Class Form1
		    Private Sub InitializeComponent()
		        Me.imageList1 = New System.Windows.Forms.ImageList()
		        Me.button1 = New System.Windows.Forms.Button()
		    End Sub
		End Class
		""";

	static int Count(string text, string needle) => System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(needle)).Count;

	[Fact]
	public void CSharp_ReplacesTheKeysKeepsTheStreamBindingAndSkipsUnnamedImages()
	{
		Assert.True(ImageListSourceEditing.TryRewriteCSharp(CSharpWithStream, "imageList1", "imageList1.ImageStream", new[] { "x.png", "", "z.png" }, out var text, out var error), error);

		Assert.Equal(1, Count(text, "ImageStream ="));
		Assert.Equal(1, Count(text, "GetObject(\"imageList1.ImageStream\")"));
		Assert.Contains("this.imageList1.Images.SetKeyName(0, \"x.png\");", text, System.StringComparison.Ordinal);
		Assert.Contains("this.imageList1.Images.SetKeyName(2, \"z.png\");", text, System.StringComparison.Ordinal);
		Assert.DoesNotContain("old0.png", text, System.StringComparison.Ordinal);
		Assert.DoesNotContain("SetKeyName(1,", text, System.StringComparison.Ordinal);
		// Untouched neighbours, in place: the keys follow the stream binding, before the next statement.
		Assert.True(text.IndexOf("TransparentColor", System.StringComparison.Ordinal) > text.IndexOf("ImageStream =", System.StringComparison.Ordinal));
		Assert.Contains("this.button1.Name = \"button1\";", text, System.StringComparison.Ordinal);
		Assert.Equal(1, Count(text, "ComponentResourceManager resources"));
	}

	[Fact]
	public void CSharp_AddsTheBindingAfterTheCreationAndDeclaresResourcesWhenTheFormHasNone()
	{
		Assert.True(ImageListSourceEditing.TryRewriteCSharp(CSharpWithoutStream, "imageList1", "imageList1.ImageStream", new[] { "a.png" }, out var text, out var error), error);

		Assert.Contains("var resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));", text, System.StringComparison.Ordinal);
		var creation = text.IndexOf("this.imageList1 = new", System.StringComparison.Ordinal);
		var binding = text.IndexOf("this.imageList1.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject(\"imageList1.ImageStream\")));", System.StringComparison.Ordinal);
		var key = text.IndexOf("this.imageList1.Images.SetKeyName(0, \"a.png\");", System.StringComparison.Ordinal);
		Assert.True(creation >= 0 && binding > creation && key > binding, text);
		Assert.True(text.IndexOf("var resources", System.StringComparison.Ordinal) < creation);
	}

	[Fact]
	public void CSharp_AnEmptyListRemovesTheBindingAndEveryKey()
	{
		Assert.True(ImageListSourceEditing.TryRewriteCSharp(CSharpWithStream, "imageList1", "imageList1.ImageStream", System.Array.Empty<string>(), out var text, out var error), error);
		Assert.DoesNotContain("ImageStream", text, System.StringComparison.Ordinal);
		Assert.DoesNotContain("SetKeyName", text, System.StringComparison.Ordinal);
		Assert.Contains("TransparentColor", text, System.StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_RewritesOnlyRequestedImageListSettings()
	{
		Assert.True(ImageListSourceEditing.TryRewriteCSharpSettings(CSharpWithStream, "imageList1", 24, 32, "Depth8Bit", out var text, out var error), error);
		Assert.Contains("this.imageList1.ImageSize = new System.Drawing.Size(24, 32);", text, System.StringComparison.Ordinal);
		Assert.Contains("this.imageList1.ColorDepth = System.Windows.Forms.ColorDepth.Depth8Bit;", text, System.StringComparison.Ordinal);
		Assert.Equal(1, Count(text, "ImageSize ="));
		Assert.Equal(1, Count(text, "ColorDepth ="));

		Assert.True(ImageListSourceEditing.TryRewriteCSharpSettings(text, "imageList1", null, null, "Depth32Bit", out var depthOnly, out error), error);
		Assert.Contains("new System.Drawing.Size(24, 32)", depthOnly, System.StringComparison.Ordinal);
		Assert.Contains("ColorDepth.Depth32Bit", depthOnly, System.StringComparison.Ordinal);
	}

	[Fact]
	public void CSharp_RefusesAForeignStreamKeyAMissingListAndBrokenSource()
	{
		Assert.False(ImageListSourceEditing.TryRewriteCSharp(CSharpWithStream, "imageList1", "other.ImageStream", new[] { "a" }, out var unchanged, out var foreign));
		Assert.Equal(CSharpWithStream, unchanged);
		Assert.Contains("different resource key", foreign, System.StringComparison.Ordinal);
		Assert.False(ImageListSourceEditing.TryRewriteCSharp(CSharpWithoutStream, "missing", "missing.ImageStream", new[] { "a" }, out _, out var missing));
		Assert.Contains("never creates", missing, System.StringComparison.Ordinal);
		Assert.False(ImageListSourceEditing.TryRewriteCSharp("class {", "imageList1", "imageList1.ImageStream", new[] { "a" }, out _, out var broken));
		Assert.Contains("syntax errors", broken, System.StringComparison.Ordinal);
		Assert.False(ImageListSourceEditing.TryRewriteCSharp(CSharpWithStream, "not valid", "k", new[] { "a" }, out _, out _));
	}

	[Fact]
	public void VisualBasic_RewritesKeysInPlaceAndAddsTheBindingWhenMissing()
	{
		Assert.True(ImageListSourceEditing.TryRewriteVisualBasic(VbWithStream, "imageList1", "imageList1.ImageStream", new[] { "x.png", "y.png" }, out var text, out var error), error);
		Assert.Equal(1, Count(text, "ImageStream ="));
		Assert.Contains("Me.imageList1.Images.SetKeyName(0, \"x.png\")", text, System.StringComparison.Ordinal);
		Assert.Contains("Me.imageList1.Images.SetKeyName(1, \"y.png\")", text, System.StringComparison.Ordinal);
		Assert.DoesNotContain("old0.png", text, System.StringComparison.Ordinal);

		Assert.True(ImageListSourceEditing.TryRewriteVisualBasic(VbWithoutStream, "imageList1", "imageList1.ImageStream", new[] { "q\"uote.png" }, out var added, out error), error);
		Assert.Contains("Dim resources As New System.ComponentModel.ComponentResourceManager(GetType(Form1))", added, System.StringComparison.Ordinal);
		Assert.Contains("Me.imageList1.ImageStream = CType(resources.GetObject(\"imageList1.ImageStream\"), System.Windows.Forms.ImageListStreamer)", added, System.StringComparison.Ordinal);
		Assert.Contains("SetKeyName(0, \"q\"\"uote.png\")", added, System.StringComparison.Ordinal);

		Assert.True(ImageListSourceEditing.TryRewriteVisualBasic(VbWithStream, "imageList1", "imageList1.ImageStream", System.Array.Empty<string>(), out var cleared, out error), error);
		Assert.DoesNotContain("ImageStream", cleared, System.StringComparison.Ordinal);
		Assert.False(ImageListSourceEditing.TryRewriteVisualBasic(VbWithStream, "imageList1", "other.ImageStream", new[] { "a" }, out _, out _));
	}

	[Fact]
	public void VisualBasic_AddsImageListSettingsAfterTheProvenCreation()
	{
		Assert.True(ImageListSourceEditing.TryRewriteVisualBasicSettings(VbWithoutStream, "imageList1", 20, 25, "Depth16Bit", out var text, out var error), error);
		var creation = text.IndexOf("Me.imageList1 = New", System.StringComparison.Ordinal);
		var size = text.IndexOf("Me.imageList1.ImageSize = New System.Drawing.Size(20, 25)", System.StringComparison.Ordinal);
		Assert.True(creation >= 0 && size > creation, text);
		Assert.Contains("Me.imageList1.ColorDepth = System.Windows.Forms.ColorDepth.Depth16Bit", text, System.StringComparison.Ordinal);
		Assert.False(ImageListSourceEditing.TryRewriteVisualBasicSettings(VbWithStream, "imageList1", 0, 16, null, out _, out _));
	}
}
