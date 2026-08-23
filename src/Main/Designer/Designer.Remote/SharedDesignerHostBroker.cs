using System;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Keeps one host connection alive for all documents of a designer backend. The idle
	/// grace period avoids process and Dock churn when documents are closed and reopened.
	/// </summary>
	public sealed class SharedDesignerHostBroker<TConnection> where TConnection : class, IDisposable
	{
		readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
		readonly Func<TConnection, bool> isAlive;
		readonly Func<CancellationToken, Task<TConnection>> start;
		readonly TimeSpan idleDelay;
		TConnection? shared;
		CancellationTokenSource? idleShutdown;
		int leases;
		long generation;

		public int ActiveLeaseCount {
			get { gate.Wait(); try { return leases; } finally { gate.Release(); } }
		}

		public long Generation {
			get { gate.Wait(); try { return generation; } finally { gate.Release(); } }
		}

		public SharedDesignerHostBroker(Func<TConnection, bool> isAlive,
			Func<CancellationToken, Task<TConnection>> start, TimeSpan? idleDelay = null)
		{
			this.isAlive = isAlive ?? throw new ArgumentNullException(nameof(isAlive));
			this.start = start ?? throw new ArgumentNullException(nameof(start));
			this.idleDelay = idleDelay ?? TimeSpan.FromSeconds(10);
		}

		public async Task<TConnection> AcquireAsync(CancellationToken cancellationToken = default)
		{
			await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try {
				idleShutdown?.Cancel();
				idleShutdown?.Dispose();
				idleShutdown = null;
				if (shared == null || !isAlive(shared)) {
					shared?.Dispose();
					shared = await start(cancellationToken).ConfigureAwait(false);
					generation++;
					leases = 0;
				}
				leases++;
				return shared;
			} finally { gate.Release(); }
		}

		public void Release(TConnection connection)
		{
			gate.Wait();
			try {
				if (!ReferenceEquals(shared, connection) || leases == 0) return;
				leases--;
				if (leases != 0) return;
				idleShutdown?.Cancel();
				idleShutdown?.Dispose();
				idleShutdown = new CancellationTokenSource();
				_ = DisposeWhenIdleAsync(connection, idleShutdown.Token);
			} finally { gate.Release(); }
		}

		public void Invalidate(TConnection connection)
		{
			gate.Wait();
			try {
				if (!ReferenceEquals(shared, connection)) return;
				idleShutdown?.Cancel();
				idleShutdown?.Dispose();
				idleShutdown = null;
				shared = null;
				leases = 0;
				connection.Dispose();
			} finally { gate.Release(); }
		}

		async Task DisposeWhenIdleAsync(TConnection connection, CancellationToken cancellationToken)
		{
			try { await Task.Delay(idleDelay, cancellationToken).ConfigureAwait(false); }
			catch (OperationCanceledException) { return; }
			await gate.WaitAsync().ConfigureAwait(false);
			try {
				if (leases == 0 && ReferenceEquals(shared, connection)) {
					shared = null;
					idleShutdown?.Dispose();
					idleShutdown = null;
					connection.Dispose();
				}
			} finally { gate.Release(); }
		}
	}
}
