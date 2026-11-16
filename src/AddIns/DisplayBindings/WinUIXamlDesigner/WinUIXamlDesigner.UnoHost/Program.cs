#if !__IOS__ && !__ANDROID__
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamJsonRpc;
using Uno.UI.Hosting;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	class Program
	{
		static int Main(string[] args)
		{
			HostBootstrap.InstallOwnDependencyResolver();
			return Run(args);
		}


		static int Run(string[] args)
		{
			var appBin = HostBootstrap.ParseArgument(args, "--appbin");
			HostBootstrap.PreloadProjectAssemblies(appBin);
			HostApp.LaunchArgs = args;
			// Uno 7 needs an explicit platform host. The desktop head ships the Skia hosts for all three desktop OSes, so pick the one for
			// the OS this child runs on; naming only macOS made the child exit at start-up everywhere else ("No platform host could be
			// selected") and the Uno designer never rendered on Windows or Linux.
			var builder = UnoPlatformHostBuilder.Create().App(() => new HostApp());
			if (OperatingSystem.IsWindows())
				builder = builder.UseWin32();
			else if (OperatingSystem.IsMacOS())
				builder = builder.UseMacOS();
			else
				builder = builder.UseX11();
			var host = builder.Build();
			host.Run();
			return 0;
		}

	}

	/// <summary>
	/// The Win32 platform host shows the activated window as a real top-level window, which on Windows sat on the user's screen (and over
	/// the IDE, so pointer tests never reached it). Activation is what makes the tree live, so the window is not hidden: it is moved far
	/// offscreen without activating it, as the Microsoft host does. The native window appears on the UI thread a moment after
	/// <c>Activate()</c>, so this looks for this process's visible top-level windows for a few seconds and moves each one once.
	/// Native-live mode positions the window itself afterwards and is not affected (the search has ended by then).
	/// </summary>
	static class OffscreenWindow
	{
		delegate bool EnumWindowsProc(nint hwnd, nint lParam);

		[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
		[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
		[System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
		[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

		const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

		public static void MoveAway()
		{
			var thread = new Thread(() => {
				var self = (uint)Environment.ProcessId;
				var moved = new System.Collections.Generic.HashSet<nint>();
				for (var attempt = 0; attempt < 100; attempt++) {
					EnumWindows((hwnd, _) => {
						if (IsWindowVisible(hwnd) && GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == self && moved.Add(hwnd))
							SetWindowPos(hwnd, 0, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
						return true;
					}, 0);
					if (moved.Count > 0 && attempt > 10) break;
					Thread.Sleep(100);
				}
			}) { IsBackground = true, Name = "design-host-offscreen" };
			thread.Start();
		}
	}

	class HostApp : Application
	{
		public static string[] LaunchArgs { get; set; } = Array.Empty<string>();
		static readonly ManualResetEventSlim Shutdown = new(false);
		Window? window;
		Grid? stage;

		public HostApp()
		{
			Resources.MergedDictionaries.Add(new XamlControlsResources());
		}

		protected override void OnLaunched(LaunchActivatedEventArgs args)
		{
			stage = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
			window = new Window { Content = stage };
			window.Activate();
			if (OperatingSystem.IsWindows())
				OffscreenWindow.MoveAway();
			HostDispatcher.Current = new DesktopDispatcher(window.DispatcherQueue, () => Shutdown.Set());
			InstallHooks();

			var thread = new Thread(() => {
				var code = DesignerChildHost.Run(LaunchArgs, "UnoDesignHost", DesignRpc.RegisterRpcMethods,
					waitForShutdown: () => Shutdown.Wait(), onParentDisconnected: () => Shutdown.Set(), afterShutdown: null);
				Environment.Exit(code);
			}) { IsBackground = true, Name = "design-host-rpc" };
			thread.Start();
		}

		void InstallHooks()
		{
			DesignHost.HostVisualRoot = (previous, next) => {
				if (stage == null) return;
				if (previous != null) stage.Children.Remove(previous);
				if (next != null && !stage.Children.Contains(next)) stage.Children.Add(next);
			};
			DesignHost.HostCommitLayout = element => {
				if (stage == null) return;
				var size = new Windows.Foundation.Size(element.RenderSize.Width, element.RenderSize.Height);
				stage.Measure(size);
				stage.Arrange(new Windows.Foundation.Rect(0, 0, size.Width, size.Height));
				stage.UpdateLayout();
				element.UpdateLayout();
			};
			DesignHost.HostAwaitFrame = () => Task.Delay(50);
		}
	}

	sealed class DesktopDispatcher : IHostDispatcher
	{
		readonly DispatcherQueue queue;
		readonly Action requestExit;
		public DesktopDispatcher(DispatcherQueue queue, Action requestExit) { this.queue = queue; this.requestExit = requestExit; }
		public T Dispatch<T>(Func<T> action)
		{
			if (queue.HasThreadAccess) return action();
			var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!queue.TryEnqueue(() => { try { result.TrySetResult(action()); } catch (Exception e) { result.TrySetException(e); } }))
				throw new InvalidOperationException("The desktop host dispatcher is not accepting work.");
			return result.Task.GetAwaiter().GetResult();
		}
		public Task<T> DispatchAsync<T>(Func<Task<T>> action)
		{
			if (queue.HasThreadAccess) return action();
			var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!queue.TryEnqueue(async () => { try { result.TrySetResult(await action()); } catch (Exception e) { result.TrySetException(e); } }))
				result.TrySetException(new InvalidOperationException("The desktop host dispatcher is not accepting work."));
			return result.Task;
		}
		public void RequestExit() => requestExit();
	}
}
#endif
