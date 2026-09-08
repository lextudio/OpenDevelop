using ICSharpCode.SharpDevelop.LanguageServices;
using ICSharpCode.SharpDevelop.LanguageServices.Protocol;
using Xunit;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// Drives the out-of-process pair - <see cref="RemoteRoslynLanguageProtocol"/> over a transport
/// into <see cref="RoslynProtocolDispatcher"/> - with the transport replaced by a direct call
/// (doc/technotes/roslyn-host-process.md Phase 3).
///
/// Removing the process, not the protocol, is what makes these worth having: they exercise the
/// exact method names and argument shapes that go on the wire, so a client/host mismatch fails
/// here in milliseconds instead of as a "method not found" in a spawned host. What they
/// deliberately do NOT cover is transport, handshake and recovery - that is Designer.Remote's job
/// and it is already tested there.
/// </summary>
public class RoslynProtocolRoundTripTests
{
    static readonly TextDocumentIdentifier Document = new("/tmp/Sample.cs", "net10.0");

    /// <summary>A transport that dispatches straight into a host-side dispatcher, in-memory.</summary>
    sealed class LoopbackTransport : IRoslynProtocolTransport
    {
        readonly RoslynProtocolDispatcher dispatcher;
        public List<string> Calls { get; } = new();

        public LoopbackTransport(IRoslynLanguageProtocol hostSide) =>
            dispatcher = new RoslynProtocolDispatcher(hostSide);

        public async Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
        {
            Calls.Add(method);
            Assert.True(dispatcher.CanHandle(method), $"Dispatcher cannot handle '{method}' sent by the client.");
            var result = await dispatcher.DispatchAsync(method, new ReflectionArguments(arguments), cancellationToken);
            return result is T typed ? typed : default!;
        }
    }

    /// <summary>
    /// Reads the anonymous argument object by property name - standing in for whatever the host's
    /// serialiser would do, while keeping the property NAMES under test.
    /// </summary>
    sealed class ReflectionArguments : IRoslynProtocolArguments
    {
        readonly object source;
        public ReflectionArguments(object source) => this.source = source;

        T Get<T>(string name)
        {
            var property = source.GetType().GetProperty(name)
                ?? throw new InvalidOperationException($"Request is missing argument '{name}'.");
            return (T)property.GetValue(source)!;
        }

        public TextDocumentIdentifier Document() => Get<TextDocumentIdentifier>("document");
        public int Offset() => Get<int>("offset");
        public string Text() => Get<string>("text");
        public string NewName() => Get<string>("newName");
        public string ActionId() => Get<string>("actionId");
        public TextSpan? Span() => Get<TextSpan?>("span");
        public LanguageServiceProjectSnapshot Snapshot() => Get<LanguageServiceProjectSnapshot>("snapshot");
        public string TypeFullName() => Get<string>("typeFullName");
        public string MethodName() => Get<string>("methodName");
        public int? ParameterCount() => Get<int?>("parameterCount");
        public string InterfaceName() => Get<string>("interfaceName");
        public IReadOnlyList<string> MemberIds() => Get<IReadOnlyList<string>>("memberIds");
        public bool AddInterfaceToClass() => Get<bool>("addInterfaceToClass");
        public bool IncludeComments() => Get<bool>("includeComments");
        public bool IncludeDiagnostics() => Get<bool>("includeDiagnostics");
        public bool RenameOverloads() => Get<bool>("renameOverloads");
        public bool RenameInStrings() => Get<bool>("renameInStrings");
        public bool RenameInComments() => Get<bool>("renameInComments");
    }

    static (IRoslynLanguageProtocol Client, LoopbackTransport Transport) CreatePair()
    {
        var transport = new LoopbackTransport(new InProcessRoslynLanguageProtocol(NoOpLanguageService.Instance));
        return (new RemoteRoslynLanguageProtocol(transport), transport);
    }

    [Fact]
    public async Task EveryDispatchedMethodName_IsOneTheHostAdvertises()
    {
        // The failure this prevents is a client sending a name the host never registered, which
        // surfaces only at runtime as "method not found". Asserted inside the transport for every
        // call below, and pinned here for the ones that matter most.
        var (client, transport) = CreatePair();
        var token = TestContext.Current.CancellationToken;

        await client.TextDocumentDidChangeAsync(Document, "class C {}", token);
        await client.TextDocumentDefinitionAsync(Document, 3, token);
        await client.RoslynLensDocumentAsync(Document, token);
        await client.RoslynStatusAsync(token);
        await client.RoslynProjectLoadAsync(new LanguageServiceProjectSnapshot(
            "/tmp/RoundTrip.csproj", "C#", Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), null, null), token);
        await client.RoslynSolutionClosedAsync(token);
        await client.RoslynFindMemberAsync("RoundTrip", "Run", 0, token);
        await client.RoslynExtractInterfaceApplyAsync(Document, 0, "IRoundTrip", new[] { "member" }, true, false, token);

        Assert.Equal(new[] {
            "textDocument/didChange", "textDocument/definition", "roslyn/lens/document", "roslyn/status",
            "roslyn/project/load", "roslyn/solution/closed", "roslyn/findMember", "roslyn/extractInterface/apply"
        }, transport.Calls);
    }

    [Fact]
    public void ClientAndHost_AgreeOnTheWholeMethodSet()
    {
        // Both sides name their methods from RoslynProtocolMethods, so this pins that the host
        // actually implements everything the constant list declares - a method added to the client
        // but forgotten in the dispatcher's switch fails here.
        var dispatcher = new RoslynProtocolDispatcher(new InProcessRoslynLanguageProtocol(NoOpLanguageService.Instance));

        foreach (var method in RoslynProtocolDispatcher.SupportedMethods)
            Assert.True(dispatcher.CanHandle(method), $"'{method}' is advertised but not handled.");
    }

    [Fact]
    public async Task ReadinessSurvivesTheRoundTrip()
    {
        // Readiness is the field a cold host most needs to convey, so it must not be something the
        // envelope loses in transit.
        var (client, _) = CreatePair();

        var result = await client.TextDocumentDefinitionAsync(Document, 0, TestContext.Current.CancellationToken);

        Assert.Equal(DocumentReadiness.Unknown, result.Readiness);
    }

    [Fact]
    public async Task TargetFrameworkSurvivesTheRoundTrip()
    {
        // Multi-targeting is a protocol extension, so the TFM has to travel with the request rather
        // than being implied by connection state - a host serving several TFMs cannot guess.
        TextDocumentIdentifier? seen = null;
        var capturing = new CapturingProtocol(d => seen = d);
        var client = new RemoteRoslynLanguageProtocol(new LoopbackTransport(capturing));

        await client.TextDocumentDefinitionAsync(Document, 0, TestContext.Current.CancellationToken);

        Assert.NotNull(seen);
        Assert.Equal("net10.0", seen!.TargetFramework);
    }

    [Fact]
    public async Task UnknownMethod_FailsLoudly()
    {
        // A host that silently returned null for an unknown method would turn a version mismatch
        // into wrong behaviour instead of an error.
        var dispatcher = new RoslynProtocolDispatcher(new InProcessRoslynLanguageProtocol(NoOpLanguageService.Instance));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            dispatcher.DispatchAsync("roslyn/doesNotExist", new ReflectionArguments(new { }), TestContext.Current.CancellationToken));
    }

    sealed class CapturingProtocol : IRoslynLanguageProtocol
    {
        public Task<ProtocolResult<IReadOnlyList<SemanticToken>>> TextDocumentSemanticTokensAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProtocolResult<string?>> RoslynSymbolNameAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProtocolResult<SymbolKindInfo?>> RoslynSymbolKindAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RoslynValidIdentifierAsync(TextDocumentIdentifier document, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceDocumentInfo?> RoslynDocumentStatusAsync(TextDocumentIdentifier document, CancellationToken cancellationToken, bool includeDiagnostics = false) => throw new NotSupportedException();
        readonly Action<TextDocumentIdentifier> onDocument;
        public CapturingProtocol(Action<TextDocumentIdentifier> onDocument) => this.onDocument = onDocument;

        public Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> TextDocumentDefinitionAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken)
        {
            onDocument(document);
            return Task.FromResult(ProtocolResult<IReadOnlyList<NavigationTarget>>.Ready(Array.Empty<NavigationTarget>()));
        }

        public Task TextDocumentDidChangeAsync(TextDocumentIdentifier document, string text, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProtocolResult<SymbolReferencesResult?>> TextDocumentReferencesAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<SymbolReferencesResult?>.Ready(null));
        public Task<ProtocolResult<CompletionResult>> TextDocumentCompletionAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<CompletionResult>.Ready(CompletionResult.Empty));
        public Task<ProtocolResult<QuickInfo?>> TextDocumentHoverAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<QuickInfo?>.Ready(null));
        public Task<ProtocolResult<IReadOnlyList<LanguageDiagnostic>>> TextDocumentDiagnosticsAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<IReadOnlyList<LanguageDiagnostic>>.Ready(Array.Empty<LanguageDiagnostic>()));
        public Task<ProtocolResult<IReadOnlyList<DocumentOutlineNode>>> TextDocumentSymbolAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<IReadOnlyList<DocumentOutlineNode>>.Ready(Array.Empty<DocumentOutlineNode>()));
        public Task<ProtocolResult<IReadOnlyList<TextEdit>>> TextDocumentFormattingAsync(TextDocumentIdentifier document, TextSpan? span, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<IReadOnlyList<TextEdit>>.Ready(Array.Empty<TextEdit>()));
        public Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> TextDocumentRenameAsync(TextDocumentIdentifier document, int offset, string newName, CancellationToken cancellationToken, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false) => Task.FromResult(ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>.Ready(new Dictionary<string, IReadOnlyList<TextEdit>>()));
        public Task<ProtocolResult<IReadOnlyList<CodeActionInfo>>> TextDocumentCodeActionAsync(TextDocumentIdentifier document, TextSpan span, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<IReadOnlyList<CodeActionInfo>>.Ready(Array.Empty<CodeActionInfo>()));
        public Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> CodeActionApplyAsync(TextDocumentIdentifier document, string actionId, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>.Ready(new Dictionary<string, IReadOnlyList<TextEdit>>()));
        public Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySupertypesAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<SymbolHierarchyResult?>.Ready(null));
        public Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySubtypesAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<SymbolHierarchyResult?>.Ready(null));
        public Task RoslynProjectLoadAsync(LanguageServiceProjectSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RoslynSolutionClosedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<WorkspaceStatus> RoslynStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new WorkspaceStatus(Array.Empty<ProjectStatus>()));
        public Task<LensDocumentResult> RoslynLensDocumentAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) => Task.FromResult(new LensDocumentResult(DocumentReadiness.Ready, Array.Empty<LensAnchorResult>()));
        public Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> RoslynFindMemberAsync(string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<IReadOnlyList<NavigationTarget>>.Ready(Array.Empty<NavigationTarget>()));
        public Task<ProtocolResult<string?>> RoslynHelpKeywordAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<string?>.Ready(null));
        public Task<ProtocolResult<string?>> RoslynContainingTypeAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<string?>.Ready(null));
        public Task<ProtocolResult<ExtractInterfaceInfo?>> RoslynExtractInterfaceInfoAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<ExtractInterfaceInfo?>.Ready(null));
        public Task<ProtocolResult<ExtractInterfaceResult?>> RoslynExtractInterfaceApplyAsync(TextDocumentIdentifier document, int offset, string interfaceName, IReadOnlyList<string> memberIds, bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken) => Task.FromResult(ProtocolResult<ExtractInterfaceResult?>.Ready(null));
    }
}
