using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Core;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Headless dispatcher install: replaces Uno's native event loop (which on Skia
	/// desktop is AppKit/GTK/Win32) with a pump running on this thread. This is exactly
	/// what Uno's own Skia hosts do via CoreDispatcher.DispatchOverride, except those
	/// set it from inside the runtime assembly tree; a standalone shim has to reflect
	/// the internal setters instead (Windows.UI.Core.CoreDispatcher.skia.cs /
	/// Uno.UI.Dispatching.NativeDispatcher.skia.cs - a tiny, stable surface).
	/// </summary>
	internal static class HeadlessDispatcher
	{
		static readonly ConcurrentQueue<Action> Queue = new();
		static readonly ManualResetEventSlim Signal = new(false);
		static volatile bool exitRequested;

		/// <summary>Must be called before Application.Start.</summary>
		public static void Install()
		{
			var cdType = typeof(CoreDispatcher);
			var prioType = Type.GetType("Uno.UI.Dispatching.NativeDispatcherPriority, Uno.UI.Dispatching")
				?? throw new InvalidOperationException("Uno.UI.Dispatching assembly not loaded");
			var makeHandler = typeof(HeadlessDispatcher).GetMethod(nameof(MakeHandler), BindingFlags.Static | BindingFlags.NonPublic)!
				.MakeGenericMethod(prioType);
			var handler = makeHandler.Invoke(null, null)!;
			SetStatic(cdType, "DispatchOverride", handler);
			SetStatic(cdType, "HasThreadAccessOverride", new Func<bool>(() => true));
		}

		/// <summary>
		/// Runs the dispatcher pump until RequestExit. The caller thread becomes the
		/// Uno dispatcher thread; all UI work (and async continuations posted to the
		/// UI SynchronizationContext) executes here.
		/// </summary>
		public static void Run()
		{
			while (true)
			{
				if (Queue.TryDequeue(out var a))
				{
					a();
				}
				else if (!exitRequested)
				{
					Signal.Wait(25);
					Signal.Reset();
				}
				else
				{
					break;
				}
			}
		}

		public static void RequestExit() => exitRequested = true;

		/// <summary>Runs work on the dispatcher thread, waiting for completion.</summary>
		public static T Dispatch<T>(Func<T> action)
		{
			if (HasThreadAccess())
			{
				return action();
			}
			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			Queue.Enqueue(() =>
			{
				try
				{
					tcs.TrySetResult(action());
				}
				catch (Exception e)
				{
					tcs.TrySetException(e);
				}
			});
			Signal.Set();
			return tcs.Task.GetAwaiter().GetResult();
		}

		/// <summary>Runs async work on the dispatcher thread; continuations round-trip through the pump.</summary>
		public static Task<T> DispatchAsync<T>(Func<Task<T>> action)
		{
			if (HasThreadAccess())
			{
				return action();
			}
			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			Queue.Enqueue(() =>
			{
				_ = RunAsync(action, tcs);
			});
			Signal.Set();
			return tcs.Task;
		}

		static async Task RunAsync<T>(Func<Task<T>> action, TaskCompletionSource<T> tcs)
		{
			try
			{
				tcs.TrySetResult(await action());
			}
			catch (Exception e)
			{
				tcs.TrySetException(e);
			}
		}

		static bool HasThreadAccess()
		{
			// The pump runs on the Main thread; anything else must be marshaled.
			return Thread.CurrentThread.ManagedThreadId == mainThreadId;
		}

		static readonly int mainThreadId = Thread.CurrentThread.ManagedThreadId;

		static Action<Action, T> MakeHandler<T>()
		{
			return (a, _) =>
			{
				Queue.Enqueue(a);
				Signal.Set();
			};
		}

		static void SetStatic(Type type, string name, object value)
		{
			var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (f is null)
			{
				var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				p!.SetValue(null, value);
			}
			else
			{
				f.SetValue(null, value);
			}
		}
	}
}
