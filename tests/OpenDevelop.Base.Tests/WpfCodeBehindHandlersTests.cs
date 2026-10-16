using System;

using ICSharpCode.WpfDesign.AddIn;

using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class WpfCodeBehindHandlersTests
{
	const string CodeBehind = """
		namespace sample
		{
		    public partial class Helper { }

		    public partial class MainWindow : System.Windows.Window
		    {
		        public MainWindow()   {   InitializeComponent();   }
		    }
		}
		""";

	[Fact]
	public void ReadsTheSimpleClassNameFromXClass()
	{
		Assert.Equal("MainWindow", WpfCodeBehindHandlers.ClassNameOf("<Window x:Class=\"sample.MainWindow\" />"));
		Assert.Null(WpfCodeBehindHandlers.ClassNameOf("<Window />"));
	}

	[Fact]
	public void AddsTheHandlerToTheXClassClassAndReportsItsLine()
	{
		var updated = WpfCodeBehindHandlers.AddHandler(CodeBehind, "MainWindow", "owner_Click", "global::System.Windows.RoutedEventArgs", out var line);
		Assert.NotNull(updated);
		Assert.Contains("private void owner_Click(object sender, global::System.Windows.RoutedEventArgs e)", updated, StringComparison.Ordinal);
		var lines = updated!.Replace("\r\n", "\n").Split('\n');
		Assert.Contains("owner_Click", lines[line - 1], StringComparison.Ordinal);
		// The handler lands in MainWindow, not in the first class of the file.
		Assert.True(updated.IndexOf("owner_Click", StringComparison.Ordinal) > updated.IndexOf("class MainWindow", StringComparison.Ordinal));
		// Only the inserted member is formatted: the user's own odd spacing is untouched.
		Assert.Contains("public MainWindow()   {   InitializeComponent();   }", updated, StringComparison.Ordinal);
	}

	[Fact]
	public void AnExistingHandlerIsFoundNotDuplicated()
	{
		var once = WpfCodeBehindHandlers.AddHandler(CodeBehind, "MainWindow", "owner_Click", "global::System.EventArgs", out _)!;
		var twice = WpfCodeBehindHandlers.AddHandler(once, "MainWindow", "owner_Click", "global::System.EventArgs", out var line);
		Assert.Null(twice);
		Assert.Contains("owner_Click", once.Replace("\r\n", "\n").Split('\n')[line - 1], StringComparison.Ordinal);
	}

	[Fact]
	public void AnUnknownClassFallsBackToTheFirstClassAndNoClassDoesNothing()
	{
		var updated = WpfCodeBehindHandlers.AddHandler(CodeBehind, "Missing", "h", "global::System.EventArgs", out var line);
		Assert.True(updated!.IndexOf("void h(", StringComparison.Ordinal) < updated.IndexOf("class MainWindow", StringComparison.Ordinal));
		Assert.True(line > 0);
		Assert.Null(WpfCodeBehindHandlers.AddHandler("namespace empty { }", "MainWindow", "h", "global::System.EventArgs", out var none));
		Assert.Equal(0, none);
	}

	[Fact]
	public void AddsAndFindsAVisualBasicHandler()
	{
		const string source = "Namespace sample\n    Public Partial Class MainWindow\n    End Class\nEnd Namespace\n";
		var updated = WpfCodeBehindHandlers.AddVisualBasicHandler(source, "MainWindow", "owner_Click", "global::System.EventArgs", out var line);
		Assert.NotNull(updated);
		Assert.Contains("Private Sub owner_Click(sender As Object, e As Global.System.EventArgs)", updated, StringComparison.Ordinal);
		Assert.Contains("owner_Click", updated!.Replace("\r\n", "\n").Split('\n')[line - 1], StringComparison.Ordinal);
		Assert.Null(WpfCodeBehindHandlers.AddVisualBasicHandler(updated, "MainWindow", "owner_Click", "global::System.EventArgs", out var existingLine));
		Assert.True(existingLine > 0);
	}

	[Fact]
	public void AddsAVisualBasicHandlerWithGenericEventArgs()
	{
		const string source = "Public Partial Class MainWindow\nEnd Class\n";
		var updated = WpfCodeBehindHandlers.AddVisualBasicHandler(source, "MainWindow", "owner_Changed", "global::System.Windows.RoutedPropertyChangedEventArgs<global::System.Double>", out _);
		Assert.Contains("e As Global.System.Windows.RoutedPropertyChangedEventArgs(Of Global.System.Double)", updated, StringComparison.Ordinal);
	}
}
