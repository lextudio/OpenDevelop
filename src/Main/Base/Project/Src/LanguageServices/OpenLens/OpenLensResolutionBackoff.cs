using System;
using System.Collections.Generic;

namespace ICSharpCode.SharpDevelop.LanguageServices.OpenLens
{
	/// <summary>
	/// When an OpenLens item may be resolved again after its provider returned it unresolved.
	///
	/// A provider leaves an item unresolved when it cannot answer yet (the language service has not
	/// loaded the document's project, see <see cref="LanguageOpenLensProvider"/>), and nothing tells
	/// the editor when that changes. The renderer used to retry by redrawing the text view, whose
	/// VisualLinesChanged started the next resolution at once: a document whose language service never
	/// became ready spun the UI thread at ~300 resolutions a second and hung the IDE. Retrying on an
	/// exponential delay keeps the eventual resolution without the spin.
	///
	/// Not thread-safe: the renderer uses it from its editor dispatcher only.
	/// </summary>
	public sealed class OpenLensResolutionBackoff
	{
		public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
		public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(10);

		readonly Dictionary<(string AnchorId, string LensId), (int Attempts, DateTime NotBefore)> pending = new();

		/// <summary>
		/// Records that resolving the item left it unresolved, and returns how long to wait before
		/// trying again: <see cref="InitialDelay"/>, doubled per consecutive miss, capped at
		/// <see cref="MaximumDelay"/>.
		/// </summary>
		public TimeSpan RecordUnresolved(string anchorId, string lensId, DateTime now)
		{
			var key = (anchorId, lensId);
			int attempts = pending.TryGetValue(key, out var entry) ? entry.Attempts + 1 : 1;
			var delay = GetDelay(attempts);
			pending[key] = (attempts, now + delay);
			return delay;
		}

		/// <summary>Forgets the item's misses once it resolved (or its cache entry was dropped).</summary>
		public void RecordResolved(string anchorId, string lensId)
		{
			pending.Remove((anchorId, lensId));
		}

		/// <summary>
		/// Forgets every miss: a provider refresh or an edit can make every item answerable again, so
		/// the next viewport pass should try them all at once.
		/// </summary>
		public void Reset()
		{
			pending.Clear();
		}

		/// <summary>True unless the item is still inside the delay of its last unresolved attempt.</summary>
		public bool CanResolve(string anchorId, string lensId, DateTime now)
		{
			return !pending.TryGetValue((anchorId, lensId), out var entry) || now >= entry.NotBefore;
		}

		/// <summary>
		/// The earliest time after <paramref name="now"/> at which a waiting item becomes resolvable
		/// again, so one timer can serve every waiting item: after each pass the renderer re-arms for
		/// this. Only future times count. An item whose delay already passed but that was not
		/// resolved was not visible; re-arming for it would fire at once and spin, and the viewport
		/// pass picks it up when it scrolls into view.
		/// </summary>
		public bool TryGetNextRetry(DateTime now, out DateTime due)
		{
			due = DateTime.MaxValue;
			foreach (var entry in pending.Values) {
				if (entry.NotBefore > now && entry.NotBefore < due)
					due = entry.NotBefore;
			}
			return due != DateTime.MaxValue;
		}

		public static TimeSpan GetDelay(int attempts)
		{
			if (attempts < 1)
				throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "The first unresolved attempt is 1.");
			// 250ms * 2^(attempts-1), computed without overflowing for a long-unresolved item
			double milliseconds = InitialDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempts - 1, 16));
			return milliseconds >= MaximumDelay.TotalMilliseconds ? MaximumDelay : TimeSpan.FromMilliseconds(milliseconds);
		}
	}
}
