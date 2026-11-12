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

            // Restart relaunches the debuggee and attaches again asynchronously, so the action returns while the
            // old session may still be the one debugging (slow Windows ARM64 runner). Wait for the stop of the
            // relaunched debuggee at the same breakpoint: a stop that happens after the restart began.
            // Twice: the second restart starts from what the first one started.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var restart = await _app.InvokeAsync("od.debug.restart", 45);
                Assert.True(restart.GetProperty("success").GetBoolean(), $"restart {attempt}: {restart}");
                Assert.True(restart.GetProperty("stopped").GetBoolean(), $"restart {attempt}: {restart}");
                Assert.True(restart.GetProperty("isDebugging").GetBoolean(), $"restart {attempt}: {restart}");
                var location = await _app.InvokeAsync("od.debug.location");
                Assert.Equal(breakpointLine, location.GetProperty("currentLine").GetInt32());
            }
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
    public async Task RunWithoutDebugging_StartsTheProjectWithoutADebugger()
    {
        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);

        // Run Without Debugging starts the program and forgets it, as SharpDevelop did. It used to
        // run through a DAP noDebug session, which put the workbench into the debugging state
        // until the program exited (closing a GTK app left the toolbar looking like a live debug
        // session), and before that attached a full debugger.
        var result = await _app.InvokeAsync("od.debug.run-without-debugging", _app.DebugTestProjectPath, 45);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.False(result.GetProperty("isDebugging").GetBoolean(), "Run Without Debugging must not start a debug session: " + result);
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());

        var info = await _app.InvokeAsync("od.debug.service-info");
        Assert.False(info.GetProperty("isDebugging").GetBoolean(), info.ToString());
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
            // An adapter that decompiles by itself (the pinned SharpDbg before decompileExternalCode) is shown through the ILSpy
            // document; an adapter that takes the IDE's symbols (doc/technotes/decompiled-debugging.md) opens the .cs file ILSpy
            // wrote for the module. Either way it is the System.Linq code, read-only, with the caret on the paused statement.
            var viaIlspyDocument = document.StartsWith("ilspy:", StringComparison.OrdinalIgnoreCase);
            var viaSymbolFile = document.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && document.Contains("DecompiledSymbols", StringComparison.OrdinalIgnoreCase);
            Assert.True(viaIlspyDocument || viaSymbolFile, opened.ToString());
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
            // The labels come from the language service, which may still be loading the project when the
            // breakpoint is hit on a slow machine; until it has, it answers from the namespace scope
            // instead, so wait for the member itself rather than for any label at all.
            JsonElement own = default;
            var ownCompleted = await OpenDevelopAppFixture.PollUntilAsync(async () => {
                own = await _app.InvokeAsync("od.debug.console-completions", "numbers.");
                return own.GetProperty("labels").EnumerateArray().Any(l => l.GetString() == "Length");
            }, TimeSpan.FromSeconds(60));
            Assert.True(ownCompleted, own.ToString());
            Assert.True(own.GetProperty("hasContext").GetBoolean(), own.ToString());
            Assert.EndsWith("Program.cs", own.GetProperty("file").GetString());
            Assert.Equal(line, own.GetProperty("line").GetInt32());

            // Open the sourceless System.Linq frame (what a double-click does): the context becomes
            // the nearest user-code caller instead of nothing, and the same expression still completes.
            var opened = await _app.InvokeAsync("od.debug.open-frame", "external");
            Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());
            JsonElement inherited = default;
            var inheritedCompleted = await OpenDevelopAppFixture.PollUntilAsync(async () => {
                inherited = await _app.InvokeAsync("od.debug.console-completions", "numbers.");
                return inherited.GetProperty("labels").EnumerateArray().Any(l => l.GetString() == "Length");
            }, TimeSpan.FromSeconds(60));
            Assert.True(inheritedCompleted, inherited.ToString());
            Assert.True(inherited.GetProperty("hasContext").GetBoolean(), inherited.ToString());
            Assert.EndsWith("Program.cs", inherited.GetProperty("file").GetString());
            Assert.Contains("Main", inherited.GetProperty("frame").GetString());
            Assert.NotEqual(line, inherited.GetProperty("line").GetInt32());

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

    /// <summary>
    /// A real click on an empty line of the Icon Bar adds a breakpoint, hovering it shows its popup,
    /// moving away closes it, and a second click removes it - SharpDevelop's gutter behaviour.
    /// On LibreWPF the empty gutter was not hittable (the margin draws nothing there and relies on a
    /// HitTestCore override), so the click reached the Grid behind it and nothing happened; the
    /// breakpoint popup then threw "No portable popup host accepted this owner" on hover, and moving
    /// away threw from PointFromScreen on the never-shown popup. Only real pointer input reaches
    /// these paths: od.debug.set-breakpoint does not.
    /// </summary>
    [Fact]
    public async Task IconBarClick_AddsAndRemovesABreakpointOnAnEmptyLine()
    {
        var program = ProgramPath;
        // A plain statement: no declaration (outline) bookmark shares the line.
        var line = FindLine(program, "var greeting = \"Hello, Debugger!\";");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        try
        {
            async Task<bool> HasBreakpointAsync()
            {
                var snapshot = await _app.InvokeAsync("od.editor.icon-bar-bookmarks");
                return snapshot.GetProperty("bookmarks").EnumerateArray().Any(bookmark =>
                    bookmark.GetProperty("type").GetString() == "BreakpointBookmark"
                    && bookmark.GetProperty("line").GetInt32() == line);
            }

            async Task<(double x, double y)> GutterPointAsync()
            {
                var point = await _app.InvokeAsync("od.editor.icon-bar-line-point", line);
                Assert.True(point.GetProperty("success").GetBoolean(), point.ToString());
                return (point.GetProperty("x").GetDouble(), point.GetProperty("y").GetDouble());
            }

            Assert.False(await HasBreakpointAsync());

            var (x, y) = await GutterPointAsync();
            var clicked = await _app.ClickPointerAsync(x, y);
            Assert.True(clicked.GetProperty("ok").GetBoolean(), clicked.ToString());
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(HasBreakpointAsync, TimeSpan.FromSeconds(10)),
                $"A click on the empty Icon Bar at line {line} ({x},{y}) did not add a breakpoint.");

            // Stay on the breakpoint long enough for its popup, then leave the gutter: both used to throw.
            await Task.Delay(TimeSpan.FromSeconds(2));
            await _app.MovePointerAsync(x + 400, y + 200);
            await Task.Delay(TimeSpan.FromSeconds(1));

            (x, y) = await GutterPointAsync();
            clicked = await _app.ClickPointerAsync(x, y);
            Assert.True(clicked.GetProperty("ok").GetBoolean(), clicked.ToString());
            Assert.True(await OpenDevelopAppFixture.PollUntilAsync(async () => !await HasBreakpointAsync(), TimeSpan.FromSeconds(10)),
                $"A second click on the Icon Bar at line {line} did not remove the breakpoint.");
        }
        finally
        {
            await _app.MovePointerAsync(0, 0);
            await _app.InvokeAsync("od.debug.clear-breakpoints");
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
