using System;
using System.Threading.Tasks;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	/// <summary>
	/// Where the design host runs UI work. The desktop head has no window and pumps its own queue
	/// (<see cref="HeadlessDispatcher"/>); a mobile head has a real window and marshals to that
	/// window's dispatcher queue. <see cref="DesignHost"/> only ever talks to this interface, so one
	/// host source serves every head.
	/// </summary>
	internal interface IHostDispatcher
	{
		/// <summary>Runs <paramref name="action"/> on the UI thread and returns its result.</summary>
		T Dispatch<T>(Func<T> action);

		/// <summary>Runs async work on the UI thread; its continuations stay on the UI thread.</summary>
		Task<T> DispatchAsync<T>(Func<Task<T>> action);

		/// <summary>Asks the host's UI loop to end (the parent asked for shutdown).</summary>
		void RequestExit();
	}

	/// <summary>The dispatcher the design host uses. Set once at start-up by the head's entry point;
	/// defaults to the headless pump so the desktop head needs no wiring.</summary>
	internal static class HostDispatcher
	{
#if !__IOS__ && !__ANDROID__
		static IHostDispatcher current = new HeadlessHostDispatcher();
#else
		static IHostDispatcher? current;
#endif

		public static IHostDispatcher Current
		{
			get => current ?? throw new InvalidOperationException("The host dispatcher was not installed by the head's entry point.");
			set => current = value;
		}

		public static T Dispatch<T>(Func<T> action) => Current.Dispatch(action);

		public static Task<T> DispatchAsync<T>(Func<Task<T>> action) => Current.DispatchAsync(action);

		public static void RequestExit() => Current.RequestExit();
	}

#if !__IOS__ && !__ANDROID__
	internal sealed class HeadlessHostDispatcher : IHostDispatcher
	{
		public T Dispatch<T>(Func<T> action) => HeadlessDispatcher.Dispatch(action);

		public Task<T> DispatchAsync<T>(Func<Task<T>> action) => HeadlessDispatcher.DispatchAsync(action);

		public void RequestExit() => HeadlessDispatcher.RequestExit();
	}
#endif
}
