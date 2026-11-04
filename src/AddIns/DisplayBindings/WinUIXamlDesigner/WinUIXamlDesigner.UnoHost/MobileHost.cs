#if __IOS__ || __ANDROID__
using System;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// The mobile (iOS/Android) head. Unlike the desktop head it cannot be headless: Uno on a device
	/// needs a real window, so the design root is parked inside one and rendered with
	/// <c>RenderTargetBitmap</c> from there. The process is started by the IDE inside a simulator or
	/// emulator with <c>--port N --token T</c> and calls back over loopback (see
	/// <c>IosSimulatorHostProcess</c> / <c>AndroidEmulatorHostProcess</c> in Designer.Remote).
	/// </summary>
	public partial class App : Application
	{
		/// <summary>The launch arguments, as the platform entry point received them.</summary>
		public static string[] LaunchArgs { get; set; } = Array.Empty<string>();

		static readonly ManualResetEventSlim Exit = new(false);
		Window? window;
		Grid? stage;

		public App()
		{
			// Platforms/<head>/App.xaml merges the Fluent control resources, like the desktop head does in code.
			InitializeComponent();
		}

		protected override void OnLaunched(LaunchActivatedEventArgs args)
		{
			stage = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
			window = new Window { Content = stage };
			window.Activate();
			var dispatcher = new MobileDispatcher(window.DispatcherQueue, () => Exit.Set());
			HostDispatcher.Current = dispatcher;
			InstallHooks();

			// The RPC loop blocks, so it must not run on the UI thread: StreamJsonRpc calls arrive on
			// pool threads and every UI touch goes through HostDispatcher.
			var thread = new Thread(() =>
			{
				var code = DesignerChildHost.Run(LaunchArgs, "UnoDesignHost", DesignRpc.RegisterRpcMethods,
					waitForShutdown: () => Exit.Wait(),
					onParentDisconnected: () => Exit.Set(),
					afterShutdown: null);
				Environment.Exit(code);
			}) { IsBackground = true, Name = "design-host-rpc" };
			thread.Start();
		}

		/// <summary>The hooks <see cref="DesignHost"/> offers to a host that needs the root inside a
		/// window: park it in the stage grid, finish layout, and wait a frame so the first capture
		/// is not blank.</summary>
		void InstallHooks()
		{
			DesignHost.HostVisualRoot = (previous, next) =>
			{
				if (stage == null)
					return;
				if (previous != null)
					stage.Children.Remove(previous);
				if (next != null && !stage.Children.Contains(next))
					stage.Children.Add(next);
			};
			DesignHost.HostCommitLayout = element =>
			{
				stage?.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
				element.UpdateLayout();
			};
			DesignHost.HostAwaitFrame = () => Task.Delay(50);
		}
	}

	internal sealed class MobileDispatcher : IHostDispatcher
	{
		readonly DispatcherQueue queue;
		readonly Action requestExit;

		public MobileDispatcher(DispatcherQueue queue, Action requestExit)
		{
			this.queue = queue;
			this.requestExit = requestExit;
		}

		public T Dispatch<T>(Func<T> action)
		{
			if (queue.HasThreadAccess)
				return action();
			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!queue.TryEnqueue(() =>
			{
				try { tcs.TrySetResult(action()); }
				catch (Exception e) { tcs.TrySetException(e); }
			}))
				throw new InvalidOperationException("The window's dispatcher queue is not accepting work.");
			return tcs.Task.GetAwaiter().GetResult();
		}

		public Task<T> DispatchAsync<T>(Func<Task<T>> action)
		{
			if (queue.HasThreadAccess)
				return action();
			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!queue.TryEnqueue(async () =>
			{
				try { tcs.TrySetResult(await action()); }
				catch (Exception e) { tcs.TrySetException(e); }
			}))
				tcs.TrySetException(new InvalidOperationException("The window's dispatcher queue is not accepting work."));
			return tcs.Task;
		}

		public void RequestExit() => requestExit();
	}
}
#endif
