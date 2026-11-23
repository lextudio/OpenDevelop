using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Switching solutions while the previous solution's project push is still running. The close used to be queued only after
/// that push finished, so the next solution's open documents had already been sent and were requesting outlines when the late
/// close disposed the Roslyn host under them and cleared their buffers (a freshly opened file kept no foldings: CI's
/// WorkbenchTests.SolutionExplorerFixture_TreeFileAndProjectBrowserChecks on Linux x64, reproduced on macOS by opening
/// OpenDevelop.Mvp.slnx and switching to SolutionExplorerFixture one second later). The close is now queued at once
/// (RegisterCSharpLanguageServiceCommand.OnSolutionClosed), and the old push's loads that were not queued yet are refused by
/// the workspace epoch instead of landing in the next solution's workspace.
///
/// These tests drive the real RemoteLanguageService, RemoteRoslynLanguageProtocol and RecoveringRoslynTransport in the order the
/// command produces, against hosts that record what reached them.
/// </summary>
public class RemoteLanguageServiceSolutionSwitchTests
{
	sealed class Host : IRecoveringRoslynHost
	{
		public readonly List<(string Method, object Arguments)> Calls = new();
		public bool Disposed { get; private set; }
		public bool IsAlive => !Disposed;
		public Task EnsureStartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
		{
			lock (Calls)
				Calls.Add((method, arguments));
			return Task.FromResult(default(T)!);
		}
		public void Dispose() => Disposed = true;

		public IEnumerable<string> LoadedProjects()
		{
			lock (Calls)
				return Calls.SelectMany(call => call.Arguments switch {
					RemoteRoslynLanguageProtocol.ProjectUpdate single => new[] { single.snapshot.ProjectFileName },
					RemoteRoslynLanguageProtocol.ProjectsUpdate batch => batch.snapshots.Select(s => s.ProjectFileName),
					_ => Array.Empty<string>(),
				}).ToList();
		}

		public IEnumerable<string> ChangedDocuments()
		{
			lock (Calls)
				return Calls.Where(call => call.Method == RoslynProtocolMethods.DidChange)
					.Select(call => ((RemoteRoslynLanguageProtocol.DocumentUpdate)call.Arguments).document.Uri).ToList();
		}
	}

	sealed class Fixture : IDisposable
	{
		public readonly List<Host> Hosts = new();
		public readonly RemoteLanguageService Service;
		readonly RecoveringRoslynTransport transport;

		public Fixture()
		{
			transport = new RecoveringRoslynTransport(() => { var host = new Host(); lock (Hosts) Hosts.Add(host); return host; });
			Service = new RemoteLanguageService(new RemoteRoslynLanguageProtocol(transport), (_, _) => Task.CompletedTask);
		}

		public void Dispose()
		{
			Service.Dispose();
			transport.Dispose();
		}
	}

	static LanguageServiceProjectSnapshot Project(string name) => new(
		"/tmp/" + name + ".csproj", "C#", Array.Empty<string>(), Array.Empty<string>(),
		Array.Empty<string>(), Array.Empty<string>(), null, null);

	static readonly DocumentId NextDocument = new("/tmp/Next/Program.cs");

	[Fact]
	public async Task ALoadForTheCurrentEpochIsQueued()
	{
		using var fixture = new Fixture();
		var token = TestContext.Current.CancellationToken;

		Assert.True(await fixture.Service.TryLoadProjectsAsync(new[] { Project("Old") }, fixture.Service.WorkspaceEpoch, token));

		Assert.Contains("/tmp/Old.csproj", fixture.Hosts.Single().LoadedProjects());
	}

	[Fact]
	public async Task ALoadRecordedBeforeACloseIsRefusedAfterIt()
	{
		using var fixture = new Fixture();
		var token = TestContext.Current.CancellationToken;
		var oldEpoch = fixture.Service.WorkspaceEpoch;

		await fixture.Service.CloseSolutionAsync(token);

		Assert.NotEqual(oldEpoch, fixture.Service.WorkspaceEpoch);
		Assert.False(await fixture.Service.TryLoadProjectsAsync(new[] { Project("Old") }, oldEpoch, token));
		Assert.DoesNotContain(fixture.Hosts, host => host.LoadedProjects().Contains("/tmp/Old.csproj"));
	}

	[Fact]
	public async Task ClosingAtOnceKeepsTheNextSolutionsDocumentAndKeepsOldLoadsOut()
	{
		using var fixture = new Fixture();
		var token = TestContext.Current.CancellationToken;
		var service = fixture.Service;

		// The previous solution's push has queued one chunk and is still building the rest.
		var oldEpoch = service.WorkspaceEpoch;
		Assert.True(await service.TryLoadProjectsAsync(new[] { Project("Old1") }, oldEpoch, token));
		var oldHost = fixture.Hosts.Single();

		// The solution switches: the close is queued at once, then the next solution's document is opened.
		var closing = service.CloseSolutionAsync(token);
		await service.UpsertDocumentAsync(NextDocument, "class Program { void Main() { } }", token);
		await closing;

		// The old push reaches its next chunk only now. Its epoch is stale, so nothing is sent.
		Assert.False(await service.TryLoadProjectsAsync(new[] { Project("Old2") }, oldEpoch, token));

		Assert.True(oldHost.Disposed, "the close must end the previous host's lifetime");
		var nextHost = fixture.Hosts.Last();
		Assert.NotSame(oldHost, nextHost);
		Assert.DoesNotContain("/tmp/Old2.csproj", fixture.Hosts.SelectMany(host => host.LoadedProjects()));
		Assert.DoesNotContain("/tmp/Old1.csproj", nextHost.LoadedProjects());
		Assert.Contains(NextDocument.FileName, nextHost.ChangedDocuments());
		Assert.False(nextHost.Disposed, "nothing may dispose the host the next solution's document is using");

		// The document's buffer survived the switch, so an edit still reaches the host. (A late close clears it, and
		// OnTextChanged then drops every edit of the file.)
		var before = nextHost.ChangedDocuments().Count();
		service.OnTextChanged(NextDocument, new TextChange(new TextSpan(new TextPosition(1, 1), new TextPosition(1, 1)), "// edited\n"));
		await WaitUntilAsync(() => nextHost.ChangedDocuments().Count() > before, token);
	}

	[Fact]
	public async Task ACloseDiscardsEveryBufferSoItMustPrecedeTheNextSolutionsDocuments()
	{
		// The contract the command relies on: CloseSolutionAsync is not scoped to a solution, it drops every buffer queued
		// before it. A close that arrives after the next solution's document (as the deferred close did) loses that document.
		using var fixture = new Fixture();
		var token = TestContext.Current.CancellationToken;
		var service = fixture.Service;
		await service.UpsertDocumentAsync(NextDocument, "class Program { }", token);

		await service.CloseSolutionAsync(token);

		var hosts = fixture.Hosts.Count;
		service.OnTextChanged(NextDocument, new TextChange(new TextSpan(new TextPosition(1, 1), new TextPosition(1, 1)), "// edited\n"));
		await Task.Delay(200, token);
		Assert.Equal(hosts, fixture.Hosts.Count);
		Assert.DoesNotContain(fixture.Hosts.Skip(1), host => host.ChangedDocuments().Any());
	}

	static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
	{
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
		while (!condition()) {
			Assert.True(DateTime.UtcNow < deadline, "timed out");
			await Task.Delay(20, token);
		}
	}
}
