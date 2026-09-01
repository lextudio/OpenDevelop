using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

public sealed class SharedDesignerHostPoolTests
{
	[Fact]
	public async Task CompatibleConcurrentLeasesShareOneConnection()
	{
		var token = TestContext.Current.CancellationToken;
		var starts = 0;
		var pool = new SharedDesignerHostPool<string, FakeConnection>(
			(_, connection) => connection.Alive,
			async (_, token) => { Interlocked.Increment(ref starts); await Task.Delay(20, token); return new FakeConnection(); },
			StringComparer.Ordinal, TimeSpan.FromMilliseconds(50));

		var acquisitions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => pool.AcquireAsync("net10", token)));
		Assert.All(acquisitions, connection => Assert.Same(acquisitions[0], connection));
		Assert.Equal(1, starts);
		Assert.Equal(8, pool.GetActiveLeaseCount("net10"));
		Assert.Equal(1, pool.GetGeneration("net10"));
		foreach (var connection in acquisitions) pool.Release("net10", connection);
	}

	[Fact]
	public async Task CompatibilityKeysAreIsolatedAndIdleLeaseIsReusedThenExpires()
	{
		var token = TestContext.Current.CancellationToken;
		var pool = new SharedDesignerHostPool<string, FakeConnection>(
			(_, connection) => connection.Alive, (_, _) => Task.FromResult(new FakeConnection()),
			StringComparer.Ordinal, TimeSpan.FromMilliseconds(80));
		var first = await pool.AcquireAsync("a", token);
		var other = await pool.AcquireAsync("b", token);
		Assert.NotSame(first, other);
		pool.Release("a", first);
		var reopened = await pool.AcquireAsync("a", token);
		Assert.Same(first, reopened);
		pool.Release("a", reopened);
		await WaitUntilAsync(() => !first.Alive, token);
		var replacement = await pool.AcquireAsync("a", token);
		Assert.NotSame(first, replacement);
		Assert.Equal(2, pool.GetGeneration("a"));
		pool.Release("a", replacement);
		pool.Release("b", other);
	}

	[Fact]
	public async Task InvalidatingOneKeyReplacesOnlyThatPoolGeneration()
	{
		var token = TestContext.Current.CancellationToken;
		var pool = new SharedDesignerHostPool<string, FakeConnection>(
			(_, connection) => connection.Alive, (_, _) => Task.FromResult(new FakeConnection()));
		var first = await pool.AcquireAsync("a", token);
		var other = await pool.AcquireAsync("b", token);
		pool.Invalidate("a", first);
		var replacement = await pool.AcquireAsync("a", token);
		Assert.False(first.Alive);
		Assert.NotSame(first, replacement);
		Assert.Equal(2, pool.GetGeneration("a"));
		Assert.Equal(1, pool.GetGeneration("b"));
		Assert.Same(other, await pool.AcquireAsync("b", token));
		pool.Release("a", replacement);
		pool.Release("b", other);
		pool.Release("b", other);
	}

	[Fact]
	public async Task RepeatedOpenCloseAndCrashCyclesLeaveNoLeasesOrLiveSupersededConnections()
	{
		var token = TestContext.Current.CancellationToken;
		var created = new List<FakeConnection>();
		var pool = new SharedDesignerHostPool<string, FakeConnection>(
			(_, connection) => connection.Alive,
			(_, _) => { var connection = new FakeConnection(); lock (created) created.Add(connection); return Task.FromResult(connection); },
			StringComparer.Ordinal, TimeSpan.FromMilliseconds(10));

		for (var cycle = 0; cycle < 40; cycle++) {
			var leases = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => pool.AcquireAsync("designer", token)));
			Assert.All(leases, lease => Assert.Same(leases[0], lease));
			if (cycle % 7 == 6) pool.Invalidate("designer", leases[0]);
			foreach (var lease in leases) pool.Release("designer", lease);
			Assert.Equal(0, pool.GetActiveLeaseCount("designer"));
		}

		await Task.Delay(50, token);
		lock (created) Assert.InRange(created.Count(connection => connection.Alive), 0, 1);
	}

	[Fact]
	public async Task RecoveryCoordinatesOneInvalidationAndRestoresEveryRecoverableLease()
	{
		var token = TestContext.Current.CancellationToken;
		var starts = 0;
		var broker = new SharedDesignerHostBroker<FakeConnection>(
			connection => connection.Alive,
			_ => { Interlocked.Increment(ref starts); return Task.FromResult(new FakeConnection()); });
		var failed = await broker.AcquireAsync(token);
		Assert.Same(failed, await broker.AcquireAsync(token));
		var first = new RecoveryClient(failed, true);
		var second = new RecoveryClient(failed, true);
		var unopened = new RecoveryClient(failed, false);
		var recovery = new SharedDesignerHostRecovery<RecoveryClient, FakeConnection>(broker,
			connection => new[] { first, second, unopened }.Where(client => ReferenceEquals(client.Connection, connection)).ToArray(),
			client => client.HasSnapshot,
			(client, _) => { client.CaptureCount++; return Task.CompletedTask; },
			(client, replacement, _) => { client.Connection = replacement; client.RestoreCount++; return Task.CompletedTask; });

		await recovery.RecoverAllAsync(failed, true, token);

		Assert.False(failed.Alive);
		Assert.Equal(2, starts);
		Assert.Equal(1, first.CaptureCount);
		Assert.Equal(1, second.CaptureCount);
		Assert.Equal(0, unopened.CaptureCount);
		Assert.Equal(1, first.RestoreCount);
		Assert.Equal(1, second.RestoreCount);
		Assert.Equal(0, unopened.RestoreCount);
		Assert.Same(first.Connection, second.Connection);
		Assert.Same(failed, unopened.Connection);
		broker.Release(first.Connection);
		broker.Release(second.Connection);
	}

	[Fact]
	public async Task RecoveryContinuesWhenOneDocumentCannotBeRestored()
	{
		var token = TestContext.Current.CancellationToken;
		var broker = new SharedDesignerHostBroker<FakeConnection>(
			connection => connection.Alive, _ => Task.FromResult(new FakeConnection()));
		var failed = await broker.AcquireAsync(token);
		Assert.Same(failed, await broker.AcquireAsync(token));
		var broken = new RecoveryClient(failed, true);
		var healthy = new RecoveryClient(failed, true);
		Exception? reported = null;
		var recovery = new SharedDesignerHostRecovery<RecoveryClient, FakeConnection>(broker,
			connection => new[] { broken, healthy }.Where(client => ReferenceEquals(client.Connection, connection)).ToArray(),
			client => client.HasSnapshot,
			(_, _) => Task.CompletedTask,
			(client, replacement, _) => {
				client.Connection = replacement;
				if (ReferenceEquals(client, broken)) throw new InvalidOperationException("Broken design document");
				client.RestoreCount++;
				return Task.CompletedTask;
			},
			(_, exception) => reported = exception);

		await recovery.RecoverAllAsync(failed, false, token);

		Assert.IsType<InvalidOperationException>(reported);
		Assert.Equal(0, broken.RestoreCount);
		Assert.Equal(1, healthy.RestoreCount);
		Assert.NotSame(failed, healthy.Connection);
		Assert.Same(broken.Connection, healthy.Connection);
		broker.Release(broken.Connection);
		broker.Release(healthy.Connection);
	}

	static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(2));
		while (!condition()) await Task.Delay(10, timeout.Token);
	}

	sealed class FakeConnection : IDisposable
	{
		public bool Alive { get; private set; } = true;
		public void Dispose() => Alive = false;
	}

	sealed class RecoveryClient
	{
		public RecoveryClient(FakeConnection connection, bool hasSnapshot) { Connection = connection; HasSnapshot = hasSnapshot; }
		public FakeConnection Connection { get; set; }
		public bool HasSnapshot { get; }
		public int CaptureCount { get; set; }
		public int RestoreCount { get; set; }
	}
}
