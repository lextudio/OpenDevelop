using System.Text.Json;

using Xunit;

namespace OpenDevelop.IntegrationTests;

[Collection("OpenDevelop app")]
public sealed class DebuggerIntegrationTests
{
    readonly OpenDevelopAppFixture _app;

    public DebuggerIntegrationTests(OpenDevelopAppFixture app)
    {
        _app = app;
    }

    [Fact]
    public async Task DebuggerService_IsRegisteredByAddIn()
    {
        var info = await _app.InvokeAsync("od.debug.service-info");

        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.Equal("OpenDevelop.Debugger.DapDebuggerService", info.GetProperty("typeName").GetString());
        Assert.False(info.GetProperty("isDebugging").GetBoolean());

        var pads = await _app.InvokeAsync("od.pads");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.BreakPointsPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.CallStackPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.LocalVarPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.ThreadsPad");
        Assert.Contains(pads.EnumerateArray(), p => p.GetProperty("className").GetString() == "ICSharpCode.SharpDevelop.Gui.Pads.LoadedModulesPad");
    }

    [Fact]
    public async Task BreakpointHit_ExposesCallStackLocalsAndEvaluate()
    {
        var program = ProgramPath;
        var breakpointLine = FindLine(program, "var message = ComputeGreeting(\"World\");");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        var breakpoint = await _app.InvokeAsync("od.debug.set-breakpoint", program, breakpointLine);
        Assert.True(breakpoint.GetProperty("success").GetBoolean());
        Assert.Contains(breakpoint.GetProperty("lines").EnumerateArray(), l => l.GetInt32() == breakpointLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());
            Assert.EndsWith("Program.cs", Normalize(start.GetProperty("currentFile").GetString()));
            Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

            var stack = await _app.InvokeAsync("od.debug.call-stack");
            Assert.Contains(stack.EnumerateArray(), f => f.GetProperty("Name").GetString()!.Contains("Main"));

            var locals = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(locals.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "greeting"
                && v.GetProperty("Value").GetString()!.Contains("Hello, Debugger!"));
            Assert.Contains(locals.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "answer"
                && v.GetProperty("Value").GetString()!.Contains("42"));

            var evaluated = await _app.InvokeAsync("od.debug.evaluate", "answer");
            Assert.Equal("answer", evaluated.GetProperty("Name").GetString());
            Assert.Contains("42", evaluated.GetProperty("Value").GetString());

            var breakpointPad = await _app.InvokeAsync("od.debug.pad-snapshot", "BreakPointsPad");
            Assert.True(breakpointPad.GetProperty("found").GetBoolean());
            Assert.Contains(breakpointPad.GetProperty("items").EnumerateArray(), i =>
                Normalize(i.GetProperty("File").GetString()).EndsWith("Program.cs")
                && i.GetProperty("Line").GetInt32() == breakpointLine);

            var callStackPad = await _app.InvokeAsync("od.debug.pad-snapshot", "CallStackPad");
            Assert.True(callStackPad.GetProperty("found").GetBoolean());
            Assert.Contains(callStackPad.GetProperty("items").EnumerateArray(), f =>
                f.GetProperty("Name").GetString()!.Contains("Main"));

            var localsPad = await _app.InvokeAsync("od.debug.pad-snapshot", "LocalVarPad");
            Assert.True(localsPad.GetProperty("found").GetBoolean());
            Assert.Contains(localsPad.GetProperty("items").EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "answer"
                && v.GetProperty("Value").GetString()!.Contains("42"));

            var threadsPad = await _app.InvokeAsync("od.debug.pad-snapshot", "ThreadsPad");
            Assert.True(threadsPad.GetProperty("found").GetBoolean());
            Assert.NotEmpty(threadsPad.GetProperty("items").EnumerateArray());

            var modulesPad = await _app.InvokeAsync("od.debug.pad-snapshot", "LoadedModulesPad");
            Assert.True(modulesPad.GetProperty("found").GetBoolean());
            Assert.Equal(JsonValueKind.Array, modulesPad.GetProperty("items").ValueKind);
        }
        finally
        {
            await _app.InvokeAsync("od.debug.stop");
        }
    }

    [Fact]
    public async Task StepIntoAndStepOver_UpdateCurrentFrameAndLocals()
    {
        var program = ProgramPath;
        var callLine = FindLine(program, "var message = ComputeGreeting(\"World\");");
        var writeLine = FindLine(program, "Console.WriteLine(message);");

        await _app.InvokeAsync("od.open-solution", _app.DebugTestProjectPath);
        await _app.InvokeAsync("od.open-file", program);
        await _app.InvokeAsync("od.debug.clear-breakpoints");
        await _app.InvokeAsync("od.debug.set-breakpoint", program, callLine);

        try
        {
            var start = await _app.InvokeAsync("od.debug.start", _app.DebugTestProjectPath, true, 45);
            Assert.True(start.GetProperty("stopped").GetBoolean(), start.ToString());

            var stepInto = await _app.InvokeAsync("od.debug.step-into", 30);
            Assert.True(stepInto.GetProperty("stopped").GetBoolean(), stepInto.ToString());

            var stackAfterStepInto = await WaitForTopFrameNameAsync("ComputeGreeting", 5);
            var topFrameAfterStepInto = stackAfterStepInto.EnumerateArray().First();
            Assert.Contains("ComputeGreeting", topFrameAfterStepInto.GetProperty("Name").GetString());
            Assert.Contains(stackAfterStepInto.EnumerateArray(), f => f.GetProperty("Name").GetString()!.Contains("Main"));

            var localsInsideMethod = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(localsInsideMethod.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "name"
                && v.GetProperty("Value").GetString()!.Contains("World"));

            var stepOut = await _app.InvokeAsync("od.debug.step-out", 30);
            Assert.True(stepOut.GetProperty("stopped").GetBoolean(), stepOut.ToString());
            Assert.True(stepOut.GetProperty("currentLine").GetInt32() >= callLine);

            var stepOver = await _app.InvokeAsync("od.debug.step-over", 30);
            Assert.True(stepOver.GetProperty("stopped").GetBoolean(), stepOver.ToString());
            var topFrameAfterStepOver = await WaitForTopFrameLineAsync(writeLine, 5);
            Assert.Equal(writeLine, topFrameAfterStepOver.GetProperty("Line").GetInt32());

            var localsAfterStepOver = await _app.InvokeAsync("od.debug.locals");
            Assert.Contains(localsAfterStepOver.EnumerateArray(), v =>
                v.GetProperty("Name").GetString() == "message"
                && v.GetProperty("Value").GetString()!.Contains("Hello, World!"));
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
        throw new InvalidOperationException($"Marker '{marker}' not found in {path}.");
    }

    static string Normalize(string? path) => (path ?? string.Empty).Replace('\\', '/');

    async Task<JsonElement> WaitForTopFrameLineAsync(int expectedLine, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement result = default;
        while (DateTime.UtcNow < deadline)
        {
            var stack = await _app.InvokeAsync("od.debug.call-stack");
            result = stack.EnumerateArray().First();
            if (result.GetProperty("Line").GetInt32() == expectedLine)
                break;
            await Task.Delay(100);
        }
        return result;
    }

    async Task<JsonElement> WaitForTopFrameNameAsync(string expectedName, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        JsonElement result = default;
        while (DateTime.UtcNow < deadline)
        {
            result = await _app.InvokeAsync("od.debug.call-stack");
            var frames = result.EnumerateArray().ToArray();
            if (frames.Length > 0 && frames[0].GetProperty("Name").GetString()!.Contains(expectedName))
                break;
            await Task.Delay(100);
        }
        return result;
    }
}
