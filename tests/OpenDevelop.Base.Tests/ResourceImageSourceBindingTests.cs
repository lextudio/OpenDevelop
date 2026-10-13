using ICSharpCode.FormsDesigner.Services;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class ResourceImageSourceBindingTests
{
	[Fact]
	public void CSharpBinding_AddsConventionalResourceDeclarationAndAssignmentBeforeResumeLayout()
	{
		const string source = "partial class Form1 { void InitializeComponent() { this.button1 = new Button(); this.ResumeLayout(false); } Button button1; }";

		var added = ResourceImageSourceBinding.TryAddCSharpBinding(source, "button1", "Image", "System.Drawing.Image", "button1.Image", out var updated, out var error);

		Assert.True(added, error);
		Assert.Contains("var resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));", updated);
		Assert.Contains("this.button1.Image = ((System.Drawing.Image)(resources.GetObject(\"button1.Image\")));", updated);
		Assert.True(updated.IndexOf("this.button1.Image", StringComparison.Ordinal) < updated.IndexOf("this.ResumeLayout", StringComparison.Ordinal));
	}

	[Fact]
	public void CSharpBinding_RejectsExistingAssignment()
	{
		const string source = "partial class Form1 { void InitializeComponent() { this.button1.Image = null; } Button button1; }";

		Assert.False(ResourceImageSourceBinding.TryAddCSharpBinding(source, "button1", "Image", "System.Drawing.Image", "button1.Image", out var updated, out var error));
		Assert.Equal(source, updated);
		Assert.Contains("already", error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CSharpBinding_BindsTheRootIconAndRejectsUnexpectedPropertyTypes()
	{
		const string source = "partial class Form1 { void InitializeComponent() { } }";

		Assert.True(ResourceImageSourceBinding.TryAddCSharpBinding(source, "$this", "Icon", "System.Drawing.Icon", "$this.Icon", out var updated, out var error), error);
		Assert.Contains("this.Icon = ((System.Drawing.Icon)(resources.GetObject(\"$this.Icon\")));", updated);
		Assert.False(ResourceImageSourceBinding.TryAddCSharpBinding(source, "button1", "Image", "System.Drawing.Image); Inject()", "button1.Image", out _, out _));
	}

	[Fact]
	public void VisualBasicBinding_AddsConventionalResourceDeclarationAndAssignment()
	{
		const string source = "Partial Class Form1\nPrivate Sub InitializeComponent()\nMe.button1 = New Button()\nMe.ResumeLayout(False)\nEnd Sub\nPrivate button1 As Button\nEnd Class";

		var added = ResourceImageSourceBinding.TryAddVisualBasicBinding(source, "button1", "Image", "System.Drawing.Image", "button1.Image", out var updated, out var error);

		Assert.True(added, error);
		Assert.Contains("Dim resources As New System.ComponentModel.ComponentResourceManager(GetType(Form1))", updated);
		Assert.Contains("Me.button1.Image = CType(resources.GetObject(\"button1.Image\"), System.Drawing.Image)", updated);
		Assert.True(updated.IndexOf("Me.button1.Image", StringComparison.Ordinal) < updated.IndexOf("Me.ResumeLayout", StringComparison.Ordinal));
	}
}
