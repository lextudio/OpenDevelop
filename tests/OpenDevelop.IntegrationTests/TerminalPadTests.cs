using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// The Terminal pad end to end through its DevFlow actions: a real login shell in a real PTY, fed
/// through libghostty-vt and read back from the render state. The PTY size must follow the view
/// (<c>stty size</c> is the shell's own view of it), and a full-screen program must use and release
/// the alternate screen.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class TerminalPadTests
{
	readonly OpenDevelopAppFixture app;

	public TerminalPadTests(OpenDevelopAppFixture app) => this.app = app;

	[Fact]
	public async Task TerminalPad_RunsShells_TracksSize_AndHandlesFullScreenPrograms()
	{
		await ShowPadAsync();

		// A fresh terminal using /bin/sh keeps the journey independent of the user's shell rc files.
		var before = (await app.InvokeAsync("od.terminal.list")).GetProperty("sessions").GetArrayLength();
		var created = await app.InvokeAsync("od.terminal.new", "/bin/sh");
		Assert.True(created.GetProperty("success").GetBoolean(), created.ToString());
		var list = await app.InvokeAsync("od.terminal.list");
		Assert.Equal(before + 1, list.GetProperty("sessions").GetArrayLength());
		Assert.True(list.GetProperty("sessions").EnumerateArray().Last().GetProperty("active").GetBoolean());

		// Text, colours (SGR must not leak into the text), and the environment the host promises.
		await SendAsync("printf '\\033[31mred\\033[0m-%s\\n' \"$TERM\"\r");
		var screen = await WaitForAsync("red-xterm-256color");
		Assert.DoesNotContain("\u001b", screen.GetProperty("text").GetString(), StringComparison.Ordinal);

		// The shell's idea of the window size is the size of the visible grid.
		var columns = screen.GetProperty("columns").GetInt32();
		var rows = screen.GetProperty("rows").GetInt32();
		Assert.True(columns > 20 && rows >= 1, screen.ToString());
		await SendAsync("echo size=$(stty size)\r");
		await WaitForAsync($"size={rows} {columns}");

		// Keys go through the view's key handler and libghostty's encoder; `cat -v` shows the bytes that arrive.
		await SendAsync("cat -v\r");
		await KeyAsync("Up");
		await KeyAsync("F5");
		await KeyAsync("Tab", "shift");
		await KeyAsync("X", "alt");
		await SendAsync("\r"); // cat only echoes a complete line
		var keys = await WaitForAsync("^[[A^[[15~^[[Z^[x");
		// Application cursor keys (DECCKM) change what the same key sends; the encoder follows the terminal's mode.
		await SendAsync("\u0004"); // EOF ends cat
		await SendAsync("printf '\\033[?1h'; cat -v\r");
		await KeyAsync("Up");
		await SendAsync("\r");
		await WaitForAsync("^[OA");
		Assert.False((await app.InvokeAsync("od.terminal.key", "Oem3")).GetProperty("handled").GetBoolean(),
			"A plain text key must be left to text input.");
		// Ctrl+C reaches the shell as ETX and interrupts a running program.
		await SendAsync("\u0004sleep 60; echo interrupted-$?\r");
		await KeyAsync("C", "ctrl");
		await WaitForAsync("interrupted-130");

		// Full-screen program: its screen replaces the shell's and the shell's comes back on exit. The marker is
		// concatenated by vim so the echoed command line cannot satisfy the wait.
		await SendAsync("echo before-vim; vim -u NONE -c 'call setline(1, \"inside-\" . \"vim\")'\r");
		await WaitForAsync("inside-vim");
		await SendAsync("\u001b:q!\r");
		var back = await WaitForAsync("before-vim");
		Assert.DoesNotContain("inside-vim", back.GetProperty("text").GetString(), StringComparison.Ordinal);

		// Split: a second shell beside the first in the same group, each with its own share of the width.
		var firstIndex = created.GetProperty("index").GetInt32();
		var widthBefore = (await app.InvokeAsync("od.terminal.text")).GetProperty("columns").GetInt32();
		// A selection reaching the right edge must not survive the pane getting narrower (it would point outside the new grid).
		var preSplit = await app.InvokeAsync("od.terminal.select", 0, 0, widthBefore - 1, 1);
		Assert.True(preSplit.GetProperty("success").GetBoolean() && preSplit.GetProperty("text").GetString()!.Length > 0, "There was nothing selected to lose.\n" + preSplit);
		var split = await app.InvokeAsync("od.terminal.split");
		Assert.True(split.GetProperty("success").GetBoolean(), split.ToString());
		Assert.Equal(created.GetProperty("group").GetInt32(), split.GetProperty("group").GetInt32());
		await SendAsync("echo pane-two\r");
		await WaitForAsync("pane-two");
		JsonElement panes = default;
		for (var attempt = 0; attempt < 50; attempt++)
		{
			panes = await app.InvokeAsync("od.terminal.list");
			var group = panes.GetProperty("sessions").EnumerateArray().Where(p => p.GetProperty("group").GetInt32() == created.GetProperty("group").GetInt32()).ToList();
			if (group.Count == 2 && group.All(p => p.GetProperty("columns").GetInt32() < widthBefore))
				break;
			await Task.Delay(100);
		}
		var inGroup = panes.GetProperty("sessions").EnumerateArray().Where(p => p.GetProperty("group").GetInt32() == created.GetProperty("group").GetInt32()).ToList();
		Assert.True(inGroup.Count == 2 && inGroup.All(p => p.GetProperty("columns").GetInt32() < widthBefore), $"The split panes did not share the width ({widthBefore} columns before).\n{panes}");
		Assert.Equal(2, panes.GetProperty("labels").EnumerateArray().Count(l => l.GetString()!.EndsWith("(split)", StringComparison.Ordinal)));

		// The session list drives which pane receives input; the other pane never saw the text.
		var activated = await app.InvokeAsync("od.terminal.activate", firstIndex);
		Assert.True(activated.GetProperty("success").GetBoolean(), activated.ToString());
		Assert.Equal("", (await app.InvokeAsync("od.terminal.copy")).GetProperty("text").GetString());
		var first = await app.InvokeAsync("od.terminal.text");
		Assert.DoesNotContain("pane-two", first.GetProperty("text").GetString(), StringComparison.Ordinal);
		Assert.Contains("before-vim", first.GetProperty("text").GetString(), StringComparison.Ordinal);

		// Killing the active terminal removes just that pane; its split sibling keeps running.
		var kill = await app.InvokeAsync("od.terminal.kill");
		Assert.True(kill.GetProperty("success").GetBoolean(), kill.ToString());
		var after = await app.InvokeAsync("od.terminal.list");
		var remaining = after.GetProperty("sessions").EnumerateArray().Select(p => p.GetProperty("pid").GetInt32()).ToList();
		Assert.DoesNotContain(created.GetProperty("pid").GetInt32(), remaining);
		Assert.Contains(split.GetProperty("pid").GetInt32(), remaining);
	}

	[Fact]
	public async Task TerminalPad_ShowsWideText_Scrolls_Selects_AndPastesBracketed()
	{
		await ShowPadAsync();
		var created = await app.InvokeAsync("od.terminal.new", "/bin/sh");
		Assert.True(created.GetProperty("success").GetBoolean(), created.ToString());

		// A double-width character occupies two cells but is one character of text.
		await SendAsync("printf 'AB\u4f60\u597dC\\n'\r");
		await WaitForAsync("AB\u4f60\u597dC\n");

		// Scrollback: output longer than the screen is kept; scrolling back moves the viewport, typing returns to the bottom.
		await SendAsync("seq 1 200\r");
		var bottom = await WaitForLineAsync("200");
		var scrollback = bottom.GetProperty("scrollback");
		var rows = bottom.GetProperty("rows").GetInt32();
		Assert.True(scrollback.GetProperty("total").GetUInt64() > (ulong)rows, bottom.ToString());
		Assert.Equal(scrollback.GetProperty("total").GetUInt64(), scrollback.GetProperty("offset").GetUInt64() + scrollback.GetProperty("length").GetUInt64());
		await app.InvokeAsync("od.terminal.scroll", -30);
		var back = await app.InvokeAsync("od.terminal.text");
		Assert.True(back.GetProperty("scrollback").GetProperty("offset").GetUInt64() < scrollback.GetProperty("offset").GetUInt64(), back.ToString());
		Assert.DoesNotContain("200", Lines(back), StringComparer.Ordinal);
		await KeyAsync("Enter"); // any key goes to the shell and shows its end again
		var again = await WaitForLineAsync("200");
		Assert.Equal(again.GetProperty("scrollback").GetProperty("total").GetUInt64(), again.GetProperty("scrollback").GetProperty("offset").GetUInt64() + again.GetProperty("scrollback").GetProperty("length").GetUInt64());

		// Wheel over scrollback: one notch (120) is three lines; small precision-device deltas add up instead of vanishing.
		async Task<ulong> OffsetAsync() => (await app.InvokeAsync("od.terminal.text")).GetProperty("scrollback").GetProperty("offset").GetUInt64();
		var atBottom = await OffsetAsync();
		await app.InvokeAsync("od.terminal.wheel", 120);
		Assert.Equal(atBottom - 3, await OffsetAsync());
		await app.InvokeAsync("od.terminal.wheel", 20);
		Assert.Equal(atBottom - 3, await OffsetAsync());
		await app.InvokeAsync("od.terminal.wheel", 20);
		Assert.Equal(atBottom - 4, await OffsetAsync());
		await KeyAsync("Enter");
		await WaitForLineAsync("200");

		// Selection and Copy: the selected cells come back as text without the blanks after them.
		await SendAsync("echo select-me-please\r");
		await WaitForLineAsync("select-me-please");
		await Task.Delay(500); // let the next prompt arrive: on a full screen it scrolls everything up by one row
		var screen = await app.InvokeAsync("od.terminal.text");
		var row = Lines(screen).ToList().IndexOf("select-me-please");
		Assert.True(row >= 0, "The line to select is not on the screen any more:\n" + screen.GetProperty("text").GetString());
		var selected = await app.InvokeAsync("od.terminal.select", 0, row, 40, row);
		Assert.Equal("select-me-please", selected.GetProperty("text").GetString());
		var copied = await app.InvokeAsync("od.terminal.copy");
		Assert.Equal("select-me-please", copied.GetProperty("text").GetString());

		// A long line the terminal wrapped is one line when copied: no newline at the wrap, nothing trimmed there.
		var columns = (await app.InvokeAsync("od.terminal.text")).GetProperty("columns").GetInt32();
		var zeros = columns + columns / 2; // wrap-<zeros>-end is longer than a row and shorter than two, whatever the pad width
		await SendAsync($"printf 'wrap-%0{zeros}d-end\\n' 0\r");
		var wrapScreen = await WaitForAsync("-end");
		await Task.Delay(500);
		var wrapLines = Lines(await app.InvokeAsync("od.terminal.text")).ToList();
		var first = wrapLines.FindIndex(l => l.StartsWith("wrap-0", StringComparison.Ordinal) && !l.Contains("printf", StringComparison.Ordinal));
		Assert.True(first >= 0 && first + 1 < wrapLines.Count && wrapLines[first + 1].EndsWith("-end", StringComparison.Ordinal),
			"The long line did not wrap over two rows as expected:\n" + string.Join("\n", wrapLines));
		var wrapped = await app.InvokeAsync("od.terminal.select", 0, first, wrapScreen.GetProperty("columns").GetInt32() - 1, first + 1);
		var expected = "wrap-" + new string('0', zeros) + "-end";
		Assert.Equal(expected, wrapped.GetProperty("text").GetString());

		// Bracketed paste is used only once the program asked for it, and newlines are sent as CR.
		await SendAsync("cat -v\r");
		await app.InvokeAsync("od.terminal.paste", "plain\nraw");
		await SendAsync("\r");
		var plainPaste = await WaitForAsync("plain");
		Assert.False(plainPaste.GetProperty("bracketedPaste").GetBoolean());
		Assert.DoesNotContain("^[[200~", plainPaste.GetProperty("text").GetString(), StringComparison.Ordinal); // no framing before the program asked for it
		await SendAsync("\u0004printf '\\033[?2004h'; cat -v\r");
		await WaitForBracketedAsync(true);
		await app.InvokeAsync("od.terminal.paste", "one\ntwo");
		await SendAsync("\r");
		await WaitForAsync("^[[200~one");
		await WaitForAsync("two^[[201~");
		await SendAsync("\u0004printf '\\033[?2004l'\r");
		await WaitForBracketedAsync(false);

		await app.InvokeAsync("od.terminal.kill");
	}

	[Fact]
	public async Task TerminalPad_IsReachableFromTheMenu_TheProjectBrowser_AndTheShellList()
	{
		await ShowPadAsync();

		// Shells: the login shell is offered, and the stored default is what a plain New Terminal starts.
		var shells = await app.InvokeAsync("od.terminal.shells");
		var paths = shells.GetProperty("shells").EnumerateArray().Select(p => p.GetString()!).ToList();
		var original = shells.GetProperty("default").GetString()!;
		Assert.Contains("/bin/sh", paths);
		try
		{
			Assert.Equal("/bin/sh", (await app.InvokeAsync("od.terminal.shells", "/bin/sh")).GetProperty("default").GetString());
			var plain = await app.InvokeAsync("od.terminal.new");
			Assert.Equal("sh", plain.GetProperty("shell").GetString());
			await app.InvokeAsync("od.terminal.kill");
		}
		finally
		{
			await app.InvokeAsync("od.terminal.shells", original);
		}

		// A shell that does not exist is reported, not thrown.
		var bad = await app.InvokeAsync("od.terminal.new", "/nonexistent/shell");
		Assert.False(bad.GetProperty("success").GetBoolean(), bad.ToString());

		// View > New Terminal.
		var before = (await app.InvokeAsync("od.terminal.list")).GetProperty("sessions").GetArrayLength();
		var viaMenu = await app.InvokeAsync("od.menu.invoke", "OpenDevelop.Terminal.NewTerminalFromMenuCommand");
		Assert.True(viaMenu.GetProperty("success").GetBoolean(), viaMenu.ToString());
		await WaitForSessionCountAsync(before + 1);
		await app.InvokeAsync("od.terminal.kill");

		// Project Browser > Open in Terminal on a project node: the integrated terminal opens in that project's folder.
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		Assert.True((await app.InvokeAsync("od.show-pad", "ProjectBrowserPad")).GetProperty("found").GetBoolean());
		var count = (await app.InvokeAsync("od.terminal.list")).GetProperty("sessions").GetArrayLength();
		var invoked = await app.InvokeAsync("od.project-browser.open-terminal", "Project", "SampleApp");
		Assert.True(invoked.GetProperty("success").GetBoolean(), invoked.ToString());
		await WaitForSessionCountAsync(count + 1);
		await SendAsync("echo in-$(basename \"$PWD\")\r");
		await WaitForAsync("in-SampleApp");
		await app.InvokeAsync("od.terminal.kill");
	}

	[Fact]
	public async Task TerminalPad_ReportsMouseToPrograms_ScrollsAlternateScreens_AndSelectsWordsAndLines()
	{
		await ShowPadAsync();
		Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());

		// Double click selects a word, triple click the line.
		await SendAsync("echo one two three\r");
		await WaitForLineAsync("one two three");
		await Task.Delay(500);
		var row = Lines(await app.InvokeAsync("od.terminal.text")).ToList().IndexOf("one two three");
		Assert.Equal("two", (await app.InvokeAsync("od.terminal.click", 5, row, 2)).GetProperty("text").GetString());
		Assert.Equal("one two three", (await app.InvokeAsync("od.terminal.click", 5, row, 3)).GetProperty("text").GetString());

		// Without mouse reporting a mouse event goes nowhere; once the program enables it (SGR, button tracking),
		// the encoder produces the report for the cell: column and row are 1-based.
		await SendAsync("cat -v\r");
		var badModifier = await app.InvokeAsync("od.terminal.mouse", "press", "left", 5, 2, "bogus");
		Assert.False(badModifier.GetProperty("success").GetBoolean(), badModifier.ToString());
		var ignored = await app.InvokeAsync("od.terminal.mouse", "press", "left", 5, 2);
		Assert.False(ignored.GetProperty("sent").GetBoolean(), ignored.ToString());
		await SendAsync("\u0004printf '\\033[?1000h\\033[?1006h'; cat -v\r");
		await WaitForMouseTrackingAsync(true);
		Assert.True((await app.InvokeAsync("od.terminal.mouse", "press", "left", 5, 2)).GetProperty("sent").GetBoolean());
		Assert.True((await app.InvokeAsync("od.terminal.mouse", "release", "left", 5, 2)).GetProperty("sent").GetBoolean());
		await app.InvokeAsync("od.terminal.wheel", 120, 5, 2);
		await SendAsync("\r");
		await WaitForAsync("^[[<0;6;3M^[[<0;6;3m^[[<64;6;3M");
		await SendAsync("\u0004printf '\\033[?1000l\\033[?1006l'\r");
		await WaitForMouseTrackingAsync(false);

		// On the alternate screen the wheel is the arrow keys: `less` moves three lines per notch.
		await SendAsync("seq 1 100 | less\r");
		await WaitForLineAsync("1");
		var top = await app.InvokeAsync("od.terminal.text");
		Assert.True(top.GetProperty("alternateScreen").GetBoolean(), top.ToString());
		await app.InvokeAsync("od.terminal.wheel", -120);
		var scrolled = await WaitForLineAsync("4");
		Assert.Equal("4", Lines(scrolled).First());
		await SendAsync("q");

		await app.InvokeAsync("od.terminal.kill");
	}

	[Fact]
	public async Task TerminalPad_FollowsItsSettings()
	{
		await ShowPadAsync();
		var original = await app.InvokeAsync("od.terminal.settings");
		var fontSize = original.GetProperty("fontSize").GetDouble();
		var scrollback = original.GetProperty("scrollbackLines").GetInt32();
		try
		{
			Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());
			var before = await WaitForColumnsAsync(c => c > 0);

			// A bigger font means fewer columns in the same pad, and the shell is told (stty size).
			await app.InvokeAsync("od.terminal.settings", 26, -1);
			var larger = await WaitForColumnsAsync(c => c < before.columns);
			await SendAsync("echo cols=$(stty size)\r");
			await WaitForAsync($"cols={larger.rows} {larger.columns}");
			await app.InvokeAsync("od.terminal.settings", fontSize, -1);
			await WaitForColumnsAsync(c => c == before.columns);
			await app.InvokeAsync("od.terminal.kill");

			// Scrollback is fixed when a terminal starts: none at 0 lines, a lot at the default.
			await app.InvokeAsync("od.terminal.settings", 0, 0);
			Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());
			await SendAsync("seq 1 500\r");
			var screen = await WaitForLineAsync("500");
			Assert.Equal((ulong)screen.GetProperty("rows").GetInt32(), screen.GetProperty("scrollback").GetProperty("total").GetUInt64());
			await app.InvokeAsync("od.terminal.kill");

			await app.InvokeAsync("od.terminal.settings", 0, 2000);
			Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());
			await SendAsync("seq 1 500\r");
			var kept = await WaitForLineAsync("500");
			Assert.True(kept.GetProperty("scrollback").GetProperty("total").GetUInt64() > 400, kept.ToString());
			await app.InvokeAsync("od.terminal.kill");
		}
		finally
		{
			await app.InvokeAsync("od.terminal.settings", fontSize, scrollback);
		}
	}

	async Task<(int columns, int rows)> WaitForColumnsAsync(Func<int, bool> accept)
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 50; attempt++)
		{
			last = await app.InvokeAsync("od.terminal.text");
			if (last.TryGetProperty("columns", out var columns) && accept(columns.GetInt32()))
				return (columns.GetInt32(), last.GetProperty("rows").GetInt32());
			await Task.Delay(100);
		}
		Assert.Fail($"The terminal never reached the expected width.\n{last}");
		return default;
	}

	[Fact]
	public async Task TerminalPad_CyclesRenamesAndShowsTheEndOfAShell()
	{
		await ShowPadAsync();
		var before = (await app.InvokeAsync("od.terminal.list")).GetProperty("sessions").GetArrayLength();
		var first = await app.InvokeAsync("od.terminal.new", "/bin/sh");
		var second = await app.InvokeAsync("od.terminal.new", "/bin/sh");
		var firstIndex = first.GetProperty("index").GetInt32();
		var secondIndex = second.GetProperty("index").GetInt32();
		Assert.Equal(before + 1, secondIndex);

		async Task<int> ActiveIndexAsync()
			=> (await app.InvokeAsync("od.terminal.list")).GetProperty("sessions").EnumerateArray().First(p => p.GetProperty("active").GetBoolean()).GetProperty("index").GetInt32();

		// View > Next/Previous Terminal walk the session list and wrap around at both ends.
		Assert.Equal(secondIndex, await ActiveIndexAsync());
		Assert.True((await app.InvokeAsync("od.menu.invoke", "OpenDevelop.Terminal.NextTerminalCommand")).GetProperty("success").GetBoolean());
		Assert.Equal(0, await ActiveIndexAsync());
		Assert.True((await app.InvokeAsync("od.menu.invoke", "OpenDevelop.Terminal.PreviousTerminalCommand")).GetProperty("success").GetBoolean());
		Assert.Equal(secondIndex, await ActiveIndexAsync());

		// A name replaces the shell's title in the list; an empty name restores it.
		var named = await app.InvokeAsync("od.terminal.rename", "build");
		Assert.Equal($"{secondIndex + 1}: build", named.GetProperty("labels")[secondIndex].GetString());
		var restored = await app.InvokeAsync("od.terminal.rename", "");
		Assert.DoesNotContain("build", restored.GetProperty("labels")[secondIndex].GetString(), StringComparison.Ordinal);

		// When the shell ends the pane stays, says so, and any key dismisses it; the other terminal is untouched.
		await SendAsync("exit 7\r");
		var ended = await WaitForAsync("[Process exited with code 7]");
		Assert.True(ended.GetProperty("exited").GetBoolean(), ended.ToString());
		Assert.Equal(7, ended.GetProperty("exitCode").GetInt32());
		await KeyAsync("Enter");
		await WaitForSessionCountAsync(before + 1);
		Assert.Equal(firstIndex, await ActiveIndexAsync());
		Assert.DoesNotContain(second.GetProperty("pid").GetInt32(), (await app.InvokeAsync("od.terminal.list")).GetProperty("sessions").EnumerateArray().Select(p => p.GetProperty("pid").GetInt32()));
		await app.InvokeAsync("od.terminal.kill");
		await WaitForSessionCountAsync(before);
	}

	async Task WaitForMouseTrackingAsync(bool expected)
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 50; attempt++)
		{
			last = await app.InvokeAsync("od.terminal.text");
			if (last.TryGetProperty("mouseTracking", out var mode) && mode.GetBoolean() == expected)
				return;
			await Task.Delay(100);
		}
		Assert.Fail($"Mouse tracking never became {expected}.\n{last}");
	}

	async Task WaitForSessionCountAsync(int expected)
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 100; attempt++)
		{
			last = await app.InvokeAsync("od.terminal.list");
			if (last.TryGetProperty("sessions", out var sessions) && sessions.GetArrayLength() == expected)
				return;
			await Task.Delay(100);
		}
		Assert.Fail($"Expected {expected} terminals.\n{last}");
	}

	static IEnumerable<string> Lines(JsonElement screen) => (screen.GetProperty("text").GetString() ?? "").Split('\n');

	async Task<JsonElement> WaitForLineAsync(string line)
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 50; attempt++)
		{
			last = await app.InvokeAsync("od.terminal.text");
			if (last.TryGetProperty("text", out _) && Lines(last).Contains(line))
				return last;
			await Task.Delay(100);
		}
		Assert.Fail($"The terminal never showed a line '{line}'.\n{last}");
		return last;
	}

	async Task WaitForBracketedAsync(bool expected)
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 50; attempt++)
		{
			last = await app.InvokeAsync("od.terminal.text");
			if (last.TryGetProperty("bracketedPaste", out var mode) && mode.GetBoolean() == expected)
				return;
			await Task.Delay(100);
		}
		Assert.Fail($"Bracketed paste never became {expected}.\n{last}");
	}

	/// <summary>
	/// Shows the pad and waits for the terminal it starts by itself when it is first created (that start is
	/// asynchronous): without it an absolute session count taken right away races with that shell appearing.
	/// </summary>
	async Task ShowPadAsync()
	{
		var pad = await app.InvokeAsync("od.show-pad", "TerminalPad");
		Assert.True(pad.GetProperty("found").GetBoolean(), "Terminal pad is not registered.\n" + pad);
		await WaitForAtLeastOneSessionAsync();
	}

	async Task WaitForAtLeastOneSessionAsync()
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 100; attempt++)
		{
			last = await app.InvokeAsync("od.terminal.list");
			if (last.TryGetProperty("sessions", out var sessions) && sessions.GetArrayLength() >= 1)
				return;
			await Task.Delay(100);
		}
		Assert.Fail($"The Terminal pad never started its first terminal.\n{last}");
	}

	async Task KeyAsync(string key, string modifiers = "")
	{
		var result = await app.InvokeAsync("od.terminal.key", key, modifiers);
		Assert.True(result.TryGetProperty("handled", out var handled) && handled.GetBoolean(), $"Terminal did not take {modifiers} {key}: {result}");
	}

	Task<JsonElement> SendAsync(string text) => app.InvokeAsync("od.terminal.send", text);

	async Task<JsonElement> WaitForAsync(string expected)
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 50; attempt++)
		{
			last = await app.InvokeAsync("od.terminal.text");
			if (last.TryGetProperty("text", out var text) && (text.GetString() ?? "").Contains(expected))
				return last;
			await Task.Delay(100);
		}
		Assert.Fail($"The terminal never showed '{expected}'.\n{last}");
		return last;
	}
}
