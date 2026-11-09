// Copyright (c) 2026 LeXtudio. MIT-licensed (see repository root LICENSE).

#nullable enable

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ICSharpCode.PackageManagement
{
	/// <summary>
	/// Runs a package operation (an install, uninstall or update, including its <c>dotnet restore</c>) on a pool thread so
	/// the dialog stays alive, and lets only one run at a time: while it runs the dialog's commands report
	/// <see cref="IsBusy"/> through CanExecute. The work reaches the UI only through <see cref="PackageManagementEvents"/>
	/// (which marshals every event to the UI thread) and the project model calls of
	/// <see cref="SdkStylePackageReferenceService"/> (which marshal themselves).
	/// </summary>
	public static class PackageOperationGate
	{
		static int busy;

		public static bool IsBusy => System.Threading.Volatile.Read(ref busy) != 0;

		/// <summary>
		/// Starts <paramref name="work"/> on a pool thread. <paramref name="failed"/> and <paramref name="completed"/> run on
		/// the UI thread afterwards (failed only when the work threw). Returns null, running nothing, when an operation is
		/// already in progress.
		/// </summary>
		public static Task? Run(Action work, Action<Exception> failed, Action? completed = null)
		{
			if (System.Threading.Interlocked.CompareExchange(ref busy, 1, 0) != 0) {
				return null;
			}
			Raise();
			return Task.Run(() => {
				try {
					work();
				} catch (Exception ex) {
					UiThread.Invoke(() => failed(ex));
				} finally {
					System.Threading.Volatile.Write(ref busy, 0);
					UiThread.Invoke(() => {
						try {
							completed?.Invoke();
						} finally {
							Raise();
						}
					});
				}
			});
		}

		static void Raise()
		{
			UiThread.Post(CommandManager.InvalidateRequerySuggested);
		}
	}

	/// <summary>Moves calls to the UI thread when there is a dispatcher and the caller is not on it (otherwise just calls).</summary>
	static class UiThread
	{
		static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

		public static void Invoke(Action action)
		{
			Dispatcher? dispatcher = Dispatcher;
			if (dispatcher == null || dispatcher.CheckAccess()) {
				action();
			} else {
				dispatcher.Invoke(action);
			}
		}

		public static T Invoke<T>(Func<T> func)
		{
			T result = default!;
			Invoke((Action)(() => result = func())); // the Action overload: a bare lambda would bind to this method again
			return result;
		}

		public static void Post(Action action)
		{
			Dispatcher? dispatcher = Dispatcher;
			if (dispatcher == null) {
				action();
			} else {
				dispatcher.BeginInvoke(action);
			}
		}
	}
}
