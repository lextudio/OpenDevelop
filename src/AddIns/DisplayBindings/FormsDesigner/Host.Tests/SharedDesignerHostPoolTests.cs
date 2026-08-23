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
}
