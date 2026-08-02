using ICSharpCode.SharpDevelop.LanguageServices;

namespace OpenDevelop.Base.Tests;

/// <summary>
/// A configurable <see cref="ILanguageService"/> double for testing code that only needs
/// <see cref="GetDocumentOutlineAsync"/>/<see cref="FindReferencesAsync"/>/
/// <see cref="GetDerivedSymbolsAsync"/> - the three members <c>LanguageOpenLensAnchorProvider</c>/
/// <c>LanguageOpenLensProvider</c> call. Every other member throws
/// <see cref="NotSupportedException"/> so an accidental dependency on unconfigured behavior fails
/// loudly instead of silently returning an empty/null default.
/// </summary>
sealed class FakeLanguageService : ILanguageService
{
	public IReadOnlyList<DocumentOutlineNode> Outline { get; set; } = Array.Empty<DocumentOutlineNode>();
	public SymbolReferencesResult? References { get; set; }
	public SymbolHierarchyResult? DerivedSymbols { get; set; }
	public int UpsertDocumentCallCount { get; private set; }

	public Task UpsertDocumentAsync(DocumentId documentId, string text, CancellationToken cancellationToken)
	{
		UpsertDocumentCallCount++;
		return Task.CompletedTask;
	}

	public Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken) =>
		Task.FromResult(Outline);

	public Task<SymbolReferencesResult?> FindReferencesAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) =>
		Task.FromResult(References);

	public Task<SymbolHierarchyResult?> GetDerivedSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) =>
		Task.FromResult(DerivedSymbols);

	public Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken) => throw new NotSupportedException();
	public void OnTextChanged(DocumentId documentId, TextChange change) => throw new NotSupportedException();
	public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(DocumentId documentId, int offset, string newName, CancellationToken cancellationToken, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false) => throw new NotSupportedException();
	public Task<string?> GetSymbolNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<bool> IsValidIdentifierAsync(DocumentId documentId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(DocumentId documentId, string actionId, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<ExtractInterfaceInfo?> GetExtractInterfaceInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<ExtractInterfaceResult?> ExtractInterfaceAsync(DocumentId documentId, int offset, string interfaceName, IReadOnlyList<string> memberIds, bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<SymbolKindInfo?> GetSymbolKindAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId documentId, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<SymbolHierarchyResult?> GetBaseSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<string?> GetHelpKeywordAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task<string?> GetContainingTypeNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken) => throw new NotSupportedException();
	public Task RefreshProjectAsync(DocumentId documentId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
