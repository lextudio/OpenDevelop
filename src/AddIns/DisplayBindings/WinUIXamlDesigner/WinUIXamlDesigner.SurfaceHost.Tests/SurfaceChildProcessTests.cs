using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ICSharpCode.SharpDevelop.Designer.Remote;
using StreamJsonRpc;
using Xunit;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Tests;

/// <summary>
/// Runs the real child executable and talks to it as the IDE does: listen on loopback, launch it with
/// <c>--port</c>/<c>--token</c>, speak DDP over header-delimited JSON-RPC. Everything except the render test needs no
/// Surface; the render test is skipped unless OD_SURFACE_EXE (and optionally OD_SURFACE_ARGS) name a built one.
/// </summary>
public class SurfaceChildProcessTests
{
	const string Token = "00112233445566778899aabbccddeeff";
	static CancellationToken Ct => TestContext.Current.CancellationToken;

	/// <summary>The child's own build output, next to this project's (same configuration and target framework).</summary>
	internal static string ChildDll()
	{
		var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
		var config = tfm.Parent!;
		var projectDirectory = config.Parent!.Parent!;   // .../WinUIXamlDesigner.SurfaceHost.Tests (bin/<config>/<tfm> is below it)
		string dll = Path.Combine(projectDirectory.Parent!.FullName,"WinUIXamlDesigner.SurfaceHost.Child", "bin", config.Name, tfm.Name,
			"WinUIXamlDesigner.SurfaceHost.Child.dll");
		Assert.True(File.Exists(dll), "The child was not built: " + dll);
		return dll;
	}

	sealed class Child : IAsyncDisposable
	{
		readonly Process process;
		readonly TcpClient client;
		public JsonRpc Rpc { get; }
		public string Stderr => stderr.ToString();
		readonly System.Text.StringBuilder stderr = new();

		Child(Process process, TcpClient client, JsonRpc rpc)
		{
			this.process = process;
			this.client = client;
			Rpc = rpc;
		}

		public static Task<Child> StartAsync(string surfaceExe, string surfaceArgs)
			=> StartWithAsync("--surface-exe", surfaceExe, "--surface-args", surfaceArgs);

		public static async Task<Child> StartWithAsync(params string[] childArguments)
		{
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			try {
				int port = ((IPEndPoint)listener.LocalEndpoint).Port;
				var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
				foreach (var arg in new[] { ChildDll(), "--port", port.ToString(), "--token", Token }.Concat(childArguments))
					psi.ArgumentList.Add(arg);
				var process = Process.Start(psi)!;
				var sb = new System.Text.StringBuilder();
				process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
				process.BeginErrorReadLine();
				var client = await listener.AcceptTcpClientAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
				var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(client.GetStream(), client.GetStream(), DesignerJsonRpc.CreateFormatter()));
				rpc.StartListening();
				var child = new Child(process, client, rpc);
				child.stderr.Append(sb);
				process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (child.stderr) child.stderr.AppendLine(e.Data); };
				return child;
			} finally {
				listener.Stop();
			}
		}

		public async Task<T> Call<T>(string method, params object?[] args)
		{
			try {
				return await Rpc.InvokeWithCancellationAsync<T>(method, args, Ct);
			} catch (ConnectionLostException e) {
				// The child's own stderr is the only place the cause of a dropped connection is recorded.
				throw new InvalidOperationException($"{method}: {e.Message}\n--- child stderr ---\n{Stderr}", e);
			}
		}

		public async Task<int> ShutdownAsync()
		{
			try {
				await Rpc.InvokeWithCancellationAsync("shutdown", Array.Empty<object>(), Ct);
			} catch (ConnectionLostException) {
				// The child may close the transport as it exits; what this checks is that it does exit, with code 0.
			}
			await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
			return process.ExitCode;
		}

		public async ValueTask DisposeAsync()
		{
			Rpc.Dispose();
			client.Dispose();
			if (!process.HasExited)
				process.Kill(true);
			process.Dispose();
			await Task.CompletedTask;
		}
	}

	static DesignerDocumentRequest Request(string xaml, long version)
	{
		var snapshot = new DesignerDocumentSnapshot { SessionId = "s1", DocumentId = "d1", Version = version, PrimaryFileName = "MainPage.xaml" };
		snapshot.Files.Add(new DesignerSourceFileSnapshot { FileName = "MainPage.xaml", Kind = "Source", Text = xaml });
		return new DesignerDocumentRequest { Snapshot = snapshot, Viewport = new DesignerViewport { Width = 480, Height = 320, Dpi = 1.0 } };
	}

	[Fact]
	public async Task Initialize_authenticates_and_reports_the_surface_runtime()
	{
		await using var child = await Child.StartAsync(@"C:\odsurf\missing\Surface.exe", "");

		await Assert.ThrowsAsync<RemoteInvocationException>(
			() => child.Call<HostHandshake>("initialize", "ffeeddccbbaa99887766554433221100", DesignerProtocol.Version, "s1"));
		await Assert.ThrowsAsync<RemoteInvocationException>(
			() => child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version + 1, "s1"));

		var handshake = await child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version, "s1");
		Assert.Equal("WinUI.Surface", handshake.Runtime);
		Assert.Equal("s1", handshake.SessionId);
		Assert.Equal("WinUI.Surface", handshake.Capabilities.Runtime);
		await child.Call<object?>("ping");

		Assert.Equal(0, await child.ShutdownAsync());
	}

	[Fact]
	public async Task A_surface_that_cannot_start_is_reported_as_an_error_not_a_hang()
	{
		await using var child = await Child.StartAsync(@"C:\odsurf\missing\Surface.exe", "");
		await child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version, "s1");

		await Assert.ThrowsAsync<RemoteInvocationException>(() => child.Call<DesignerSessionState>("session/open", Request("<Page/>", 1)));
		// The child survives the failed launch.
		await child.Call<object?>("ping");
	}

	/// <summary>
	/// The IDE's mode: a pristine matched-host directory (a provisioner cache entry, OD_SURFACE_HOST_DIR) and a short run
	/// root. Every document gets its own run copy, which is gone again after session/close.
	/// </summary>
	[Fact]
	public async Task Matched_host_directory_gets_a_private_run_copy_per_document_removed_on_close()
	{
		string? hostDir = Environment.GetEnvironmentVariable("OD_SURFACE_HOST_DIR");
		if (string.IsNullOrEmpty(hostDir) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_HOST_DIR is not set (or not Windows).");
		string runRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "odsurf-ddp-" + Guid.NewGuid().ToString("N")[..6]);
		Directory.CreateDirectory(runRoot);
		try {
			await using var child = await Child.StartWithAsync("--host-dir", hostDir, "--run-root", runRoot);
			await child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version, "s1");
			const string xaml = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Button Content=\"hi\"/></Page>";

			var first = await child.Call<DesignerSessionState>("session/open", Request(xaml, 1));
			Assert.True(first.Accepted, first.Error + "\n" + child.Stderr);
			// The child keeps its copies under its own process id.
			string childRoot = Assert.Single(Directory.GetDirectories(runRoot));
			var runs = Directory.GetDirectories(childRoot);
			Assert.Single(runs);
			// The pristine entry is untouched: only the copy carries a launched surface's mutated PRI.
			Assert.True(File.Exists(Path.Combine(runs[0], "Surface.exe")));

			// A second document gets a second, separate copy.
			var secondRequest = Request(xaml, 1);
			secondRequest.Snapshot.DocumentId = "d2";
			Assert.True((await child.Call<DesignerSessionState>("session/open", secondRequest)).Accepted);
			Assert.Equal(2, Directory.GetDirectories(childRoot).Length);

			await child.Call<object?>("session/close", "s1", "d1");
			await child.Call<object?>("session/close", "s1", "d2");
			Assert.Empty(Directory.GetDirectories(childRoot));
			Assert.Equal(0, await child.ShutdownAsync());
			Assert.Empty(Directory.GetDirectories(runRoot));   // the child removed its own folder on shutdown
		} finally {
			TestFiles.TryDelete(runRoot);
		}
	}

	/// <summary>
	/// A force-killed IDE cannot close its documents, so its children's run copies are left behind. The next child sweeps the
	/// per-process folders of dead processes and must leave a live process's folder alone.
	/// </summary>
	[Fact]
	public async Task A_new_child_sweeps_run_folders_of_dead_processes_and_keeps_live_ones()
	{
		string runRoot = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "odsurf-sweep-" + Guid.NewGuid().ToString("N")[..6]);
		string dead = Path.Combine(runRoot, "2147480000");                                   // no such process
		string live = Path.Combine(runRoot, Environment.ProcessId.ToString());                // this test process
		string unrelated = Path.Combine(runRoot, "not-a-process-id");
		foreach (string directory in new[] { dead, live, unrelated }) {
			Directory.CreateDirectory(Path.Combine(directory, "run-abc"));
			File.WriteAllText(Path.Combine(directory, "run-abc", "Surface.exe"), "x");
		}
		try {
			await using var child = await Child.StartWithAsync("--host-dir", @"C:\odsurf-missing", "--run-root", runRoot);
			await child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version, "s1");

			Assert.False(Directory.Exists(dead), "the dead process's folder should have been swept");
			Assert.True(Directory.Exists(live), "a live process's folder must be kept");
			Assert.True(Directory.Exists(unrelated), "a folder that is not a process id must be kept");
			Assert.Equal(0, await child.ShutdownAsync());
		} finally {
			TestFiles.TryDelete(runRoot);
		}
	}

	[Fact]
	public async Task Host_directory_without_a_run_root_is_refused_at_startup()
	{
		var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
		foreach (var arg in new[] { ChildDll(), "--port", "1", "--token", Token, "--host-dir", @"C:\x" })
			psi.ArgumentList.Add(arg);
		using var process = Process.Start(psi)!;
		await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
		Assert.Equal(2, process.ExitCode);
		Assert.Contains("--run-root", await process.StandardError.ReadToEndAsync(Ct));
	}

	/// <summary>The message the user sees when the Windows App Runtime is missing must survive the RPC from the child.</summary>
	[Fact]
	public async Task A_missing_runtime_is_reported_to_the_ide_side_with_the_install_hint()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Needs cmd.exe.");
		string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
		await using var child = await Child.StartWithAsync("--surface-exe", cmd, "--surface-args", "/c exit " + SurfaceLaunch.RuntimeNotInstalledExitCode);
		await child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version, "s1");

		var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => child.Call<DesignerSessionState>("session/open", Request("<Page/>", 1)));

		Assert.Contains("Windows App Runtime is not installed", ex.Message);
		await child.Call<object?>("ping");   // the child survives and can be asked again after the user installs the runtime
	}

	/// <summary>A canvas click answered by the real surface's own PickAt, through the child's design/hit-test.</summary>
	[Fact]
	public async Task Real_surface_hit_tests_a_named_element_over_ddp()
	{
		string? exe = Environment.GetEnvironmentVariable("OD_SURFACE_EXE");
		if (string.IsNullOrEmpty(exe) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_EXE is not set (or not Windows).");
		await using var child = await Child.StartAsync(exe, Environment.GetEnvironmentVariable("OD_SURFACE_ARGS") ?? "");
		await child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version, "s1");
		const string xaml = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">"
			+ "<StackPanel><Button x:Name=\"Go\" Content=\"Click me\"/><TextBlock Text=\"unnamed text\"/></StackPanel></Page>";
		var opened = await child.Call<DesignerSessionState>("session/open", Request(xaml, 1));
		Assert.True(opened.Accepted, opened.Error + "\n" + child.Stderr);

		// The element tree the WinUI canvas works from: assembled from the document's structure and the surface's own selections.
		var tree = opened.Tree;
		Assert.NotNull(tree);
		Assert.Equal("Page", tree.Type);
		var stack = Assert.Single(tree.Children);
		Assert.Equal("StackPanel", stack.Type);
		Assert.Equal(new[] { "Button", "TextBlock" }, stack.Children.Select(c => c.Type).ToArray());
		var go = stack.Children[0];
		Assert.Equal(("Go", "0,0"), (go.Name, go.Path));
		Assert.True(go.Width > 40 && go.Height > 20, $"the button should have real bounds, got {go.Width}x{go.Height}");
		Assert.True(stack.Children[1].Y >= go.Y + go.Height - 1, "the text block sits below the button");

		var clock = System.Diagnostics.Stopwatch.StartNew();
		var onButton = await child.Call<DesignerHitTestResult>("design/hit-test", "s1", "d1", 1L, 20.0, 15.0);
		long firstHitMs = clock.ElapsedMilliseconds;   // this one also pays for the surface's first native window
		clock.Restart();
		var onButtonAgain = await child.Call<DesignerHitTestResult>("design/hit-test", "s1", "d1", 1L, 25.0, 18.0);
		long hitMs = clock.ElapsedMilliseconds;
		clock.Restart();
		var onEmpty = await child.Call<DesignerHitTestResult>("design/hit-test", "s1", "d1", 1L, 900.0, 600.0);
		long missMs = clock.ElapsedMilliseconds;
		var onUnnamed = await child.Call<DesignerHitTestResult>("design/hit-test", "s1", "d1", 1L, 20.0, 45.0);
		TestContext.Current.SendDiagnosticMessage($"hit-test latency (host natively, pick, leave): first hit {firstHitMs} ms, repeat hit {hitMs} ms, miss {missMs} ms");

		Assert.True(onButton.Hit, "a click on the named button should hit it; child stderr:\n" + child.Stderr);
		Assert.Equal("Go", onButton.ComponentName);
		Assert.Equal("Button", onButton.ComponentType);
		Assert.False(onEmpty.Hit);
		Assert.False(onUnnamed.Hit);   // an unnamed element is not offered to the shell
		Assert.True(onButtonAgain.Hit && onButtonAgain.ComponentName == "Go");
		Assert.True(firstHitMs < 3000 && hitMs < 3000, $"a click should stay interactive; first {firstHitMs} ms, repeat {hitMs} ms");
		// A miss neither broke the connection nor the next render.
		var again = await child.Call<DesignerSessionState>("session/update", Request(xaml, 2));
		Assert.True(again.Accepted, again.Error);
	}

	[Fact]
	public async Task Real_surface_renders_refuses_edits_and_exports_over_ddp()
	{
		string? exe = Environment.GetEnvironmentVariable("OD_SURFACE_EXE");
		if (string.IsNullOrEmpty(exe) || !OperatingSystem.IsWindows())
			Assert.Skip("OD_SURFACE_EXE is not set (or not Windows).");

		await using var child = await Child.StartAsync(exe, Environment.GetEnvironmentVariable("OD_SURFACE_ARGS") ?? "");
		await child.Call<HostHandshake>("initialize", Token, DesignerProtocol.Version, "s1");

		const string xaml = "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Button Content=\"hi\"/></Page>";
		var opened = await child.Call<DesignerSessionState>("session/open", Request(xaml, 1));
		Assert.True(opened.Accepted, opened.Error + "\n" + child.Stderr);
		Assert.Equal("Page", opened.RootType);
		Assert.StartsWith("iVBOR", opened.Render!.PngBase64);   // base64 of the PNG signature

		var updated = await child.Call<DesignerSessionState>("session/update", Request(xaml.Replace("hi", "changed"), 2));
		Assert.True(updated.Accepted, updated.Error);
		Assert.Equal(2, updated.Render!.Sequence);

		var refused = await child.Call<DesignerSessionState>("design/set-property", "s1", "d1", 2L, "Button", "Content", "x");
		Assert.False(refused.Accepted);
		Assert.Equal(2L, refused.Version);

		// A theme change and a forced visual state are refused as ordinary results, with the measured reason, not pretended.
		var theme = await child.Call<DesignerSessionState>("design/theme", "s1", "d1", 2L, "Dark");
		Assert.False(theme.Accepted);
		Assert.Contains("design/theme", theme.Error);
		Assert.Contains("Light theme", theme.Error);
		var state = await child.Call<DesignerSessionState>("design/go-to-state", "s1", "d1", 2L, "CommonStates", "Pressed");
		Assert.False(state.Accepted);
		Assert.Contains("visual state", state.Error);

		string png = Path.Combine(Path.GetTempPath(), "od-ddp-" + Guid.NewGuid().ToString("N") + ".png");
		try {
			await child.Call<string>("design/export-png", "s1", "d1", png);
			Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(png)[..4]);
		} finally {
			File.Delete(png);
		}

		var broken = await child.Call<DesignerSessionState>("session/update", Request("<Page", 3));
		Assert.False(broken.Accepted);
		Assert.NotEmpty(broken.Diagnostics);

		await child.Call<object?>("session/close", "s1", "d1");
		Assert.Equal(0, await child.ShutdownAsync());
	}
}
