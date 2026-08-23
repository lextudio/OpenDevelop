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
			=> GetBroker(key).AcquireAsync(cancellationToken);

		public void Release(TKey key, TConnection connection)
			=> GetBroker(key).Release(connection);

		public void Invalidate(TKey key, TConnection connection)
			=> GetBroker(key).Invalidate(connection);

		public int GetActiveLeaseCount(TKey key) => GetBroker(key).ActiveLeaseCount;
		public long GetGeneration(TKey key) => GetBroker(key).Generation;

		SharedDesignerHostBroker<TConnection> GetBroker(TKey key)
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
