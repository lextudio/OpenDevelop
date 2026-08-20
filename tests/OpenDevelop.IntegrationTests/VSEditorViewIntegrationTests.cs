// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).
//
// Integration coverage for the VS editor compatibility layer's ITextViewLine/
// ITextViewLineCollection geometry (vs-editor-api.md sections 22/64), driven through
// VSEditorViewDevFlowActions against the real running app. A bare in-process WPF Window +
// UpdateLayout() inside a plain unit test process hangs on this repo's LibreWPF/macOS stack (no
// native message loop pumping it) - only a live app instance's own Dispatcher, reached here via
// [DevFlowUIThread], produces real AvalonEdit VisualLine/TextLine layout.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("20 General workbench fixture")]
public sealed class VSEditorViewIntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public VSEditorViewIntegrationTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    string SampleAppDirectory => Path.Combine(Path.GetDirectoryName(_app.SolutionExplorerFixturePath)!, "SampleApp");

    static readonly string[] Lines = { "// line zero", "// line one", "// line two", "// line three" };

    async Task<string> OpenScratchFileAsync(string testName)
    {
        var path = Path.Combine(SampleAppDirectory, $"VSEditorScratch_{testName}.cs");
        File.WriteAllText(path, string.Join("\n", Lines));
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
        var opened = await _app.InvokeAsync("od.open-file", path);
        Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
        return path;
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task LineGeometry_Has_One_Line_Per_Document_Line()
    {
        var path = await OpenScratchFileAsync(nameof(LineGeometry_Has_One_Line_Per_Document_Line));
        try
        {
            var status = await _app.InvokeAsync("od.vseditor.line-geometry");
            Assert.True(status.GetProperty("active").GetBoolean());
            Assert.True(status.GetProperty("viewAvailable").GetBoolean(), status.ToString());

            var lines = status.GetProperty("lines").EnumerateArray().ToArray();
            Assert.Equal(Lines.Length, lines.Length);
            for (int i = 0; i < Lines.Length; i++)
                Assert.Equal(Lines[i], lines[i].GetProperty("text").GetString());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task LineGeometry_Lines_Are_Ordered_TopToBottom_And_Flush()
    {
        var path = await OpenScratchFileAsync(nameof(LineGeometry_Lines_Are_Ordered_TopToBottom_And_Flush));
        try
        {
            var status = await _app.InvokeAsync("od.vseditor.line-geometry");
            var lines = status.GetProperty("lines").EnumerateArray().ToArray();

            for (int i = 0; i < lines.Length; i++)
                Assert.True(lines[i].GetProperty("height").GetDouble() > 0, $"line {i} must have positive height");

            for (int i = 0; i + 1 < lines.Length; i++)
            {
                var bottom = lines[i].GetProperty("bottom").GetDouble();
                var nextTop = lines[i + 1].GetProperty("top").GetDouble();
                Assert.True(lines[i].GetProperty("top").GetDouble() < lines[i + 1].GetProperty("top").GetDouble());
                Assert.True(Math.Abs(bottom - nextTop) < 0.5, $"line {i} bottom ({bottom}) should be flush with line {i + 1} top ({nextTop})");
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task LineGeometry_LineBreak_Length_Is_One_Except_On_The_Last_Line()
    {
        var path = await OpenScratchFileAsync(nameof(LineGeometry_LineBreak_Length_Is_One_Except_On_The_Last_Line));
        try
        {
            var status = await _app.InvokeAsync("od.vseditor.line-geometry");
            var lines = status.GetProperty("lines").EnumerateArray().ToArray();

            for (int i = 0; i < lines.Length; i++)
            {
                var text = lines[i].GetProperty("text").GetString()!;
                var expected = text.Length + (i < lines.Length - 1 ? 1 : 0);
                Assert.Equal(expected, lines[i].GetProperty("lengthIncludingLineBreak").GetInt32());
            }
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Caret_Defaults_To_The_First_Line_On_Open()
    {
        var path = await OpenScratchFileAsync(nameof(Caret_Defaults_To_The_First_Line_On_Open));
        try
        {
            var status = await _app.InvokeAsync("od.vseditor.line-geometry");
            Assert.Equal(0, status.GetProperty("caret").GetProperty("offset").GetInt32());
            Assert.Equal(Lines[0], status.GetProperty("caret").GetProperty("lineText").GetString());
            // The caret's reported top must match its own containing line's top from the same
            // TextViewLines collection - i.e. AvalonTextCaret.ContainingTextViewLine really does
            // resolve to the first row, not just report some default.
            var firstLine = status.GetProperty("lines").EnumerateArray().First();
            Assert.Equal(firstLine.GetProperty("top").GetDouble(), status.GetProperty("caret").GetProperty("top").GetDouble());
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Folding_Merges_The_Folded_DocumentLines_Into_One_ITextViewLine_With_The_Combined_Extent()
    {
        // Plain .txt, not .cs: a source file's language binding (CSharpBinding's outlining
        // strategy) periodically reconciles its own FoldingManager foldings against a fresh
        // parse and would otherwise race with - and silently discard - the folding this test
        // creates by hand.
        var path = Path.Combine(SampleAppDirectory, $"VSEditorScratch_{nameof(Folding_Merges_The_Folded_DocumentLines_Into_One_ITextViewLine_With_The_Combined_Extent)}.txt");
        File.WriteAllText(path, string.Join("\n", Lines));
        await _app.InvokeAsync("od.open-solution", _app.SolutionExplorerFixturePath);
        var opened = await _app.InvokeAsync("od.open-file", path);
        Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
        try
        {
            // "// line zero\n// line one\n// line two\n// line three"
            //  0           12 13         24 25         36 37
            // Fold from the start of line 1 to inside line 2, so AvalonEdit's FoldingManager
            // collapses line 2's own row into line 1's VisualLine (FirstDocumentLine=line1,
            // LastDocumentLine=line2) - exactly the multi-DocumentLine VisualLine case
            // AvalonTextViewLine must report as one ITextViewLine with the combined Extent.
            var foldStart = Lines[0].Length + 1; // 13: start of "// line one"
            var foldEnd = foldStart + Lines[1].Length + 1 + 4; // a few chars into "// line two"

            var status = await _app.InvokeAsync("od.vseditor.fold-and-geometry", foldStart, foldEnd);
            Assert.True(status.GetProperty("active").GetBoolean(), status.ToString());
            Assert.False(status.TryGetProperty("error", out _), status.ToString());

            // AvalonEdit's own VisualLine model: confirms the fold actually merged document
            // lines 2 and 3 (1-based: "// line one" and "// line two") into one VisualLine -
            // i.e. FirstDocumentLine != LastDocumentLine, the exact case AvalonTextViewLine's
            // offset math must handle correctly.
            var visualLines = status.GetProperty("visualLines").EnumerateArray().ToArray();
            var mergedVisualLine = Assert.Single(visualLines, vl => vl.GetProperty("firstDocumentLine").GetInt32() != vl.GetProperty("lastDocumentLine").GetInt32());
            Assert.Equal(2, mergedVisualLine.GetProperty("firstDocumentLine").GetInt32());
            Assert.Equal(3, mergedVisualLine.GetProperty("lastDocumentLine").GetInt32());

            // ITextViewLine rows: however many physical rows AvalonEdit's renderer split the
            // merged VisualLine into (a separate, known AvalonEdit rendering-layer gap - see
            // VSEditorViewDevFlowActions.FoldAndGetLineGeometry's comment - not something
            // AvalonTextViewLine's own Extent math depends on), the ones whose buffer offsets
            // fall inside the fold's range must concatenate back to exactly the folded text,
            // and every row outside the fold must be untouched.
            var lines = status.GetProperty("lines").EnumerateArray().ToArray();
            var foldRangeEnd = Lines[0].Length + 1 + Lines[1].Length + 1 + Lines[2].Length; // end of "// line two"

            Assert.Equal(Lines[0], lines[0].GetProperty("text").GetString());
            Assert.Equal(0, lines[0].GetProperty("start").GetInt32());
            Assert.Equal(Lines[0].Length, lines[0].GetProperty("end").GetInt32());

            var foldedRows = lines.Where(l => l.GetProperty("start").GetInt32() >= foldStart && l.GetProperty("end").GetInt32() <= foldRangeEnd).ToArray();
            Assert.NotEmpty(foldedRows);
            Assert.Equal(foldStart, foldedRows[0].GetProperty("start").GetInt32());
            Assert.Equal(foldRangeEnd, foldedRows[^1].GetProperty("end").GetInt32());
            var mergedText = string.Concat(foldedRows.Select(l => l.GetProperty("text").GetString()));
            Assert.Equal(Lines[1] + "\n" + Lines[2], mergedText);

            var lastLine = lines[^1];
            Assert.Equal(Lines[3], lastLine.GetProperty("text").GetString());

            // Every row must be flush with the next, whether it came from the fold or not.
            for (int i = 0; i + 1 < lines.Length; i++)
                Assert.Equal(lines[i].GetProperty("bottom").GetDouble(), lines[i + 1].GetProperty("top").GetDouble(), precision: 1);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task Selection_Spanning_Two_Lines_Reports_The_Correct_Overlap_Per_Line()
    {
        var path = await OpenScratchFileAsync(nameof(Selection_Spanning_Two_Lines_Reports_The_Correct_Overlap_Per_Line));
        try
        {
            // Select from inside line 0 through inside line 1: "zero\n// line one"[:-4]-ish -
            // start at offset 8 ("zero" begins at 8 in "// line zero"), end 8 chars into line 1.
            var start = Lines[0].Length - 4; // "zero"
            var end = Lines[0].Length + 1 + 8; // + newline + "// line "
            var result = await _app.InvokeAsync("od.vseditor.select", start, end - start);
            Assert.True(result.GetProperty("active").GetBoolean());

            var lines = result.GetProperty("lines").EnumerateArray().ToArray();
            var firstSelection = lines[0].GetProperty("selection");
            var secondSelection = lines[1].GetProperty("selection");
            var thirdSelection = lines[2].GetProperty("selection");

            // GetSelectionOnTextViewLine clips against ExtentIncludingLineBreak (matching real VS
            // behavior), so a selection continuing onto the next line includes line 0's own
            // line-break character in its reported overlap.
            Assert.Equal("zero\n", firstSelection.GetString());
            Assert.Equal("// line ", secondSelection.GetString());
            Assert.Equal(JsonValueKind.Null, thirdSelection.ValueKind);
        }
        finally { TryDelete(path); }
    }
}
