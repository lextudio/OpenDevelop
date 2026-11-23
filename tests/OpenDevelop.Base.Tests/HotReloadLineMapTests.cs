using System.Text.Json.Nodes;

using Debugger.AddIn.Service.Dap;

using ICSharpCode.SharpDevelop.LanguageServices.Protocol;

using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// The unchanged-line map a delta carries for active-frame remap under SharpDbg: computed from the two texts
/// (<see cref="HotReloadSourceLineMap.Compute"/>) and sent as the <c>lineMaps</c> argument of <c>sharpdbg/applyDeltas</c>.
/// </summary>
public class HotReloadLineMapTests
{
	static (int Old, int New, int Count)[] Ranges(string oldText, string newText)
		=> HotReloadSourceLineMap.Compute(oldText, newText).Select(r => (r.OldStartLine, r.NewStartLine, r.LineCount)).ToArray();

	[Fact]
	public void IdenticalTexts_AreOneRange()
		=> Assert.Equal(new[] { (1, 1, 3) }, Ranges("a\nb\nc", "a\nb\nc"));

	[Fact]
	public void InsertedLines_ShiftWhatFollows()
		=> Assert.Equal(new[] { (1, 1, 1), (2, 4, 2) }, Ranges("a\nb\nc", "a\nx\ny\nb\nc"));

	[Fact]
	public void AnEditedLine_IsLeftOut()
		=> Assert.Equal(new[] { (1, 1, 1), (3, 3, 1) }, Ranges("a\nb\nc", "a\nB\nc"));

	[Fact]
	public void UnchangedLines_BetweenTwoEdits_AreKept()
	{
		// The shape of an edit around an active statement: lines inserted above, a line changed below, and the
		// statement in between untouched. A common-prefix/suffix comparison alone would lose the middle.
		const string before = "m(){\nvar a = 1;\nBreak();\nvar mid = a;\nvar b = 1;\nreturn;\n}";
		const string after = "m(){\n\n\nvar a = 2;\nBreak();\nvar mid = a;\nvar b = 2;\nreturn;\n}";
		Assert.Equal(new[] { (1, 1, 1), (3, 5, 2), (6, 8, 2) }, Ranges(before, after));
	}

	[Fact]
	public void LineBreakStyle_DoesNotChangeLines()
		=> Assert.Equal(new[] { (1, 1, 3) }, Ranges("a\r\nb\r\nc", "a\nb\nc"));

	[Fact]
	public void DapArgument_HasTheShapeSharpDbgReads()
	{
		Assert.Null(DapSession.LineMapsJson(null));
		Assert.Null(DapSession.LineMapsJson([]));
		var json = DapSession.LineMapsJson([new HotReloadSourceLineMap("/src/A.cs", [new HotReloadUnchangedLines(3, 5, 2)])])!;
		var map = Assert.Single(json)!;
		Assert.Equal("/src/A.cs", map["documentPath"]!.GetValue<string>());
		var range = Assert.Single((JsonArray)map["unchangedLines"]!)!;
		Assert.Equal(3, range["oldStartLine"]!.GetValue<int>());
		Assert.Equal(5, range["newStartLine"]!.GetValue<int>());
		Assert.Equal(2, range["lineCount"]!.GetValue<int>());
	}
}
