// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Tracks overlapping runs of one asynchronous load, so a view can show a single busy indicator and keep only the
	/// newest result.
	///
	/// A view that reloads when a setting changes can start a second load before the first finishes (sdkmanager, for
	/// example, runs for tens of seconds). Two things must then hold: an older run must not clear the busy state while a
	/// newer one is still running, and an older run must not publish its now obsolete result.
	///
	/// <code>
	/// var run = tracker.Begin();
	/// try {
	///     var result = await LoadAsync();
	///     if (tracker.IsCurrent(run)) Publish(result);
	/// } finally {
	///     if (tracker.End(run)) busy.IsBusy = false;   // only when nothing is in flight any more
	/// }
	/// </code>
	/// Not thread-safe: use it from the owning view's thread.
	/// </summary>
	public sealed class LoadTracker
	{
		int generation;
		readonly HashSet<int> inFlight = new HashSet<int>();

		/// <summary>Starts a run. The returned token is passed to <see cref="IsCurrent"/> and <see cref="End"/>.</summary>
		public int Begin()
		{
			int run = ++generation;
			inFlight.Add(run);
			return run;
		}

		/// <summary>True while <paramref name="token"/> is the newest run started by this tracker.</summary>
		public bool IsCurrent(int token)
		{
			return token == generation;
		}

		/// <summary>
		/// Ends a run. Returns true when that was the last run still in flight, which is when the busy state can be cleared.
		/// The token identifies the run: ending a token that already ended (or was never issued) changes nothing and returns
		/// false, so a stale run cannot clear a newer run's busy state.
		/// </summary>
		public bool End(int token)
		{
			if (!inFlight.Remove(token))
				return false;
			return inFlight.Count == 0;
		}

		/// <summary>True while at least one run has begun and not ended.</summary>
		public bool IsBusy {
			get { return inFlight.Count > 0; }
		}
	}
}
