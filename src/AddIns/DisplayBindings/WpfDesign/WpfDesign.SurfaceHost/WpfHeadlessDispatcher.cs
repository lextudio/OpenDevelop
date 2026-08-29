using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ICSharpCode.WpfDesign.SurfaceHost;

/// <summary>
/// A dedicated STA thread running a WPF <see cref="Dispatcher"/> pump. All WPF layout,
/// rendering and hit-testing must happen on this thread; the RPC-handling thread marshals
/// onto it via <see cref="Dispatch{T}"/>/<see cref="DispatchAsync{T}"/> and blocks for the
/// result. Much simpler than Uno's headless dispatcher shim (HeadlessDispatcher.cs in the
/// Uno child): WPF's Dispatcher is public API, so no reflection into internal statics is
/// needed here.
/// </summary>
sealed class WpfHeadlessDispatcher
{
	readonly Thread thread;
	Dispatcher? dispatcher;

	public WpfHeadlessDispatcher(bool useCurrentThread = false)
	{
		if (useCurrentThread) {
			thread = Thread.CurrentThread;
			dispatcher = Dispatcher.CurrentDispatcher;
			#if !MICROSOFT_WPF
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				Dispatcher.NativeInputPump ??= () => { };
			#endif
			return;
		}
		using var ready = new ManualResetEventSlim(false);
		thread = new Thread(() => {
			dispatcher = Dispatcher.CurrentDispatcher;
			// Off Windows, Dispatcher.Run()'s top-level frame only keeps polling an empty
			// queue while Dispatcher.NativeInputPump is set (normally wired up by a real
			// native Window's run-loop tick) - with no window at all, the loop instead
			// breaks out on the very first empty check and Run() returns immediately,
			// leaving nothing left to ever pump a later Invoke. A no-op pump keeps the idle
			// loop alive via its own Thread.Sleep(1) polling path with no real window needed.
			#if !MICROSOFT_WPF
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				Dispatcher.NativeInputPump ??= () => { };
			#endif
			ready.Set();
			Dispatcher.Run();
		});
		// Thread.SetApartmentState throws PlatformNotSupportedException off Windows
		// (there is no real COM to marshal onto) - LibreWPF doesn't need STA there.
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			thread.SetApartmentState(ApartmentState.STA);
		thread.IsBackground = true;
		thread.Start();
		ready.Wait();
	}

	public Dispatcher Dispatcher => dispatcher!;
	public void Run() => Dispatcher.Run();

	/// <summary>Runs <paramref name="action"/> on the dispatcher thread and blocks for its result.</summary>
	public T Dispatch<T>(Func<T> action)
	{
		if (Thread.CurrentThread == thread)
			return action();
		return Dispatcher.Invoke(action);
	}

	/// <summary>Runs the async <paramref name="action"/> on the dispatcher thread and blocks for its result.</summary>
	public T DispatchAsync<T>(Func<Task<T>> action)
	{
		if (Thread.CurrentThread == thread)
			return action().GetAwaiter().GetResult();
		return Dispatcher.Invoke(() => action().GetAwaiter().GetResult());
	}

	/// <summary>Requests the dispatcher pump to stop, ending <see cref="Dispatcher.Run"/> on the
	/// STA thread and letting it exit.</summary>
	public void Shutdown()
	{
		if (dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false })
			dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
	}
}
