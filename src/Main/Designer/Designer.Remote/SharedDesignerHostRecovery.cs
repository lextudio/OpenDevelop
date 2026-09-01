// Common coordination for recovering every document that leased a shared designer host.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Coordinates a single replacement connection after a shared host exits. The adapter keeps
	/// its runtime-specific connection type and reopen operation; this class owns the ordering:
	/// capture live source for an explicit restart, invalidate once, acquire one lease per document,
	/// then restore each recoverable document.
	/// </summary>
	public sealed class SharedDesignerHostRecovery<TClient, TConnection>
		where TConnection : class, IDisposable
	{
		readonly SharedDesignerHostBroker<TConnection> broker;
		readonly Func<TConnection, TClient[]> getAffectedClients;
		readonly Func<TClient, bool> canRecover;
		readonly Func<TClient, CancellationToken, Task> captureSnapshot;
		readonly Func<TClient, TConnection, CancellationToken, Task> restore;
		readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

		public SharedDesignerHostRecovery(SharedDesignerHostBroker<TConnection> broker,
			Func<TConnection, TClient[]> getAffectedClients, Func<TClient, bool> canRecover,
			Func<TClient, CancellationToken, Task> captureSnapshot,
			Func<TClient, TConnection, CancellationToken, Task> restore)
		{
			this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
			this.getAffectedClients = getAffectedClients ?? throw new ArgumentNullException(nameof(getAffectedClients));
			this.canRecover = canRecover ?? throw new ArgumentNullException(nameof(canRecover));
			this.captureSnapshot = captureSnapshot ?? throw new ArgumentNullException(nameof(captureSnapshot));
			this.restore = restore ?? throw new ArgumentNullException(nameof(restore));
		}

		/// <summary>Restores all documents on <paramref name="failed"/>. Explicit restarts first
		/// flush their source authority while the old host remains available.</summary>
		public async Task RecoverAllAsync(TConnection failed, bool captureBeforeRestart,
			CancellationToken cancellationToken = default)
		{
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				var clients = getAffectedClients(failed);
				if (clients.Length == 0) return;
				if (captureBeforeRestart) {
					foreach (var client in clients) {
						if (!canRecover(client)) continue;
						try { await captureSnapshot(client, cancellationToken).ConfigureAwait(false); }
						catch { }
					}
				}
				broker.Invalidate(failed);
				foreach (var client in clients) {
					if (!canRecover(client)) continue;
					var replacement = await broker.AcquireAsync(cancellationToken).ConfigureAwait(false);
					await restore(client, replacement, cancellationToken).ConfigureAwait(false);
				}
			} finally { gate.Release(); }
		}
	}
}
