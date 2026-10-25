using System.Diagnostics;
using System.Linq;
using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// Integration coverage for the debugger features the shell now drives end to end: Set Next
/// Statement (goto/gotoTargets), restart, loaded sources and breakpoint locations. These are the
/// user-visible parts of the debug-adapter features exercised by the adapter's own conformance
/// tests; here they are driven through the real IDE via the od.debug.* actions.
/// </summary>
[Collection("50 Debugger fixture")]
public sealed class DebuggerFeatureIntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public DebuggerFeatureIntegrationTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task SetNextStatement_MovesTheInstructionPointer()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");
        var targetLine = FindLine(program, "var greeting = \"Hello, Debugger!\";");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

            var moved = await _app.InvokeAsync("od.debug.set-next-statement", program, targetLine);
            Assert.Equal(targetLine, moved.GetProperty("currentLine").GetInt32());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task Restart_KeepsTheSessionRunning()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var restart = await _app.InvokeAsync("od.debug.restart");
            Assert.True(restart.GetProperty("success").GetBoolean(), restart.ToString());
            Assert.True(restart.GetProperty("isDebugging").GetBoolean(), restart.ToString());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task LoadedSources_ListTheDebuggeeSources()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var sources = await _app.InvokeAsync("od.debug.loaded-sources");
            Assert.Contains(sources.EnumerateArray(), s => Normalize(s.GetString()).EndsWith("Program.cs"));
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task BreakpointLocations_ListsValidPositions()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var locations = await _app.InvokeAsync("od.debug.breakpoint-locations", program, breakpointLine);
            Assert.NotEmpty(locations.EnumerateArray());
            Assert.Contains(locations.EnumerateArray(), l => l.GetProperty("Line").GetInt32() == breakpointLine);
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task RunWithoutDebugging_StartsTheProjectWithoutABreakpoint()
    {
        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        var result = await _app.InvokeAsync("od.debug.run-without-debugging", _app.DebugTestProjectPath);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
    }

    [Fact]
    public async Task Logpoint_LogsItsMessageAndContinues()
    {
        var program = ProgramPath;
        var line = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        var added = await _app.InvokeAsync("od.debug.add-logpoint", program, line, "logpoint {answer}");
        Assert.True(added.GetProperty("success").GetBoolean(), added.ToString());

        try
        {
            // The logpoint continues rather than stopping, so start without waiting for a stop.
            await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, false, 45);
            var logged = await OpenDevelopAppFixture.PollUntilAsync(async () => {
                var output = await _app.InvokeAsync("od.debug.output");
                return (output.GetProperty("text").GetString() ?? string.Empty).Contains("logpoint 42", StringComparison.Ordinal);
            }, TimeSpan.FromSeconds(30));
            Assert.True(logged, "the logpoint should have written its interpolated message to the Debug output");
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    // doc/technotes/debugging.md, "Opening an external (decompiled) stack frame".
    [Fact]
    public async Task ExternalFrame_OpensDecompiledSource()
    {
        var program = ProgramPath;
        // Inside the System.Linq Select lambda: the frames below it are System.Linq code with no PDB.
        var line = FindLine(program, "var twice = n * 2;");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, line);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.Equal(line, start.GetProperty("currentLine").GetInt32());

            // The same path a double-click on the first sourceless call-stack frame takes.
            var opened = await _app.InvokeAsync("od.debug.open-frame", "external");
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            Assert.True(opened.GetProperty("resolved").GetBoolean(), opened.ToString());
            var document = opened.GetProperty("activeDocument").GetString()!;
            Assert.True(document.StartsWith("ilspy:", StringComparison.OrdinalIgnoreCase), opened.ToString());
            Assert.Contains("System.Linq", document, StringComparison.OrdinalIgnoreCase);
            var caretLine = opened.GetProperty("caretLine").GetInt32();
            Assert.InRange(caretLine, 1, opened.GetProperty("lineCount").GetInt32());
            // IL offset -> sequence point -> caret: ArraySelectIterator.Fill invokes the selector, and the
            // frame is paused inside that call. A missed token or offset match leaves the caret on line 1.
            var caretText = opened.GetProperty("caretLineText").GetString()!;
            Assert.Contains("func(", caretText, StringComparison.Ordinal);
            Assert.True(caretLine > 1, opened.ToString());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    // doc/technotes/debugging.md, "Eval window": dot-completion keeps working once an external frame is open.
    [Fact]
    public async Task ExternalFrame_ConsoleCompletionResolvesInTheNearestUserFrame()
    {
        var program = ProgramPath;
        var line = FindLine(program, "var twice = n * 2;");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, line);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            // A frame with real source is its own completion context, and `numbers.` (an int[] local of
            // Main, captured by the lambda) completes to array members.
            var own = await _app.InvokeAsync("od.debug.console-completions", "numbers.");
            Assert.True(own.GetProperty("hasContext").GetBoolean(), own.ToString());
            Assert.EndsWith("Program.cs", own.GetProperty("file").GetString());
            Assert.Equal(line, own.GetProperty("line").GetInt32());
            Assert.Contains("Length", own.GetProperty("labels").EnumerateArray().Select(l => l.GetString()), StringComparer.Ordinal);

            // Open the sourceless System.Linq frame (what a double-click does): the context becomes
            // the nearest user-code caller instead of nothing, and the same expression still completes.
            var opened = await _app.InvokeAsync("od.debug.open-frame", "external");
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            var inherited = await _app.InvokeAsync("od.debug.console-completions", "numbers.");
            Assert.True(inherited.GetProperty("hasContext").GetBoolean(), inherited.ToString());
            Assert.EndsWith("Program.cs", inherited.GetProperty("file").GetString());
            Assert.Contains("Main", inherited.GetProperty("frame").GetString());
            Assert.NotEqual(line, inherited.GetProperty("line").GetInt32());
            Assert.Contains("Length", inherited.GetProperty("labels").EnumerateArray().Select(l => l.GetString()), StringComparer.Ordinal);

            // The real path: typing '.' into the console shows the completion popup with the items. The
            // result labels above would pass even if the console editor showed nothing, so check it.
            var typed = await _app.InvokeAsync("od.debug.console-type-dot", "numbers.");
            Assert.True(typed.GetProperty("popupShown").GetBoolean(), typed.ToString());
            Assert.False(string.IsNullOrEmpty(typed.GetProperty("selected").GetString()), typed.ToString());

            // The source document the completion borrowed was put back: the editor text is unchanged.
            var fileText = File.ReadAllText(program);
            Assert.DoesNotContain("numbers.;", fileText);
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    // doc/technotes/debugging.md, "Eval window".
    [Fact]
    public async Task DebugConsole_HighlightsAndEvaluates()
    {
        var program = ProgramPath;
        var line = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, line);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var before = await _app.InvokeAsync("od.debug.console-state");
            Assert.True(before.GetProperty("hasHighlighting").GetBoolean(), before.ToString());
            Assert.Equal("C#", before.GetProperty("highlightingName").GetString());

            // Through the console's own AcceptCommand, i.e. the DAP "repl" evaluate path.
            var evaluated = await _app.InvokeAsync("od.debug.console-eval", "answer + 1");
            Assert.True(evaluated.GetProperty("success").GetBoolean(), evaluated.ToString());
            Assert.Contains("43", evaluated.GetProperty("text").GetString());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task Attach_ToRunningProcess_StopsAtBreakpoint()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_app.DebugTestProjectPath)!, "..", "AttachFixture"));
        var program = Path.Combine(dir, "Program.cs");
        var dll = Path.Combine(dir, "bin", "Debug", "net10.0", "AttachFixture.dll");
        Assert.True(File.Exists(dll), "AttachFixture was not built: " + dll);
        var line = FindLine(program, "var attachMarker = 7;");

        using var process = Process.Start(new ProcessStartInfo(OpenDevelopAppFixture.ResolveDotNetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { dll }
        })!;
        try
        {
            await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
            await _app.InvokeAsync("od.open-file", program);
            await _app.InvokeAsync("od.debug.clear-breakpoints");

            var attach = await _app.InvokeAsync("od.debug.attach", process.Id);
            Assert.True(attach.GetProperty("attached").GetBoolean(), attach.ToString());

            await _app.InvokeAsync("od.debug.set-breakpoint", program, line);
            var continued = await _app.InvokeAsync("od.debug.continue", true, 30);
            Assert.Equal(line, continued.GetProperty("currentLine").GetInt32());
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    [Fact]
    public async Task ShowExternalCode_Toggles()
    {
        var hidden = await _app.InvokeAsync("od.debug.show-external-code", false);
        Assert.False(hidden.GetProperty("showExternalCode").GetBoolean(), hidden.ToString());
        var shown = await _app.InvokeAsync("od.debug.show-external-code", true);
        Assert.True(shown.GetProperty("showExternalCode").GetBoolean(), shown.ToString());
        await _app.InvokeAsync("od.debug.show-external-code", false); // restore the default
    }

    [Fact]
    public async Task Breakpoints_EnableDisableAndDeleteAll()
    {
        var program = ProgramPath;
        var first = FindLine(program, "var greeting = \"Hello, Debugger!\";");
        var second = FindLine(program, "var answer = 42;");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, first);
        await _app.InvokeAsync("od.debug.set-breakpoint", program, second);

        var disabled = await _app.InvokeAsync("od.debug.set-all-breakpoints-enabled", false);
        Assert.Equal(2, disabled.GetProperty("count").GetInt32());
        var enabled = await _app.InvokeAsync("od.debug.set-all-breakpoints-enabled", true);
        Assert.Equal(2, enabled.GetProperty("count").GetInt32());

        var pad = await _app.InvokeAsync("od.debug.pad-snapshot", "BreakPointsPad");
        Assert.Equal(2, pad.GetProperty("items").GetArrayLength());

        var deleted = await _app.InvokeAsync("od.debug.delete-all-breakpoints");
        Assert.Equal(2, deleted.GetProperty("count").GetInt32());
        pad = await _app.InvokeAsync("od.debug.pad-snapshot", "BreakPointsPad");
        Assert.Equal(0, pad.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Modules_CarrySymbolStatus()
    {
        var program = ProgramPath;
        var line = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, line);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var modules = await _app.InvokeAsync("od.debug.modules");
            Assert.Contains(modules.EnumerateArray(), m =>
                (m.GetProperty("Name").GetString() ?? string.Empty) == "DebugTestApp.dll"
                && (m.GetProperty("SymbolStatus").GetString() ?? string.Empty) == "loaded");

            var pad = await _app.InvokeAsync("od.debug.pad-snapshot", "LoadedModulesPad");
            Assert.True(pad.GetProperty("found").GetBoolean());
            Assert.Contains(pad.GetProperty("items").EnumerateArray(), i =>
                (i.GetProperty("Name").GetString() ?? string.Empty) == "DebugTestApp.dll"
                && (i.GetProperty("Symbols").GetString() ?? string.Empty) == "loaded");
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task Evaluate_ExposesDapExpressionFeatures()
    {
        var program = ProgramPath;
        var line = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, line);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            Assert.Equal("42", (await _app.InvokeAsync("od.debug.evaluate", "answer")).GetProperty("Value").GetString());
            // Member access on a string value ("Hello, Debugger!" is 16 characters).
            Assert.Equal("16", (await _app.InvokeAsync("od.debug.evaluate", "greeting.Length")).GetProperty("Value").GetString());
            // The ,nq format specifier returns the string without quotes.
            Assert.Equal("Hello, Debugger!", (await _app.InvokeAsync("od.debug.evaluate", "greeting,nq")).GetProperty("Value").GetString());
            // The $pid pseudo-variable.
            Assert.True(int.TryParse((await _app.InvokeAsync("od.debug.evaluate", "$pid")).GetProperty("Value").GetString(), out var pid) && pid > 0);
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    string ProgramPath => Path.Combine(Path.GetDirectoryName(_app.DebugTestProjectPath)!, "Program.cs");
    static int FindLine(string path, string marker)
    {
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal))
                return i + 1;
        }
        throw new InvalidOperationException($"Marker '{marker}' not found in {path}");
    }

    static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/');
}
