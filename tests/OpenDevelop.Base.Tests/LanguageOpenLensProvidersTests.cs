using System.ComponentModel.Design;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.OpenLens;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Tests for <see cref="LanguageOpenLensAnchorProvider"/>/<see cref="LanguageOpenLensProvider"/> -
/// the generic <see cref="ILanguageService"/>-backed implementations CSharpBinding/VBBinding each
/// register their own extension-scoped instance of (doc/technotes/openlens.md §17.1). A
/// <see cref="FakeLanguageService"/> stands in for Roslyn/LSP so these run without a real compiler.
///
/// Each test gets its own <see cref="SD.InitializeForUnitTests"/> service container (see
/// <see cref="Setup"/>/<see cref="Dispose"/>) because both providers reach the language service
/// through <c>SD.GetService&lt;LanguageServiceRegistry&gt;()</c>, matching how they're actually
/// looked up in the running IDE rather than taking a constructor dependency - registered the same
/// way <c>RegisterCSharpOpenLensProvidersCommand</c> does in production.
/// </summary>
public class LanguageOpenLensProvidersTests : IDisposable
{
	readonly FakeLanguageService languageService = new();
	readonly LanguageServiceRegistry registry = new();

	public LanguageOpenLensProvidersTests()
	{
		SD.InitializeForUnitTests();
		registry.RegisterExtension(".cs", languageService);
		((IServiceContainer)SD.Services).AddService(typeof(LanguageServiceRegistry), registry);
	}

	public void Dispose() => SD.TearDownForUnitTests();

	static OpenLensDocumentContext Context(string fileName = "Test.cs") =>
		new(new DocumentId(fileName), fileName, DocumentVersion: 0,
			ResolveOffset: pos => (pos.Line - 1) * 100 + pos.Column);

	[Fact]
	public async Task Batch_RetriesLoadingWithoutTextChange_AndSeparatesSameLineDeclarations()
	{
		var provider = new LanguageOpenLensProvider("CSharp", ".cs");
		var first = new TextSpan(new TextPosition(1, 1), new TextPosition(1, 4));
		var second = new TextSpan(new TextPosition(1, 20), new TextPosition(1, 23));
		var entries = new[] {
			new LensAnchorResult("a", first, "Foo", null, SymbolOverridability.None, 2, -1, -1),
			new LensAnchorResult("b", second, "Bar", null, SymbolOverridability.None, 3, -1, -1)
		};
		OpenLensItem Item(string id, TextSpan span) => new("CSharp", "references", id, 0,
			new OpenLensPresentation("references"), null,
			new OpenLensAnchor(id, new DocumentId("Batch.cs"), new OpenLensRange(span), OpenLensAnchorKind.Type, id, null, 0), false);
		languageService.LensResult = new(DocumentReadiness.Loading, entries);
		Assert.False((await provider.ResolveAsync(Context("Batch.cs"), Item("a", first), CancellationToken.None)).IsResolved);
		languageService.LensResult = new(DocumentReadiness.Ready, entries);
		Assert.Equal("2 references", (await provider.ResolveAsync(Context("Batch.cs"), Item("a", first), CancellationToken.None)).Presentation.Title);
		Assert.Equal("3 references", (await provider.ResolveAsync(Context("Batch.cs"), Item("b", second), CancellationToken.None)).Presentation.Title);
		Assert.Equal(2, languageService.LensCalls);
	}

	[Fact]
	public async Task Batch_DoesNotReuseAnotherServiceOrTargetFramework()
	{
		var provider = new LanguageOpenLensProvider("CSharp", ".cs");
		var span = new TextSpan(new TextPosition(1, 1), new TextPosition(1, 4));
		LensDocumentResult Result(int count) => new(DocumentReadiness.Ready, new[] {
			new LensAnchorResult("a", span, "Foo", null, SymbolOverridability.None, count, -1, -1)
		});
		async Task<string> Resolve(string tfm)
		{
			var document = new DocumentId("Identity.cs", tfm);
			var context = new OpenLensDocumentContext(document, document.FileName, 0, _ => 0);
			var anchor = new OpenLensAnchor("a", document, new OpenLensRange(span), OpenLensAnchorKind.Type, "Foo", null, 0);
			var item = new OpenLensItem("CSharp", "references", "a", 0, new OpenLensPresentation("references"), null, anchor, false);
			return (await provider.ResolveAsync(context, item, CancellationToken.None)).Presentation.Title;
		}
		languageService.LensResult = Result(2);
		Assert.Equal("2 references", await Resolve("net8.0"));
		languageService.LensResult = Result(3);
		Assert.Equal("3 references", await Resolve("net10.0"));
		var replacement = new FakeLanguageService { LensResult = Result(4) };
		using var registration = registry.RegisterExtension(".cs", replacement);
		Assert.Equal("4 references", await Resolve("net10.0"));
		Assert.Equal(1, replacement.LensCalls);
	}

	[Fact]
	public async Task Batch_InvalidatesWhenAnotherDocumentChangesTheWorkspace()
	{
		var provider = new LanguageOpenLensProvider("CSharp", ".cs");
		var span = new TextSpan(new TextPosition(1, 1), new TextPosition(1, 4));
		var document = new DocumentId("Declaration.cs");
		var context = new OpenLensDocumentContext(document, document.FileName, 0, _ => 0);
		var anchor = new OpenLensAnchor("Class:Foo", document, new OpenLensRange(span), OpenLensAnchorKind.Type, "Foo", null, 0);
		var item = new OpenLensItem("CSharp", "references", anchor.AnchorId, 0, new OpenLensPresentation("references"), null, anchor, false);
		LensDocumentResult Result(int count) => new(DocumentReadiness.Ready, new[] {
			new LensAnchorResult(anchor.AnchorId, span, "Foo", null, SymbolOverridability.None, count, -1, -1)
		});

		languageService.LensResult = Result(1);
		Assert.Equal("1 reference", (await provider.ResolveAsync(context, item, CancellationToken.None)).Presentation.Title);
		languageService.WorkspaceRevision++;
		languageService.LensResult = Result(2);
		Assert.Equal("2 references", (await provider.ResolveAsync(context, item, CancellationToken.None)).Presentation.Title);
		Assert.Equal(2, languageService.LensCalls);
	}

	static DocumentOutlineNode Node(string kind, string name, int line = 1, int column = 1, IReadOnlyList<DocumentOutlineNode>? children = null) =>
		new(name, kind, new TextSpan(new TextPosition(line, column), new TextPosition(line, column + name.Length)), children ?? Array.Empty<DocumentOutlineNode>());

	[Fact]
	public async Task GetAnchorsAsync_FlattensTypesAndMembersWithStableIds()
	{
		languageService.Outline = new[] {
			Node("Class", "Foo", children: new[] {
				Node("Method", "Bar", line: 2),
				Node("Property", "Baz", line: 3),
			}),
		};
		var provider = new LanguageOpenLensAnchorProvider("CSharp", ".cs");

		var anchors = await provider.GetAnchorsAsync(Context(), requestedRange: null, CancellationToken.None);

		Assert.Equal(new[] { "Class:Foo", "Method:Bar", "Property:Baz" }, anchors.Select(a => a.AnchorId));
		Assert.Equal(OpenLensAnchorKind.Type, anchors[0].Kind);
		Assert.Equal(OpenLensAnchorKind.Method, anchors[1].Kind);
		Assert.Equal(OpenLensAnchorKind.Property, anchors[2].Kind);
	}

	[Fact]
	public async Task GetAnchorsAsync_ForUnhandledExtension_ReturnsEmpty()
	{
		languageService.Outline = new[] { Node("Class", "Foo") };
		var provider = new LanguageOpenLensAnchorProvider("CSharp", ".cs");

		var anchors = await provider.GetAnchorsAsync(Context("Test.vb"), requestedRange: null, CancellationToken.None);

		Assert.Empty(anchors);
	}

	[Fact]
	public void CanHandle_MatchesOnlyItsOwnExtension()
	{
		var provider = new LanguageOpenLensAnchorProvider("CSharp", ".cs");

		Assert.True(provider.CanHandle(Context("Test.cs")));
		Assert.False(provider.CanHandle(Context("Test.vb")));
	}

	[Fact]
	public async Task ProvideAsync_AddsSecondLensOnlyWhenOverridabilityIsSet()
	{
		// doc/technotes/openlens.md §17.3: an interface member/abstract member gets
		// "implementations", a virtual member/non-sealed override gets "overrides", and a
		// non-virtual, non-interface member gets neither.
		var provider = new LanguageOpenLensProvider("CSharp", ".cs");
		var anchors = new[] {
			new OpenLensAnchor("Interface:IFoo", new DocumentId("Test.cs"), default, OpenLensAnchorKind.Type, "IFoo", null, 0, SymbolOverridability.Implementable),
			new OpenLensAnchor("Method:Bar", new DocumentId("Test.cs"), default, OpenLensAnchorKind.Method, "Bar", null, 0, SymbolOverridability.Overridable),
			new OpenLensAnchor("Field:x", new DocumentId("Test.cs"), default, OpenLensAnchorKind.Field, "x", null, 0),
		};

		var items = await provider.ProvideAsync(Context(), anchors, CancellationToken.None);

		Assert.Equal(5, items.Count);
		Assert.All(items, i => Assert.False(i.IsResolved));
		// Sorted by AnchorId: "Field:x" < "Interface:IFoo" < "Method:Bar".
		Assert.Equal(new[] { "references", "references", "implementations", "references", "overrides" },
			items.OrderBy(i => i.AnchorId).ThenBy(i => i.Order).Select(i => i.LensId));
	}

	[Fact]
	public async Task ResolveAsync_FillsReferenceCountAndCommand()
	{
		languageService.References = new SymbolReferencesResult("Foo", new[] {
			new NavigationTarget("Test.cs", new TextPosition(5, 1)),
			new NavigationTarget("Test.cs", new TextPosition(6, 1)),
		});
		var provider = new LanguageOpenLensProvider("CSharp", ".cs");
		var anchor = new OpenLensAnchor("Class:Foo", new DocumentId("Test.cs"),
			new OpenLensRange(new TextSpan(new TextPosition(1, 1), new TextPosition(1, 4))),
			OpenLensAnchorKind.Type, "Foo", null, 0);
		var unresolved = new OpenLensItem("CSharp", "references", "Class:Foo", 0,
			new OpenLensPresentation("references"), Command: null, ResolveData: anchor, IsResolved: false);

		var resolved = await provider.ResolveAsync(Context(), unresolved, CancellationToken.None);

		Assert.True(resolved.IsResolved);
		Assert.Equal("2 references", resolved.Presentation.Title);
		Assert.NotNull(resolved.Command);
		Assert.Equal("OpenLens.ShowReferences", resolved.Command!.CommandId);
	}

	[Fact]
	public async Task ResolveAsync_ZeroReferences_UsesSingularAndPlural()
	{
		languageService.References = new SymbolReferencesResult("Foo", Array.Empty<NavigationTarget>());
		var provider = new LanguageOpenLensProvider("CSharp", ".cs");
		var anchor = new OpenLensAnchor("Method:Foo", new DocumentId("Test.cs"),
			new OpenLensRange(new TextSpan(new TextPosition(1, 1), new TextPosition(1, 4))),
			OpenLensAnchorKind.Method, "Foo", null, 0);
		var unresolved = new OpenLensItem("CSharp", "references", "Method:Foo", 0,
			new OpenLensPresentation("references"), Command: null, ResolveData: anchor, IsResolved: false);

		var resolved = await provider.ResolveAsync(Context(), unresolved, CancellationToken.None);

		Assert.Equal("0 references", resolved.Presentation.Title);
	}
}
