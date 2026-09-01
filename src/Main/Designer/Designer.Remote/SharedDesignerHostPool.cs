using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.Designer.Remote
{
	/// <summary>
	/// Partitions shared designer connections by a backend-defined runtime compatibility key.
	/// A key normally includes designer kind, target runtime, architecture and dependency graph.
	/// </summary>
	public sealed class SharedDesignerHostPool<TKey, TConnection>
		where TKey : notnull
		where TConnection : class, IDisposable
	{
		readonly object gate = new object();
		readonly Dictionary<TKey, SharedDesignerHostBroker<TConnection>> brokers;
		readonly Func<TKey, TConnection, bool> isAlive;
		readonly Func<TKey, CancellationToken, Task<TConnection>> start;
		readonly TimeSpan? idleDelay;

		public SharedDesignerHostPool(Func<TKey, TConnection, bool> isAlive,
			Func<TKey, CancellationToken, Task<TConnection>> start,
			IEqualityComparer<TKey>? comparer = null, TimeSpan? idleDelay = null)
		{
			this.isAlive = isAlive ?? throw new ArgumentNullException(nameof(isAlive));
			this.start = start ?? throw new ArgumentNullException(nameof(start));
			this.idleDelay = idleDelay;
			brokers = new Dictionary<TKey, SharedDesignerHostBroker<TConnection>>(comparer);
		}

		public Task<TConnection> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
			=> GetOrCreateBroker(key).AcquireAsync(cancellationToken);

		public void Release(TKey key, TConnection connection)
			=> GetOrCreateBroker(key).Release(connection);

		public void Invalidate(TKey key, TConnection connection)
			=> GetOrCreateBroker(key).Invalidate(connection);

		/// <summary>Returns the compatibility partition's broker so document-recovery coordination
		/// can invalidate and reacquire the exact same shared lease set.</summary>
		public SharedDesignerHostBroker<TConnection> GetBroker(TKey key) => GetOrCreateBroker(key);

		public int GetActiveLeaseCount(TKey key) => GetOrCreateBroker(key).ActiveLeaseCount;
		public long GetGeneration(TKey key) => GetOrCreateBroker(key).Generation;

		SharedDesignerHostBroker<TConnection> GetOrCreateBroker(TKey key)
		{
			lock (gate) {
				if (!brokers.TryGetValue(key, out var broker)) {
					broker = new SharedDesignerHostBroker<TConnection>(
						connection => isAlive(key, connection),
						token => start(key, token), idleDelay);
					brokers.Add(key, broker);
				}
				return broker;
			}
		}
	}
}
