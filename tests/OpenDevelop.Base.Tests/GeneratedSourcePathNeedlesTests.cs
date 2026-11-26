using ICSharpCode.SharpDevelop.LanguageServices.Roslyn;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// od.xsg.generated-sources and View Generated Code find a XAML file's generated C# by the quoted path in its <c>#line</c> directives.
/// The LibreWPF generator writes that path as a C# string literal, so on Windows the backslashes are doubled (CI run 45892153613 found
/// nothing there); a generator that writes the path as it is must still match.
/// </summary>
public class GeneratedSourcePathNeedlesTests
{
	[Fact]
	public void WindowsPath_MatchesBothTheEscapedAndTheRawForm()
	{
		var needles = CSharpVBLanguageService.GeneratedSourcePathNeedles(@"D:\a\b\MainWindow.xaml");
		Assert.Equal(new[] { "\"D:\\a\\b\\MainWindow.xaml\"", "\"D:\\\\a\\\\b\\\\MainWindow.xaml\"" }, needles);
		var generated = "#line 12 \"D:\\\\a\\\\b\\\\MainWindow.xaml\"\n";
		Assert.Contains(needles, needle => generated.Contains(needle));
	}

	[Fact]
	public void UnixPath_HasOneNeedle()
	{
		Assert.Equal(new[] { "\"/tmp/x/MainWindow.xaml\"" }, CSharpVBLanguageService.GeneratedSourcePathNeedles("/tmp/x/MainWindow.xaml"));
	}

	[Fact]
	public void QuoteInPath_IsEscaped()
	{
		var needles = CSharpVBLanguageService.GeneratedSourcePathNeedles("/tmp/a\"b.xaml");
		Assert.Contains("\"/tmp/a\\\"b.xaml\"", needles);
	}
}
