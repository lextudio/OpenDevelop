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
