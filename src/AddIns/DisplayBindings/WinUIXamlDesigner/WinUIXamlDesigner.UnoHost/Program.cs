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
