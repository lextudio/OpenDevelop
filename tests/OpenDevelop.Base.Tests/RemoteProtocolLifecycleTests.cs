using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using ICSharpCode.SharpDevelop.LanguageServices;
using Xunit;

namespace OpenDevelop.Base.Tests;

public class RemoteProtocolLifecycleTests
{
	sealed class ConcurrentHost : IRecoveringRoslynHost
	{
		public readonly TaskCompletionSource SlowEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource ReleaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool IsAlive => true;
		public bool FailDocumentUpdates { get; set; }
		public Task EnsureStartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public async Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
		{
			if (FailDocumentUpdates && method == RoslynProtocolMethods.DidChange)
				throw new InvalidOperationException("simulated update failure");
			if (method == "slow") {
				SlowEntered.TrySetResult();
				await ReleaseSlow.Task.WaitAsync(cancellationToken);
			}
			return (T)(object)method;
		}
		public void Dispose() { }
	}

	[Fact]
	public async Task RecoveryTransport_ReleasesStateGateAfterFailedUpdate()
	{
		var host = new ConcurrentHost { FailDocumentUpdates = true };
		using var transport = new RecoveringRoslynTransport(() => host);
		var update = new RemoteRoslynLanguageProtocol.DocumentUpdate(new TextDocumentIdentifier("Broken.cs"), "class Broken {}");
		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			transport.InvokeAsync<object?>(RoslynProtocolMethods.DidChange, update, TestContext.Current.CancellationToken));
		host.FailDocumentUpdates = false;
		Assert.Equal("fast", await transport.InvokeAsync<string>("fast", new object(), TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task RecoveryTransport_AllowsIndependentQueriesAfterReplay()
	{
		var host = new ConcurrentHost();
		using var transport = new RecoveringRoslynTransport(() => host);
		var slow = transport.InvokeAsync<string>("slow", new object(), TestContext.Current.CancellationToken);
		await host.SlowEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
		var fast = await transport.InvokeAsync<string>("fast", new object(), TestContext.Current.CancellationToken)
			.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
		Assert.Equal("fast", fast);
		host.ReleaseSlow.TrySetResult();
		Assert.Equal("slow", await slow);
	}

	sealed class RecordingHost : IRecoveringRoslynHost
	{
		public readonly List<(string Method, object Arguments)> Calls = new();
		public bool IsAlive { get; set; } = true;
		public Task EnsureStartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
		{
			Calls.Add((method, arguments));
			return Task.FromResult(default(T)!);
		}
		public void Dispose() { }
	}

	static LanguageServiceProjectSnapshot Project(string name) => new(
		"/tmp/" + name + ".csproj", "C#", Array.Empty<string>(), Array.Empty<string>(),
		Array.Empty<string>(), Array.Empty<string>(), null, null);

	[Fact]
	public async Task RecoveryTransport_ReplaysABatchedLoadAsSingleLoads()
	{
		// A batch is parent intent like the loads it stands for: a restarted host must receive
		// every project in it, replayed the same way single loads are.
		var hosts = new List<RecordingHost>();
		using var transport = new RecoveringRoslynTransport(() => { var host = new RecordingHost(); hosts.Add(host); return host; });
		var token = TestContext.Current.CancellationToken;
		await transport.InvokeAsync<object?>(RoslynProtocolMethods.ProjectsLoad,
			new RemoteRoslynLanguageProtocol.ProjectsUpdate(new[] { Project("A"), Project("B") }), token);
		// (The first host also gets the recorded intent replayed on start, as with single loads.)
		Assert.Equal(RoslynProtocolMethods.ProjectsLoad, hosts[0].Calls[^1].Method);

		hosts[0].IsAlive = false;
		await transport.InvokeAsync<object?>(RoslynProtocolMethods.Status, new object(), token);

		var replayed = hosts[1].Calls.Where(call => call.Method == RoslynProtocolMethods.ProjectLoad)
			.Select(call => ((RemoteRoslynLanguageProtocol.ProjectUpdate)call.Arguments).snapshot.ProjectFileName)
			.OrderBy(name => name);
		Assert.Equal(new[] { "/tmp/A.csproj", "/tmp/B.csproj" }, replayed);
	}

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference RegisterAndRelease(LanguageServiceRegistry registry)
    {
        using var service = new RemoteLanguageService(new RemoteRoslynLanguageProtocol(new WaitingTransport()), (_, _) => Task.CompletedTask);
        using var registration = registry.RegisterExtension(".cs", service);
        Assert.True(registry.TryGetProtocol("Sample.cs", out var protocol));
        Assert.Same(service.Protocol, protocol);
        return new WeakReference(service);
    }

    [Fact]
    public void ProtocolCacheDoesNotKeepUnregisteredServicesAlive()
    {
        var registry = new LanguageServiceRegistry();
        var weak = RegisterAndRelease(registry);
        for (var attempt = 0; attempt < 3 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(registry);
    }

    sealed class WaitingTransport : IRoslynProtocolTransport, IDisposable
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        public int Disposals;
        public async Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
        {
            Requests++;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return default!;
        }
        public void Dispose() => Disposals++;
    }

    [Fact]
    public async Task DisposalCancelsPendingWorkAndPermanentlyRejectsNewRequests()
    {
        var transport = new WaitingTransport();
        var protocol = new RemoteRoslynLanguageProtocol(transport);
        var pending = protocol.RoslynStatusAsync(TestContext.Current.CancellationToken);
        await transport.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        protocol.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => protocol.RoslynStatusAsync(TestContext.Current.CancellationToken));
        protocol.Dispose();
        Assert.Equal(1, transport.Requests);
        Assert.Equal(1, transport.Disposals);
    }
}
