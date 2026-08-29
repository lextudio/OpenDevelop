using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

// Deliberately in the UnoHost namespace: DesignHost.cs and DesignRpc.cs are source-linked from
// there unchanged, and they call HeadlessDispatcher statically. Supplying the same static surface
// here is what lets those ~1300 lines compile against Microsoft WinUI 3 with no edits at all.
namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// The Microsoft WinUI 3 counterpart of the Uno host's HeadlessDispatcher.
	///
	/// The Uno original has to install its own pump, because Uno's Skia desktop head would
	/// otherwise run an AppKit/GTK/Win32 loop it cannot borrow; it reflects into Uno internals to
	/// override CoreDispatcher. None of that applies here: Application.Start already runs a real
	/// DispatcherQueue on the UI thread, so this shim simply captures that queue and marshals onto
	/// it. The API surface DesignHost depends on - Dispatch, DispatchAsync, RequestExit - is
	/// identical, which is the whole point.
	/// </summary>
	internal static class HeadlessDispatcher
	{
		static DispatcherQueue? queue;
		static readonly ManualResetEventSlim exit = new(false);

		/// <summary>Captures the UI thread's queue. Must be called ON that thread (from
		/// Application.Start's callback) before any RPC can arrive.</summary>
		public static void Attach() => queue = DispatcherQueue.GetForCurrentThread()
			?? throw new InvalidOperationException("No DispatcherQueue on the current thread.");

		/// <summary>Blocks until <see cref="RequestExit"/>. Handed to DesignerChildHost as its
		/// wait-for-shutdown hook; unlike the Uno original this does not pump, because
		/// Application.Start is already pumping on the UI thread.</summary>
		public static void Run() => exit.Wait();

		public static void RequestExit() => exit.Set();

		/// <summary>Fire-and-forget marshal onto the UI thread. Used for shutdown, which runs on
		/// the DDP worker and must not block waiting for a pump it is about to stop.</summary>
		public static void Post(Action action) => queue?.TryEnqueue(() => action());

		public static T Dispatch<T>(Func<T> action)
		{
			var target = queue ?? throw new InvalidOperationException("Dispatcher is not attached.");
			if (target.HasThreadAccess) return action();

			var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!target.TryEnqueue(() => {
					try { completion.SetResult(action()); }
					catch (Exception e) { completion.SetException(e); }
				}))
				throw new InvalidOperationException("The WinUI dispatcher rejected the work item.");
			// Blocking is correct here: every caller is a StreamJsonRpc handler on a worker thread
			// whose RPC result is the return value, and the UI thread is never the one waiting.
			return completion.Task.GetAwaiter().GetResult();
		}

		public static Task<T> DispatchAsync<T>(Func<Task<T>> action)
		{
			var target = queue ?? throw new InvalidOperationException("Dispatcher is not attached.");
			var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			if (!target.TryEnqueue(async () => {
					try { completion.SetResult(await action().ConfigureAwait(true)); }
					catch (Exception e) { completion.SetException(e); }
				}))
				throw new InvalidOperationException("The WinUI dispatcher rejected the work item.");
			return completion.Task;
		}
	}
}
