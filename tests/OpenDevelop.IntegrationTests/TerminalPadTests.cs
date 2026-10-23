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

	[Fact]
	public async Task TerminalPad_FollowsTheIdeTheme()
	{
		await ShowPadAsync();
		var originalTheme = (await app.InvokeAsync("od.terminal.settings")).GetProperty("ideTheme").GetString()!;
		try
		{
			// A terminal started under the Dark theme uses the Dark colours (the theme's tool window background and
			// foreground, and the dark set of ANSI colours: SGR 32 is #0dbc79).
			await app.InvokeAsync("od.workbench.set-theme", "Dark");
			Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());
			await SendAsync("printf '\\033[32mGREENXYZ\\033[0m\\n'\r");
			await WaitForLineAsync("GREENXYZ");
			await Task.Delay(500); // the next prompt may scroll a full screen
			var dark = await app.InvokeAsync("od.terminal.text");
			Assert.Equal("#252526", dark.GetProperty("background").GetString());
			Assert.Equal("#f1f1f1", dark.GetProperty("foreground").GetString());
			var row = Lines(dark).ToList().IndexOf("GREENXYZ");
			Assert.True(row >= 0, "The coloured line is not on the screen:\n" + dark.GetProperty("text").GetString());
			Assert.Equal("#0dbc79", (await app.InvokeAsync("od.terminal.cell", 0, row)).GetProperty("foreground").GetString());

			// Switching the IDE theme recolours the terminal that is already open, text on screen included.
			await app.InvokeAsync("od.workbench.set-theme", "Light");
			JsonElement light = default;
			for (var attempt = 0; attempt < 100; attempt++)
			{
				light = await app.InvokeAsync("od.terminal.text");
				if (light.GetProperty("background").GetString() == "#f5f5f5")
					break;
				await Task.Delay(100);
			}
			Assert.Equal("#f5f5f5", light.GetProperty("background").GetString());
			Assert.Equal("#000000", light.GetProperty("foreground").GetString());
			var greenOnLight = await app.InvokeAsync("od.terminal.cell", 0, row);
			Assert.Equal("G", greenOnLight.GetProperty("text").GetString());
			Assert.Equal("#00bc00", greenOnLight.GetProperty("foreground").GetString());
			await app.InvokeAsync("od.terminal.kill");
		}
		finally
		{
			await app.InvokeAsync("od.workbench.set-theme", originalTheme);
		}
	}

	[Fact]
	public async Task TerminalPad_FindsLinksInTheOutput_AndOpensFilesInTheEditor()
	{
		await app.EnsureSolutionOpenAsync(app.SolutionExplorerFixturePath);
		await ShowPadAsync();
		var program = Path.Combine(Path.GetDirectoryName(app.SolutionExplorerFixturePath)!, "SampleApp", "Program.cs");
		Assert.True(File.Exists(program), program);
		var scratch = $"/tmp/od-link-test-{Guid.NewGuid():N}.cs".Substring(0, 24) + ".cs"; // short: it must fit a row next to its prefix
		File.WriteAllLines(scratch, Enumerable.Range(1, 8).Select(i => $"// line {i}"));
		try
		{
			// The terminal starts in the solution folder, so a relative path resolves against it. One link per line, each with a
			// prefix that the typed command line does not start with, so the output rows can be told from the echoed input.
			Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());
			await SendAsync($"P='{scratch}'; printf 'abs %s:3:5\\n' \"$P\"; printf 'rel SampleApp/Program.cs(2,1)\\n'; printf 'none nothere/x.cs:9\\n'; printf 'big %s:99999999999999999999:5\\n' \"$P\"; printf 'wide %s:\uff11\uff12\\n' \"$P\"; printf 'web https://example.com/a.b-c, done\\n'\r");
			await WaitForLineAsync("web https://example.com/a.b-c, done");
			await Task.Delay(500);
			var screen = await app.InvokeAsync("od.terminal.text");
			var lines = Lines(screen).ToList();
			int RowOf(string prefix) => lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
			var all = (await app.InvokeAsync("od.terminal.links")).GetProperty("links").EnumerateArray().ToList();
			List<JsonElement> OnRow(string prefix) => all.Where(l => l.GetProperty("row").GetInt32() == RowOf(prefix)).ToList();
			Assert.True(RowOf("abs ") >= 0 && RowOf("rel ") >= 0 && RowOf("none ") >= 0 && RowOf("big ") >= 0 && RowOf("wide ") >= 0 && RowOf("web ") >= 0, string.Join("\n", lines));

			var abs = Assert.Single(OnRow("abs "));
			Assert.Equal("file", abs.GetProperty("kind").GetString());
			Assert.Equal(scratch, abs.GetProperty("target").GetString());
			Assert.Equal((3, 5), (abs.GetProperty("line").GetInt32(), abs.GetProperty("column").GetInt32()));
			Assert.Equal(4, abs.GetProperty("startColumn").GetInt32()); // right after "abs "

			var rel = Assert.Single(OnRow("rel "));
			Assert.Equal(Path.GetFullPath(program), rel.GetProperty("target").GetString());
			Assert.Equal((2, 1), (rel.GetProperty("line").GetInt32(), rel.GetProperty("column").GetInt32()));

			Assert.Empty(OnRow("none ")); // a path that does not exist is not a link

			// A line number that cannot be one (20 digits; full-width digits) is not a suffix: the path alone is the link, and nothing throws.
			foreach (var prefix in new[] { "big ", "wide " })
			{
				var odd = Assert.Single(OnRow(prefix));
				Assert.Equal(scratch, odd.GetProperty("target").GetString());
				Assert.Equal((0, 0), (odd.GetProperty("line").GetInt32(), odd.GetProperty("column").GetInt32()));
			}

			var web = Assert.Single(OnRow("web "));
			Assert.Equal("url", web.GetProperty("kind").GetString());
			Assert.Equal("https://example.com/a.b-c", web.GetProperty("target").GetString()); // the trailing comma is not part of it

			// Following a file link (what Ctrl/Cmd+click does) opens it in the editor at its line and column; a cell with no link does nothing.
			var nothing = await app.InvokeAsync("od.terminal.open-link", 0, RowOf("abs "));
			Assert.False(nothing.GetProperty("opened").GetBoolean(), nothing.ToString());
			var opened = await app.InvokeAsync("od.terminal.open-link", abs.GetProperty("startColumn").GetInt32() + 2, RowOf("abs "));
			Assert.True(opened.GetProperty("opened").GetBoolean(), opened.ToString());
			var view = await app.InvokeAsync("od.active-view");
			Assert.EndsWith(Path.GetFileName(scratch), view.GetProperty("fileName").GetString());
			Assert.Equal((3, 5), (view.GetProperty("caretLine").GetInt32(), view.GetProperty("caretColumn").GetInt32()));

			var url = await app.InvokeAsync("od.terminal.open-link", web.GetProperty("startColumn").GetInt32(), RowOf("web "));
			Assert.Equal("url", url.GetProperty("kind").GetString());
			await app.InvokeAsync("od.terminal.kill");
		}
		finally
		{
			await app.InvokeAsync("od.close-all-document-views");
			File.Delete(scratch);
		}
	}

	[Fact]
	public async Task TerminalPad_FindsTextInTheScrollback()
	{
		await ShowPadAsync();
		Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());
		// 200 rows, "needle" on rows 20 and 150 only (built with two string parts so the typed command line does not contain it).
		await SendAsync("awk 'BEGIN { for (i = 1; i <= 200; i++) if (i == 20 || i == 150) print \"need\" \"le \" i; else print \"line \" i }'\r");
		await WaitForLineAsync("line 200");
		await Task.Delay(500);
		async Task<ulong> OffsetAsync() => (await app.InvokeAsync("od.terminal.text")).GetProperty("scrollback").GetProperty("offset").GetUInt64();
		async Task<bool> ShowsAsync(string text) => Lines(await app.InvokeAsync("od.terminal.text")).Any(l => l.Contains(text, StringComparison.Ordinal));
		var bottom = await OffsetAsync();

		// Searching alone finds both and leaves the viewport where it was (it has to page through the scrollback to look).
		var searched = await app.InvokeAsync("od.terminal.find", "needle", false, "none");
		Assert.Equal(2, searched.GetProperty("count").GetInt32());
		Assert.Equal(bottom, await OffsetAsync());

		// First / next / previous scroll the match into view and wrap around.
		var first = await app.InvokeAsync("od.terminal.find", "", false, "first");
		Assert.Equal("needle", first.GetProperty("match").GetProperty("text").GetString());
		Assert.True(await ShowsAsync("needle 20"), "first match is row 20");
		Assert.Equal(0, first.GetProperty("current").GetInt32());
		Assert.True((await app.InvokeAsync("od.terminal.find", "", false, "next")).GetProperty("current").GetInt32() == 1 && await ShowsAsync("needle 150"));
		Assert.True((await app.InvokeAsync("od.terminal.find", "", false, "next")).GetProperty("current").GetInt32() == 0 && await ShowsAsync("needle 20"));
		Assert.True((await app.InvokeAsync("od.terminal.find", "", false, "previous")).GetProperty("current").GetInt32() == 1 && await ShowsAsync("needle 150"));

		// Case: insensitive by default, exact on request.
		Assert.Equal(2, (await app.InvokeAsync("od.terminal.find", "NEEDLE", false, "none")).GetProperty("count").GetInt32());
		var exact = await app.InvokeAsync("od.terminal.find", "NEEDLE", true, "first");
		Assert.Equal(0, exact.GetProperty("count").GetInt32());
		Assert.Equal(JsonValueKind.Null, exact.GetProperty("match").ValueKind);

		// The find bar: the shortcut opens it, typing a query searches and goes to the first match, closing hides it again.
		await app.InvokeAsync("od.terminal.find", "", false, "clear");
		var keyed = await app.InvokeAsync("od.terminal.key", "F", "ctrl,shift");
		Assert.True(keyed.GetProperty("handled").GetBoolean(), keyed.ToString());
		Assert.True((await app.InvokeAsync("od.terminal.find-bar")).GetProperty("open").GetBoolean());
		var typed = await app.InvokeAsync("od.terminal.find-bar", "needle");
		Assert.Equal("1 of 2", typed.GetProperty("status").GetString());
		Assert.True(await ShowsAsync("needle 20"));
		// Typing is debounced: six keystrokes in one go cost one search, which runs after a short pause.
		var searchesBefore = (await app.InvokeAsync("od.terminal.find-bar", "x")).GetProperty("searches").GetInt32(); // "x" opens it and searches once
		var typedNow = await app.InvokeAsync("od.terminal.find-bar", "", false, "needle");
		Assert.Equal(searchesBefore, typedNow.GetProperty("searches").GetInt32()); // nothing searched yet
		JsonElement settled = default;
		for (var attempt = 0; attempt < 50; attempt++)
		{
			settled = await app.InvokeAsync("od.terminal.find-bar");
			if (settled.GetProperty("status").GetString() == "1 of 2")
				break;
			await Task.Delay(100);
		}
		Assert.Equal("1 of 2", settled.GetProperty("status").GetString());
		Assert.Equal(searchesBefore + 1, settled.GetProperty("searches").GetInt32());
		var none = await app.InvokeAsync("od.terminal.find-bar", "no-such-text-anywhere");
		Assert.Equal("No results", none.GetProperty("status").GetString());
		var closed = await app.InvokeAsync("od.terminal.find-bar", "", true);
		Assert.False(closed.GetProperty("open").GetBoolean());

		await app.InvokeAsync("od.terminal.kill");
	}

	[Fact]
	public async Task TerminalPad_KnowsWhereTheShellIs_ForLinksAndSplits()
	{
		await ShowPadAsync();
		var folder = $"/tmp/od-cwd-{Guid.NewGuid():N}".Substring(0, 20);
		Directory.CreateDirectory(folder);
		File.WriteAllLines(Path.Combine(folder, "cwdfile.cs"), new[] { "// one", "// two", "// three" });
		try
		{
			Assert.True((await app.InvokeAsync("od.terminal.new", "/bin/sh")).GetProperty("success").GetBoolean());
			var started = (await app.InvokeAsync("od.terminal.cwd")).GetProperty("initial").GetString();
			Assert.NotEqual(folder, started);

			// The directory comes from the operating system, so a plain cd is enough (no shell integration needed).
			await SendAsync($"cd {folder}\r");
			string current = "";
			for (var attempt = 0; attempt < 50; attempt++)
			{
				current = (await app.InvokeAsync("od.terminal.cwd")).GetProperty("current").GetString()!;
				if (current.EndsWith(Path.GetFileName(folder), StringComparison.Ordinal))
					break;
				await Task.Delay(100);
			}
			Assert.EndsWith(Path.GetFileName(folder), current); // /tmp is /private/tmp on macOS, so only the end is compared
			Assert.True(File.Exists(Path.Combine(current, "cwdfile.cs")));

			// A relative file reference resolves against that directory, not the one the shell started in.
			await SendAsync("printf 'lnk %s:2\\n' cwdfile.cs\r");
			await WaitForLineAsync("lnk cwdfile.cs:2");
			await Task.Delay(500);
			var lines = Lines(await app.InvokeAsync("od.terminal.text")).ToList();
			var row = lines.FindIndex(l => l.StartsWith("lnk ", StringComparison.Ordinal));
			Assert.True(row >= 0, string.Join("\n", lines));
			var link = (await app.InvokeAsync("od.terminal.links")).GetProperty("links").EnumerateArray().Single(l => l.GetProperty("row").GetInt32() == row);
			Assert.Equal(Path.Combine(current, "cwdfile.cs"), link.GetProperty("target").GetString());
			Assert.Equal(2, link.GetProperty("line").GetInt32());

			// A split starts in the directory the terminal beside it is in now.
			var split = await app.InvokeAsync("od.terminal.split");
			Assert.True(split.GetProperty("success").GetBoolean(), split.ToString());
			Assert.Equal(current, (await app.InvokeAsync("od.terminal.cwd")).GetProperty("current").GetString());
			await SendAsync("echo here-$(basename \"$PWD\")\r");
			await WaitForAsync("here-" + Path.GetFileName(folder));
			await app.InvokeAsync("od.terminal.kill");
			await app.InvokeAsync("od.terminal.kill");
		}
		finally
		{
			Directory.Delete(folder, true);
		}
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
