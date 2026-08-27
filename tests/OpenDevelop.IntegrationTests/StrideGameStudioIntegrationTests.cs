using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace OpenDevelop.IntegrationTests;

/// <summary>
/// End-to-end Stride Game Studio integration: open a real Stride game project, see its .sdpkg
/// Assets subtree in the Projects pad, edit a game script, inspect the tree/editor, and run the
/// game. Driven entirely through DevFlow (see <stride>/sources/tools/Stride.OpenDevelop.AddIn/stride-game-studio.md "Integration
/// test design" for the full case and per-phase blockers).
///
/// Uses the Stride First-Person-Shooter template game IN PLACE (the local port clone, the same
/// checkout the addin's $(StrideRoot) resolves to) rather than a copied fixture, because the
/// game's <c>ProjectReference</c>s point at shared Packs two levels above it; a temp copy would
/// break those paths.
///
/// The addin itself no longer lives in this repo - it is built from the Stride checkout
/// (<c>sources/tools/Stride.OpenDevelop.AddIn</c>) and deploys into this repo's
/// <c>AddIns/DisplayBindings/StrideGameStudio/</c>. So this test SKIPS unless both the Stride
/// checkout and a deployed addin are present, instead of failing on a machine that has neither.
/// </summary>
[Collection("30 Add-ins and specialized fixtures")]
public sealed class StrideGameStudioIntegrationTests : IAsyncDisposable
{
	readonly OpenDevelopAppFixture app;
	readonly string gameProjectPath;
	readonly string gameScriptPath;
	readonly string addinProjectPath;

	// Match the addin's StrideCheckoutRoot default (uno-tools/stride). Adjust if the clone lives
	// elsewhere: pass via env var STRIDE_CHECKOUT_ROOT.
	static string StrideRoot =>
		Environment.GetEnvironmentVariable("STRIDE_CHECKOUT_ROOT")
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "uno-tools", "stride");

	public StrideGameStudioIntegrationTests(OpenDevelopAppFixture app)
	{
		this.app = app;
		var gameDir = Path.Combine(StrideRoot, "samples", "Templates", "FirstPersonShooter", "FirstPersonShooter", "FirstPersonShooter.Game");
		gameProjectPath = Path.Combine(gameDir, "FirstPersonShooter.Game.csproj");
		gameScriptPath = Path.Combine(gameDir, "Player", "PlayerController.cs");
		addinProjectPath = Path.Combine(StrideRoot, "sources", "tools", "Stride.OpenDevelop.AddIn",
			"ICSharpCode.StrideGameStudio.csproj");
	}

	/// <summary>The deployed addin manifest, which is what makes the .sdpkg bindings exist at all.</summary>
	static string DeployedAddinManifest =>
		Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
			"AddIns", "DisplayBindings", "StrideGameStudio", "ICSharpCode.StrideGameStudio.addin");

	[Fact]
	public async Task StrideGame_OpenEditInspectRun()
	{
		if (!Directory.Exists(StrideRoot))
			Assert.Skip($"No Stride checkout at {StrideRoot}; set STRIDE_CHECKOUT_ROOT to run the Stride suite.");
		if (!File.Exists(Path.GetFullPath(DeployedAddinManifest)))
			Assert.Skip("The Stride addin is not deployed. Build sources/tools/Stride.OpenDevelop.AddIn from the Stride checkout first.");

		// ── 1. OPEN — load the Stride game project ────────────────────────────────────────────
		var opened = await app.ReopenSolutionAsync(gameProjectPath);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		// The .sdpkg-backed Assets subtree must appear under the game project in the Projects pad.
		var browser = await app.InvokeAsync("od.project-browser-state", "FirstPersonShooter.Game");
		Assert.True(browser.GetProperty("success").GetBoolean(), browser.ToString());
		var projectNode = browser.GetProperty("project");
		var assets = FindNode(projectNode, n => n.GetProperty("name").GetString() == "Assets");
		Assert.NotNull(assets);
		var assetsNode = assets!.Value;
		Assert.Equal("Folder", assetsNode.GetProperty("kind").GetString());

		// The virtual asset tree is derived from the .sdpkg AssetFolders (../Assets, Effects), so
		// the Assets subtree must contain at least one folder and one file somewhere (nesting is
		// package-dependent: the root "Assets" node typically wraps a folder of the same name that
		// holds the actual asset files). Assert recursively rather than on direct children.
		var allNodes = Flatten(assetsNode).ToList();
		Assert.Contains(allNodes, n => n.GetProperty("kind").GetString() == "Folder");
		Assert.Contains(allNodes, n => n.GetProperty("kind").GetString() == "File");
		Assert.True(allNodes.Any(n => n.GetProperty("kind").GetString() == "File"
			&& n.GetProperty("name").GetString()!.EndsWith(".sdscene", StringComparison.OrdinalIgnoreCase)),
			"The .sdpkg scene asset should surface as a File node: " + assetsNode);

		// ── 2. EDIT — open a game script for editing ─────────────────────────────────────────
		var script = await app.InvokeAsync("od.open-file", gameScriptPath);
		Assert.True(script.GetProperty("opened").GetBoolean(), script.ToString());
		var active = await WaitForActiveEditorAsync();
		Assert.True(active.GetProperty("active").GetBoolean(), active.ToString());
		Assert.True(active.GetProperty("isAvalonEdit").GetBoolean(),
			"The game script should open in the AvalonEdit text editor: " + active);
		Assert.True(active.GetProperty("textLength").GetInt32() > 0, "Script should have content: " + active);

		// ── 3. INSPECT — the tree/editor hold after the edit ─────────────────────────────────
		// Tree still intact (project + Assets subtree), and the script is the active view.
		var browserAgain = await app.InvokeAsync("od.project-browser-state", "FirstPersonShooter.Game");
		Assert.True(browserAgain.GetProperty("success").GetBoolean(), browserAgain.ToString());
		var assetsAgain = FindNode(browserAgain.GetProperty("project"), n => n.GetProperty("name").GetString() == "Assets");
		Assert.NotNull(assetsAgain);
		_ = assetsAgain!.Value;
		Assert.Equal("FirstPersonShooter.Game", browserAgain.GetProperty("project").GetProperty("name").GetString());

		var activeAgain = await app.InvokeAsync("od.active-view");
		Assert.Equal(gameScriptPath, activeAgain.GetProperty("fileName").GetString());

		// ── 4. RUN — build the game, then launch it ──────────────────────────────────────────
		// Building runs the Stride SDK asset pipeline (the kept CLI). od.build-solution is NOT
		// usable here yet: it builds only the current (single-project) solution and does not
		// transitively build the game's ProjectReference packs (mannequinModel, VFXPackage, ...),
		// so CSC fails on their missing ref assemblies - that is an open build-integration item
		// (see technote). Build the game csproj directly instead, which builds the packs
		// transitively and exercises the real Stride asset-pipeline build.
		var buildOutput = await BuildProjectAsync(gameProjectPath);
		Assert.True(buildOutput.exitCode == 0, "Stride game build failed:\n" + buildOutput.output);

		// The game project is a class library and cannot be started; the addin's launcher service
		// resolves or generates the startable "<Game>.Desktop" entry project, adds it to the
		// solution, and makes it the startup project. That happens automatically on solution open;
		// calling it explicitly makes the assertion independent of that hook's timing.
		var launcher = await app.InvokeAsync("od.stride.ensure-launcher");
		Assert.True(launcher.GetProperty("success").GetBoolean(), launcher.ToString());
		var launcherPath = launcher.GetProperty("launcherProjectPath").GetString();
		Assert.False(string.IsNullOrEmpty(launcherPath));
		Assert.True(File.Exists(launcherPath), "Launcher project was not created at " + launcherPath);
		Assert.True(launcher.GetProperty("setAsStartupProject").GetBoolean(),
			"The launcher was not made the startup project, so Run/Debug still has nothing to start.");

		// Building the launcher builds the game and its packs transitively and runs the Stride
		// asset pipeline for the launcher's own platform.
		var launcherBuild = await BuildProjectAsync(launcherPath!);
		Assert.True(launcherBuild.exitCode == 0, "Stride launcher build failed:\n" + launcherBuild.output);

		// Run through the IDE's own launch surface: od.run-project reuses IProject.CreateStartInfo
		// (the behavior chain the Debug > Start Without Debugging command uses) but keeps the
		// launched Process, so the smoke check can poll it and stop it again.
		var run = await app.InvokeAsync("od.run-project");
		Assert.True(run.GetProperty("success").GetBoolean(), run.ToString());
		Assert.True(run.GetProperty("processId").GetInt32() > 0, run.ToString());
		try
		{
			// The generated launcher sets GraphicsDeviceManager.SkipBackBufferClampToWindow, which
			// is what keeps this alive on macOS: without it the Vulkan presenter lets the Retina
			// CAMetalLayer extent feed back into the backbuffer size until a texture descriptor
			// exceeds Metal's 16384 cap and the game aborts within seconds (see technote). Staying
			// alive here is therefore a real regression guard on that line, not just a liveness poll.
			await Task.Delay(TimeSpan.FromSeconds(10));
			var status = await app.InvokeAsync("od.run-status");
			Assert.True(status.GetProperty("running").GetBoolean(),
				"The launched game exited instead of running: " + status);
		}
		finally
		{
			await app.InvokeAsync("od.stop-project");
		}
	}

	static async Task<(int exitCode, string output)> BuildProjectAsync(string projectPath)
	{
		var psi = new ProcessStartInfo("dotnet", "build \"" + projectPath + "\" -c Debug")
		{
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		using var process = Process.Start(psi);
		var stdout = await process!.StandardOutput.ReadToEndAsync();
		var stderr = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		return (process.ExitCode, stdout + stderr);
	}

	async Task<JsonElement> WaitForActiveEditorAsync()
	{
		for (var i = 0; i < 20; i++)
		{
			var active = await app.InvokeAsync("od.active-view");
			if (active.GetProperty("active").GetBoolean() && active.GetProperty("isAvalonEdit").GetBoolean())
				return active;
			await Task.Delay(TimeSpan.FromMilliseconds(500));
		}
		return await app.InvokeAsync("od.active-view");
	}

	static JsonElement? FindNode(JsonElement root, Func<JsonElement, bool> predicate)
	{
		if (predicate(root))
			return root;
		if (root.TryGetProperty("children", out var children))
		{
			foreach (var child in children.EnumerateArray())
			{
				var match = FindNode(child, predicate);
				if (match.HasValue)
					return match;
			}
		}
		return null;
	}

	static IEnumerable<JsonElement> Flatten(JsonElement root)
	{
		yield return root;
		if (root.TryGetProperty("children", out var children))
		{
			foreach (var child in children.EnumerateArray())
			{
				foreach (var descendant in Flatten(child))
					yield return descendant;
			}
		}
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	/// <summary>
	/// The Addin SDK's develop/run loop, exercised against the real Stride addin project now that
	/// it lives in the Stride repo: opening the ADDIN project (not the game) must produce a
	/// startable project, and starting it must bring up a second OpenDevelop that loads the addin.
	///
	/// This is the loop that makes an out-of-repo addin developable at all - without it the
	/// relocated addin would have no way to be run from the IDE that hosts it.
	///
	/// Runs under the real debugger and breaks inside the addin's own source. That is the strongest
	/// available evidence: the breakpoint is set before launch, in a module the debuggee has not
	/// loaded yet and which lives outside the debuggee's own directory, so hitting it proves the
	/// SDK-emitted start configuration launched the right host, the host loaded the addin from
	/// -addindir:, and the debugger rebound a pending breakpoint when that module arrived.
	/// </summary>
	[Fact]
	public async Task StrideAddInProject_IsStartable_AndDebuggingBreaksInsideTheAddIn()
	{
		if (!File.Exists(addinProjectPath))
			Assert.Skip($"No Stride addin project at {addinProjectPath}; set STRIDE_CHECKOUT_ROOT to run the Stride suite.");

		var opened = await app.ReopenSolutionAsync(addinProjectPath);
		Assert.True(opened.GetProperty("success").GetBoolean(), opened.ToString());

		// ── Addin SDK: the start configuration it emits ──────────────────────────────────────
		// StartAction=Program is what makes IsStartable ignore OutputType, so an addin (a class
		// library) becomes startable at all; the rest is what the child instance is told to do.
		var props = await app.InvokeAsync("od.project.properties", "ICSharpCode.StrideGameStudio",
			"StartAction,StartProgram,StartArguments,OpenDevelopAddin,OpenDevelopAddinKind");
		Assert.True(props.GetProperty("success").GetBoolean(), props.ToString());
		var values = props.GetProperty("properties");
		Assert.Equal("true", values.GetProperty("OpenDevelopAddin").GetString());
		Assert.Equal("InProcess", values.GetProperty("OpenDevelopAddinKind").GetString());
		Assert.Equal("Program", values.GetProperty("StartAction").GetString());

		var startProgram = values.GetProperty("StartProgram").GetString();
		Assert.False(string.IsNullOrEmpty(startProgram), "The Addin SDK emitted no StartProgram, so F5 would do nothing.");
		Assert.True(File.Exists(startProgram), $"StartProgram does not exist: {startProgram}");

		var startArguments = values.GetProperty("StartArguments").GetString() ?? string.Empty;
		Assert.Contains("-addindir:", startArguments);
		// Instance isolation: the child must not write into the developer's own settings/layout,
		// and must not fight this test's own app for the DevFlow port.
		Assert.Contains("-configdir:", startArguments);
		Assert.Contains("-devflow:off", startArguments);
		var configDir = ExtractQuotedArgument(startArguments, "-configdir:");
		Assert.False(string.IsNullOrEmpty(configDir), "No -configdir: value: the child would share the developer's profile.");

		// ── Debug the second instance, breaking inside the addin ────────────────────────────
		// The addin's autostart command runs while the child workbench initializes, so the
		// breakpoint is reached without driving the child's UI (which would be impossible anyway:
		// it runs with its DevFlow agent off so it cannot fight this test's app for the port).
		var addinSource = Path.Combine(Path.GetDirectoryName(addinProjectPath)!,
			"RegisterStrideProjectTreeContributorCommand.cs");
		Assert.True(File.Exists(addinSource), $"Missing addin source {addinSource}");
		var breakpointLine = FindLine(addinSource, "ProjectTreeContributorRegistry.Register(");

		await app.InvokeAsync("od.open-file", addinSource);
		await app.InvokeAsync("od.debug.clear-breakpoints");
		var breakpoint = await app.InvokeAsync("od.debug.set-breakpoint", addinSource, breakpointLine);
		Assert.True(breakpoint.GetProperty("success").GetBoolean(), breakpoint.ToString());

		try
		{
			// A second full IDE start under a suspended-attach debug session is slow.
			var start = await app.InvokeAsync("od.debug.start", addinProjectPath, true, 180);
			Assert.True(start.GetProperty("stopped").GetBoolean(),
				"Debugging the addin project never stopped at the breakpoint: " + start);
			Assert.EndsWith("RegisterStrideProjectTreeContributorCommand.cs",
				(start.GetProperty("currentFile").GetString() ?? string.Empty).Replace('\\', '/'));
			Assert.Equal(breakpointLine, start.GetProperty("currentLine").GetInt32());

			// The frame really is the addin's autostart command, not a same-named file elsewhere.
			var stack = await app.InvokeAsync("od.debug.call-stack");
			Assert.Contains(stack.EnumerateArray(),
				f => (f.GetProperty("Name").GetString() ?? string.Empty).Contains("Run"));

			// Instance isolation actually took effect on disk.
			Assert.True(Directory.Exists(configDir),
				$"The child did not use its isolated config directory {configDir}.");
		}
		finally
		{
			await app.InvokeAsync("od.debug.stop");
			await app.InvokeAsync("od.debug.clear-breakpoints");
		}
	}

	/// <summary>1-based line number of the first line containing <paramref name="marker"/>.</summary>
	static int FindLine(string file, string marker)
	{
		var lines = File.ReadAllLines(file);
		for (var i = 0; i < lines.Length; i++)
		{
			if (lines[i].Contains(marker, StringComparison.Ordinal))
				return i + 1;
		}
		throw new InvalidOperationException($"No line containing '{marker}' in {file}");
	}

	/// <summary>Value of a <c>-name:"value"</c> style start argument.</summary>
	static string ExtractQuotedArgument(string arguments, string name)
	{
		var index = arguments.IndexOf(name, StringComparison.Ordinal);
		if (index < 0)
			return string.Empty;
		var rest = arguments.Substring(index + name.Length);
		if (rest.StartsWith("\"", StringComparison.Ordinal))
		{
			var end = rest.IndexOf('"', 1);
			return end > 0 ? rest.Substring(1, end - 1) : string.Empty;
		}
		var space = rest.IndexOf(' ');
		return space > 0 ? rest.Substring(0, space) : rest;
	}

}