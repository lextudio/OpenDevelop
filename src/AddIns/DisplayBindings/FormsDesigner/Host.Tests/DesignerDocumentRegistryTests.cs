using ICSharpCode.SharpDevelop.Designer.Remote;
using Xunit;

namespace ICSharpCode.FormsDesigner.Host.Tests;

public sealed class DesignerDocumentRegistryTests
{
	[Fact]
	public async Task ConcurrentOpenPublishesOneDocumentSession()
	{
		var registry = new DesignerDocumentRegistry<Session>();
		registry.Initialize("session-a");
		var creationCount = 0;
		var opens = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
			registry.GetOrAdd("session-a", "document-a", () => new Session(Interlocked.Increment(ref creationCount)))));

		var sessions = await Task.WhenAll(opens);

		Assert.Equal(1, creationCount);
		Assert.All(sessions, session => Assert.Same(sessions[0], session));
		Assert.Equal(1, registry.Count);
	}

	[Fact]
	public void RejectsAccessBeforeHandshakeAndFromAnotherSession()
	{
		var registry = new DesignerDocumentRegistry<Session>();
		Assert.Throws<UnauthorizedAccessException>(() => registry.GetOrAdd("session-a", "document-a", () => new Session(1)));
		registry.Initialize("session-a");
		Assert.Throws<UnauthorizedAccessException>(() => registry.GetOrAdd("session-b", "document-a", () => new Session(1)));
		Assert.Throws<ArgumentException>(() => registry.GetOrAdd("session-a", "", () => new Session(1)));
	}

	[Fact]
	public void RemoveAndShutdownCloseEachMaterializedDocumentOnce()
	{
		var registry = new DesignerDocumentRegistry<Session>();
		registry.Initialize("session-a");
		var first = registry.GetOrAdd("session-a", "first", () => new Session(1));
		var second = registry.GetOrAdd("session-a", "second", () => new Session(2));

		Assert.True(registry.Remove("session-a", "first", session => session.Close()));
		Assert.False(registry.Remove("session-a", "first", session => session.Close()));
		registry.CloseAll(session => session.Close());
		registry.CloseAll(session => session.Close());

		Assert.Equal(1, first.CloseCount);
		Assert.Equal(1, second.CloseCount);
		Assert.Equal(0, registry.Count);
		Assert.Throws<ObjectDisposedException>(() => registry.GetOrAdd("session-a", "third", () => new Session(3)));
	}

	sealed class Session(int id)
	{
		public int Id { get; } = id;
		public int CloseCount { get; private set; }
		public void Close() => CloseCount++;
	}
}
