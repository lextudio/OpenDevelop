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
}