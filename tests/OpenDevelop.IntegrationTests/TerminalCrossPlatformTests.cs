using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// A deliberately small, shell-agnostic Terminal journey for the Windows CI lane.
/// The richer <see cref="TerminalPadTests"/> suite uses POSIX programs and remains macOS coverage.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class TerminalCrossPlatformTests(OpenDevelopAppFixture app)
{
	[Fact]
	public async Task TerminalPad_StartsTheDetectedShell_RendersOutput_AndSplitsBothWays()
	{
		var shown = await app.InvokeAsync("od.show-pad", "TerminalPad");
		Assert.True(shown.GetProperty("found").GetBoolean(), shown.ToString());

		var shellInfo = await app.InvokeAsync("od.terminal.shells");
		Assert.True(shellInfo.GetProperty("success").GetBoolean(), shellInfo.ToString());
		var shells = shellInfo.GetProperty("shells").EnumerateArray().Select(s => s.GetString()).ToList();
		Assert.NotEmpty(shells);
		Assert.Contains(shellInfo.GetProperty("default").GetString(), shells);

		var first = await app.InvokeAsync("od.terminal.new");
		Assert.True(first.GetProperty("success").GetBoolean(), first.ToString());
		try {
			await app.InvokeAsync("od.terminal.send", OperatingSystem.IsWindows() ? "echo terminal-cross-platform\r" : "printf 'terminal-cross-platform\\n'\r");
			var screen = await WaitForTextAsync("terminal-cross-platform");
			Assert.True(screen.GetProperty("columns").GetInt32() > 20, screen.ToString());
			Assert.True(screen.GetProperty("rows").GetInt32() > 0, screen.ToString());

			var split = await app.InvokeAsync("od.terminal.split");
			Assert.True(split.GetProperty("success").GetBoolean(), split.ToString());
			Assert.Equal(first.GetProperty("group").GetInt32(), split.GetProperty("group").GetInt32());
			var sessions = await WaitForGroupSizeAsync(first.GetProperty("group").GetInt32(), 2);
			Assert.All(sessions, pane => Assert.True(pane.GetProperty("columns").GetInt32() > 0, pane.ToString()));

			// Exercise the public vertical split action on both CI platforms. It must add a
			// third leaf to the same nested group instead of flattening the horizontal split.
			var vertical = await app.InvokeAsync("od.terminal.split-vertical");
			Assert.True(vertical.GetProperty("success").GetBoolean(), vertical.ToString());
			Assert.Equal(first.GetProperty("group").GetInt32(), vertical.GetProperty("group").GetInt32());
			sessions = await WaitForGroupSizeAsync(first.GetProperty("group").GetInt32(), 3);
			Assert.All(sessions, pane => Assert.True(pane.GetProperty("rows").GetInt32() > 0, pane.ToString()));
		}
		finally {
			await app.InvokeAsync("od.terminal.kill");
			await app.InvokeAsync("od.terminal.kill");
		}
	}

	async Task<JsonElement> WaitForTextAsync(string expected)
	{
		JsonElement last = default;
		for (var attempt = 0; attempt < 50; attempt++) {
			last = await app.InvokeAsync("od.terminal.text");
			if (last.GetProperty("text").GetString()!.Contains(expected, StringComparison.Ordinal))
				return last;
			await Task.Delay(100);
		}
		Assert.Fail($"Terminal never rendered {expected}.\n{last}");
		return default;
	}

	async Task<List<JsonElement>> WaitForGroupSizeAsync(int groupId, int expected)
	{
		List<JsonElement> group = [];
		for (var attempt = 0; attempt < 50; attempt++) {
			var list = await app.InvokeAsync("od.terminal.list");
			group = list.GetProperty("sessions").EnumerateArray().Where(p => p.GetProperty("group").GetInt32() == groupId).ToList();
			if (group.Count == expected)
				return group;
			await Task.Delay(100);
		}
		Assert.Fail($"Terminal group {groupId} never contained {expected} panes.");
		return group;
	}
}
