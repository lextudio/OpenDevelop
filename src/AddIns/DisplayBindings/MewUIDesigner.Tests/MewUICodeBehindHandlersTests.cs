using ICSharpCode.MewUIDesigner;
using Xunit;

public sealed class MewUICodeBehindHandlersTests
{
	const string CodeBehind = """
using Aprillz.MewUI;

namespace App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void NewButton_Click()
    {
    }
}
""";

	[Fact]
	public void SignaturesFollowTheMewUIDelegatesAndUnknownPairsHaveNone()
	{
		Assert.Equal("", MewUICodeBehindHandlers.ParameterList("Button", "Click"));
		Assert.Equal("", MewUICodeBehindHandlers.ParameterList("Window", "Loaded"));
		Assert.Equal("bool? isChecked", MewUICodeBehindHandlers.ParameterList("CheckBox", "CheckedChanged"));
		Assert.Equal("bool isChecked", MewUICodeBehindHandlers.ParameterList("RadioButton", "CheckedChanged"));
		Assert.Equal("object? selectedItem", MewUICodeBehindHandlers.ParameterList("ListBox", "SelectionChanged"));
		// A Label has no Loaded event and ItemActivated's delegate is not known: no guessed method.
		Assert.Null(MewUICodeBehindHandlers.ParameterList("Label", "Loaded"));
		Assert.Null(MewUICodeBehindHandlers.ParameterList("ListBox", "ItemActivated"));
	}

	[Fact]
	public void ClassNameComesFromTheRootClassAttribute()
	{
		Assert.Equal("MainWindow", MewUICodeBehindHandlers.ClassNameOf("<Window xmlns=\"x\" Name=\"w\" Class=\"App.Windows.MainWindow\">"));
		Assert.Null(MewUICodeBehindHandlers.ClassNameOf("<Window Name=\"w\">"));
	}

	[Fact]
	public void AddsAMissingHandlerOnceAndFindsAnExistingOne()
	{
		var added = MewUICodeBehindHandlers.AddHandler(CodeBehind, "MainWindow", "check_CheckedChanged", "bool? isChecked", out var line);
		Assert.NotNull(added);
		Assert.Contains("private void check_CheckedChanged(bool? isChecked)", added);
		Assert.True(line > 0);
		Assert.Contains("    private void NewButton_Click()\n    {\n    }", added!.Replace("\r\n", "\n"));
		Assert.Null(MewUICodeBehindHandlers.AddHandler(added, "MainWindow", "check_CheckedChanged", "bool? isChecked", out var again));
		Assert.Equal(line, again);
		Assert.Null(MewUICodeBehindHandlers.AddHandler(CodeBehind, "MainWindow", "NewButton_Click", "", out var existing));
		Assert.True(existing > 0);
	}
}
