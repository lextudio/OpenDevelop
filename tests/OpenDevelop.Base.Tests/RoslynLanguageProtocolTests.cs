using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Covers the wire-shaped protocol surface while it is still in-process
/// (doc/technotes/roslyn-host-process.md Phase 2).
///
/// What these assert is not "the adapter forwards calls" - that would just restate the code. They
/// assert the two properties the protocol exists to guarantee, and that a remote implementation
/// would have to preserve: every document-scoped reply carries readiness, and a document is
/// identified by (uri, TFM) rather than by a live object.
/// </summary>
public class RoslynLanguageProtocolTests
{
    static IRoslynLanguageProtocol CreateProtocol() =>
        new InProcessRoslynLanguageProtocol(NoOpLanguageService.Instance);

    static readonly TextDocumentIdentifier Document = new("/tmp/Sample.cs");

    [Fact]
    public void TextDocumentIdentifier_IsPlainData_AndDefaultsToTheActiveTargetFramework()
    {
        // The identifier must be serialisable and must not imply a TFM it was not given: null means
        // "whatever is active", which is what every existing caller wants.
        Assert.Null(Document.TargetFramework);
        Assert.Equal("/tmp/Sample.cs", Document.Uri);
        Assert.Equal("/tmp/Sample.cs", Document.ToDocumentId().FileName);
    }

    [Fact]
    public void TextDocumentIdentifier_DistinguishesTargetFrameworkSlices()
    {
        // Multi-targeting is one of the two reasons plain LSP is the wrong shape (§4): the same
        // file has a different Roslyn document per TFM, so these must not be equal.
        var net8 = new TextDocumentIdentifier("/tmp/Sample.cs", "net8.0");
        var net10 = new TextDocumentIdentifier("/tmp/Sample.cs", "net10.0");

        Assert.NotEqual(net8, net10);
        Assert.Equal(net8, new TextDocumentIdentifier("/tmp/Sample.cs", "net8.0"));
    }

    [Fact]
    public async Task EveryDocumentScopedReply_CarriesReadiness()
    {
        // The invariant that makes a cold host safe to talk to: a caller can always tell whether
        // the answer is final. Checked across several methods so a new one that forgets the
        // envelope is not quietly fine because one sibling still has it.
        var protocol = CreateProtocol();
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(DocumentReadiness.Unknown, (await protocol.TextDocumentDefinitionAsync(Document, 0, token)).Readiness);
        Assert.Equal(DocumentReadiness.Unknown, (await protocol.TextDocumentReferencesAsync(Document, 0, token)).Readiness);
        Assert.Equal(DocumentReadiness.Unknown, (await protocol.TextDocumentHoverAsync(Document, 0, token)).Readiness);
        Assert.Equal(DocumentReadiness.Unknown, (await protocol.TextDocumentSymbolAsync(Document, token)).Readiness);
        Assert.Equal(DocumentReadiness.Unknown, (await protocol.TextDocumentDiagnosticsAsync(Document, token)).Readiness);
        Assert.Equal(DocumentReadiness.Unknown, (await protocol.RoslynHelpKeywordAsync(Document, 0, token)).Readiness);
        Assert.Equal(DocumentReadiness.Unknown, (await protocol.RoslynContainingTypeAsync(Document, 0, token)).Readiness);
    }

    [Fact]
    public async Task ReadinessTracksTheService_NotAConstant()
    {
        // Guards against an adapter that satisfies the test above by hardcoding a value.
        var readyProtocol = new InProcessRoslynLanguageProtocol(new AlwaysReadyLanguageService());

        Assert.Equal(DocumentReadiness.Ready,
            (await readyProtocol.TextDocumentDefinitionAsync(Document, 0, TestContext.Current.CancellationToken)).Readiness);
    }

    [Fact]
    public async Task FindMember_IsNotDocumentScoped()
    {
        // roslyn/findMember is by name, not position - the reason LSP cannot express it (§4). There
        // is no document whose readiness could qualify the answer.
        var result = await CreateProtocol().RoslynFindMemberAsync("N.C", "M", null, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentReadiness.Ready, result.Readiness);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task Status_IsAlwaysAnswerable_EvenWithNoProjects()
    {
        // roslyn/status is what a client polls while a host warms up, so it must answer before
        // anything is loaded rather than failing.
        var status = await CreateProtocol().RoslynStatusAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.Empty(status.Projects);
    }

    [Fact]
    public async Task LensDocument_GoesStraightThrough_KeepingItsOwnBatchReadiness()
    {
        // The lens reply carries readiness for the WHOLE batch rather than per anchor, so it is not
        // wrapped in ProtocolResult; this pins that shape (§6).
        var result = await CreateProtocol().RoslynLensDocumentAsync(Document, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentReadiness.Unknown, result.Readiness);
        Assert.Empty(result.Anchors);
    }

    sealed class AlwaysReadyLanguageService : ILanguageService
    {
        public DocumentReadiness GetDocumentReadiness(DocumentId documentId) => DocumentReadiness.Ready;
        public long GetWorkspaceRevision() => 0;
        public WorkspaceDocumentInfo? GetWorkspaceDocumentInfo(DocumentId documentId) => null;
        public Task<LensDocumentResult> GetLensDocumentAsync(DocumentId documentId, CancellationToken cancellationToken) =>
            Task.FromResult(new LensDocumentResult(DocumentReadiness.Ready, System.Array.Empty<LensAnchorResult>()));

        public Task UpsertDocumentAsync(DocumentId documentId, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult(CompletionResult.Empty);
        public Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<QuickInfo?>(null);
        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(System.Array.Empty<LanguageDiagnostic>());
        public Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NavigationTarget>>(System.Array.Empty<NavigationTarget>());
        public Task<SymbolReferencesResult?> FindReferencesAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<SymbolReferencesResult?>(null);
        public Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TextEdit>>(System.Array.Empty<TextEdit>());
        public void OnTextChanged(DocumentId documentId, TextChange change) { }
        public Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DocumentOutlineNode>>(System.Array.Empty<DocumentOutlineNode>());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(DocumentId documentId, int offset, string newName, CancellationToken cancellationToken, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(new Dictionary<string, IReadOnlyList<TextEdit>>());
        public Task<string?> GetSymbolNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> IsValidIdentifierAsync(DocumentId documentId, string name, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NavigationTarget>>(System.Array.Empty<NavigationTarget>());
        public Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeActionInfo>>(System.Array.Empty<CodeActionInfo>());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(DocumentId documentId, string actionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(new Dictionary<string, IReadOnlyList<TextEdit>>());
        public Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SemanticToken>>(System.Array.Empty<SemanticToken>());
        public Task<ExtractInterfaceInfo?> GetExtractInterfaceInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<ExtractInterfaceInfo?>(null);
        public Task<ExtractInterfaceResult?> ExtractInterfaceAsync(DocumentId documentId, int offset, string interfaceName, IReadOnlyList<string> memberIds, bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken) => Task.FromResult<ExtractInterfaceResult?>(null);
        public Task<SymbolKindInfo?> GetSymbolKindAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<SymbolKindInfo?>(null);
        public Task<SymbolHierarchyResult?> GetBaseSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<SymbolHierarchyResult?>(null);
        public Task<SymbolHierarchyResult?> GetDerivedSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<SymbolHierarchyResult?>(null);
        public Task<string?> GetHelpKeywordAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> GetContainingTypeNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task RefreshProjectAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
