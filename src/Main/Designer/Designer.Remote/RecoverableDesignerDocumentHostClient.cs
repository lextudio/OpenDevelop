// Common parent-side recovery-snapshot mechanics for source-model designers.

using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Adds host-owned snapshot tracking to a document lease. Backends keep ownership of pool
	/// compatibility, restart coordination and their runtime-specific RPCs, while this class
	/// ensures a recovered document has the latest flushed source and stable document identity.
	/// </summary>
	public abstract class RecoverableDesignerDocumentHostClient : DesignerDocumentHostClient
	{
		protected RecoverableDesignerDocumentHostClient(DesignerHostProcessClient connection) : base(connection) { }

		protected DesignerDocumentSnapshot? RecoverySnapshot { get; private set; }
		/// <summary>Viewport accepted with <see cref="RecoverySnapshot"/>. It is recovery state,
		/// not source state: a restarted child must render the same parent viewport before its next
		/// layout notification arrives.</summary>
		protected DesignerViewport? RecoveryViewport { get; private set; }

		/// <summary>The primary file's text in the last recovery snapshot - the latest source the
		/// child accepted, refreshed after every accepted mutation. A view uses it to hand over its
		/// content while the host is down (mid-restart, or crashed before recovery), when a flush
		/// would throw.</summary>
		public string? RecoveryText => RecoverySnapshot?.Files.Count > 0 ? RecoverySnapshot.Files[0].Text : null;

		/// <summary>The current source: flushed from the child when it is running, otherwise the
		/// last recovery snapshot, otherwise <paramref name="fallback"/>.</summary>
		public string CurrentTextOrRecovery(long version, string fallback)
		{
			if (IsAlive) {
				try {
					var edit = Document.FlushAsync(version).GetAwaiter().GetResult();
					// No files means no changed source since the last capture (see CaptureRecoverySnapshotAsync).
					if (edit.Files.Count > 0) return edit.Files[0].Text;
				} catch (System.IO.IOException) when (!IsAlive) {
					// The host went down between the check and the flush: same as not running.
				}
			}
			return RecoveryText ?? fallback;
		}

		/// <summary>Records the parent-owned source authority for adapters whose open/update wire
		/// payload extends the common snapshot (for example viewport-aware markup hosts).</summary>
		protected void SetRecoverySnapshot(DesignerDocumentSnapshot snapshot, DesignerViewport? viewport = null)
		{
			RecoverySnapshot = snapshot ?? throw new System.ArgumentNullException(nameof(snapshot));
			RecoveryViewport = viewport;
		}

		protected async Task<DesignerSessionState> OpenRecoverableAsync(DesignerDocumentSnapshot snapshot,
			CancellationToken cancellationToken = default, DesignerViewport? viewport = null)
		{
			var state = await Document.OpenAsync(snapshot, viewport, cancellationToken).ConfigureAwait(false);
			if (state.Accepted)
				SetRecoverySnapshot(snapshot, viewport);
			return state;
		}

		protected async Task<DesignerSessionState> UpdateRecoverableAsync(DesignerDocumentSnapshot snapshot,
			CancellationToken cancellationToken = default, DesignerViewport? viewport = null)
		{
			// Source-only updates normally have no reason to repeat immutable surface metrics. Keep
			// the last accepted viewport so a later host recovery cannot silently fall back to the
			// child default just because this particular update omitted it.
			var effectiveViewport = viewport ?? RecoveryViewport;
			var state = await Document.UpdateAsync(snapshot, effectiveViewport, cancellationToken).ConfigureAwait(false);
			// A rejected stale/invalid source update must never become the next crash-recovery
			// authority. Retain the last accepted snapshot until the child accepts a replacement.
			if (state.Accepted)
				SetRecoverySnapshot(snapshot, effectiveViewport);
			return state;
		}

		protected async Task<DesignerSessionState> TrackMutationAsync(Task<DesignerSessionState> operation,
			CancellationToken cancellationToken = default)
		{
			var state = await operation.ConfigureAwait(false);
			// A rejected stale mutation reports the caller's stale version by design. Flushing
			// that version would turn a normal rejected operation into an RPC exception and,
			// more importantly, would not describe the source authority we need for recovery.
			if (state.Accepted)
				await CaptureRecoverySnapshotAsync(state.Version, cancellationToken).ConfigureAwait(false);
			return state;
		}

		/// <summary>Refreshes the parent authority before an explicit shared-host restart.</summary>
		protected async Task CaptureRecoverySnapshotAsync(long version, CancellationToken cancellationToken = default)
		{
			var snapshot = RecoverySnapshot;
			if (snapshot == null) return;
			var edit = await Document.FlushAsync(version, cancellationToken).ConfigureAwait(false);
			snapshot.Version = version;
			// Empty files means the child has no changed source to return; retain the last known
			// complete snapshot rather than replacing it with an empty document.
			if (edit.Files.Count == 0) return;
			snapshot.Files.Clear();
			foreach (var file in edit.Files) snapshot.Files.Add(file);
		}
	}
}
