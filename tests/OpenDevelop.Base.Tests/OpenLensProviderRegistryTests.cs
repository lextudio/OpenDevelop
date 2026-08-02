using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Contract tests for <see cref="OpenLensProviderRegistry"/> (doc/technotes/codelens.md §22
/// "Contract tests"). These exercise the registry in isolation - no editor, no document, no
/// ILanguageService - so they run fast and without booting the full application, unlike
/// tests/OpenDevelop.IntegrationTests.
/// </summary>
public class OpenLensProviderRegistryTests
{
	static OpenLensDocumentContext Context(string fileName = "Test.cs") =>
		new(new DocumentId(fileName), fileName, DocumentVersion: 0, ResolveOffset: pos => 0);

	[Fact]
	public void GetProviders_OrdersByOrder()
	{
		var registry = new OpenLensProviderRegistry();
		var last = new FakeProvider("last", order: 10);
		var first = new FakeProvider("first", order: 0);
		var middle = new FakeProvider("middle", order: 5);

		registry.RegisterProvider(last);
		registry.RegisterProvider(first);
		registry.RegisterProvider(middle);

		var ordered = registry.GetProviders(Context());

		Assert.Equal(new[] { "first", "middle", "last" }, ordered.Select(p => p.Id));
	}

	[Fact]
	public void GetProviders_ExcludesProvidersThatCannotHandleTheContext()
	{
		var registry = new OpenLensProviderRegistry();
		registry.RegisterProvider(new FakeProvider("cs", order: 0, extension: ".cs"));
		registry.RegisterProvider(new FakeProvider("vb", order: 0, extension: ".vb"));

		var ordered = registry.GetProviders(Context("Test.cs"));

		Assert.Equal(new[] { "cs" }, ordered.Select(p => p.Id));
	}

	[Fact]
	public void RegisterProvider_DisposingRegistrationRemovesIt()
	{
		var registry = new OpenLensProviderRegistry();
		var provider = new FakeProvider("p", order: 0);

		var registration = registry.RegisterProvider(provider);
		Assert.Single(registry.GetProviders(Context()));

		registration.Dispose();
		Assert.Empty(registry.GetProviders(Context()));
	}

	[Fact]
	public void RegisterProvider_DisposingTwiceDoesNotThrow()
	{
		var registry = new OpenLensProviderRegistry();
		var registration = registry.RegisterProvider(new FakeProvider("p", order: 0));

		registration.Dispose();
		registration.Dispose();
	}

	[Fact]
	public void RegisterAnchorProvider_DisposingRegistrationRemovesIt()
	{
		var registry = new OpenLensProviderRegistry();
		var registration = registry.RegisterAnchorProvider(new FakeAnchorProvider("a"));

		Assert.Single(registry.GetAnchorProviders(Context()));
		registration.Dispose();
		Assert.Empty(registry.GetAnchorProviders(Context()));
	}

	[Fact]
	public void RequestRefresh_RaisesRefreshRequestedWithGivenArgs()
	{
		var registry = new OpenLensProviderRegistry();
		OpenLensRefreshEventArgs? received = null;
		registry.RefreshRequested += (_, e) => received = e;

		var args = new OpenLensRefreshEventArgs("git", new DocumentId("Test.cs"), new[] { "Class:Foo" });
		registry.RequestRefresh(args);

		Assert.Same(args, received);
		Assert.Equal("git", received!.ProviderId);
		Assert.Equal(new[] { "Class:Foo" }, received.AnchorIds);
	}

	[Fact]
	public void RequestRefresh_WithNullAnchorIds_MeansEveryAnchor()
	{
		var args = new OpenLensRefreshEventArgs("git");
		Assert.Null(args.AnchorIds);
	}

	sealed class FakeProvider(string id, int order, string extension = ".cs") : IOpenLensProvider
	{
		public string Id { get; } = id;
		public int Order { get; } = order;

		public bool CanHandle(OpenLensDocumentContext context) => context.FileName.EndsWith(extension);

		public Task<IReadOnlyList<OpenLensItem>> ProvideAsync(OpenLensDocumentContext context, IReadOnlyList<OpenLensAnchor> anchors, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<OpenLensItem>>(Array.Empty<OpenLensItem>());

		public Task<OpenLensItem> ResolveAsync(OpenLensDocumentContext context, OpenLensItem item, CancellationToken cancellationToken) =>
			Task.FromResult(item);
	}

	sealed class FakeAnchorProvider(string id) : IOpenLensAnchorProvider
	{
		public string Id { get; } = id;

		public bool CanHandle(OpenLensDocumentContext context) => true;

		public Task<IReadOnlyList<OpenLensAnchor>> GetAnchorsAsync(OpenLensDocumentContext context, OpenLensRange? requestedRange, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<OpenLensAnchor>>(Array.Empty<OpenLensAnchor>());
	}
}
