using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpDevelop.LanguageServices;

namespace ICSharpCode.Translations;

/// <summary>
/// The in-process language service for gettext <c>.po</c> files, registered on <see cref="LanguageServiceRegistry"/> for the
/// ".po" extension. The questions are answered by <see cref="PoAnalyzer"/> (pure, tested without the IDE); this class only
/// converts offsets and lines to the contracts' positions and spans, holds the open documents, and resolves a
/// <c>#:</c> reference to the file it names (relative to the project folder, which is the parent of the <c>po</c> folder).
/// </summary>
public sealed class PoLanguageService : ILanguageService, ILanguageServiceDocumentLifecycle
{
	readonly ConcurrentDictionary<DocumentId, string> documents = new();
	long revision;

	public DocumentReadiness GetDocumentReadiness(DocumentId documentId)
		=> documents.ContainsKey(documentId) ? DocumentReadiness.Ready : DocumentReadiness.Unknown;

	public long GetWorkspaceRevision() => Interlocked.Read(ref revision);

	public WorkspaceDocumentInfo? GetWorkspaceDocumentInfo(DocumentId documentId)
		=> documents.ContainsKey(documentId) ? new WorkspaceDocumentInfo(DocumentReadiness.Ready, null, Array.Empty<string>()) : null;

	public Task<LensDocumentResult> GetLensDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
		=> Task.FromResult(new LensDocumentResult(GetDocumentReadiness(documentId), Array.Empty<LensAnchorResult>()));

	public Task UpsertDocumentAsync(DocumentId documentId, string text, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var changed = !documents.TryGetValue(documentId, out var existing) || !string.Equals(existing, text, StringComparison.Ordinal);
		documents[documentId] = text ?? "";
		if (changed) Interlocked.Increment(ref revision);
		return Task.CompletedTask;
	}

	public void OnTextChanged(DocumentId documentId, TextChange change)
	{
		if (change == null || !documents.TryGetValue(documentId, out var text)) return;
		var start = OffsetOf(text, change.Span.Start);
		var end = OffsetOf(text, change.Span.End);
		if (start > end) (start, end) = (end, start);
		documents[documentId] = text.Substring(0, start) + change.NewText + text.Substring(Math.Min(end, text.Length));
		Interlocked.Increment(ref revision);
	}

	/// <summary>The registry relays the IDE's document-closed message here, so a closed file's text is not kept.</summary>
	public Task CloseDocumentAsync(string fileName)
	{
		foreach (var id in documents.Keys.Where(key => string.Equals(key.FileName, fileName, StringComparison.OrdinalIgnoreCase)).ToList())
			documents.TryRemove(id, out _);
		return Task.CompletedTask;
	}

	bool TryGet(DocumentId documentId, out string text) => documents.TryGetValue(documentId, out text!);

	public Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text) || PoAnalyzer.Complete(text, offset) is not { } result)
			return Task.FromResult(CompletionResult.Empty);
		var items = result.Items.Select(i => new CompletionItem(i.Display, i.Insertion, i.Description)).ToList();
		return Task.FromResult(new CompletionResult(items, SpanOf(text, result.Start, offset)));
	}

	public Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text)) return Task.FromResult<QuickInfo?>(null);
		var line = PositionOf(text, offset).Line;
		return Task.FromResult(PoAnalyzer.Hover(text, line) is { } hover ? new QuickInfo(hover) : null);
	}

	public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text)) return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());
		var lines = text.Replace("\r", "").Split('\n');
		var diagnostics = PoAnalyzer.Analyze(text).Select(problem => {
			var length = problem.Line - 1 < lines.Length ? lines[problem.Line - 1].Length : 0;
			var span = new TextSpan(new TextPosition(problem.Line, 1), new TextPosition(problem.Line, length + 1));
			var severity = problem.Severity switch { PoSeverity.Error => DiagnosticSeverity.Error, PoSeverity.Warning => DiagnosticSeverity.Warning, _ => DiagnosticSeverity.Info };
			return new LanguageDiagnostic(problem.Id, problem.Message, severity, span);
		}).ToList();
		return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(diagnostics);
	}

	/// <summary>On a <c>#:</c> reference, the file and line it names; the file is looked for next to the .po's project folder first.</summary>
	public Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var none = Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());
		if (!TryGet(documentId, out var text) || PoAnalyzer.ReferenceAt(text, offset) is not { } reference) return none;
		var poDirectory = Path.GetDirectoryName(documentId.FileName) ?? "";
		var roots = new List<string>();
		if (string.Equals(Path.GetFileName(poDirectory), "po", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(poDirectory) is { } project) roots.Add(project);
		roots.Add(poDirectory);
		foreach (var root in roots) {
			var candidate = Path.GetFullPath(Path.Combine(root, reference.Path.Replace('/', Path.DirectorySeparatorChar)));
			if (File.Exists(candidate))
				return Task.FromResult<IReadOnlyList<NavigationTarget>>(new[] { new NavigationTarget(candidate, new TextPosition(Math.Max(1, reference.Line), 1)) });
		}
		return none;
	}

	public Task<SymbolReferencesResult?> FindReferencesAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<SymbolReferencesResult?>(null);

	public Task<string?> GetSymbolNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<string?>(null);

	public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(
		DocumentId documentId, int offset, string newName, CancellationToken cancellationToken,
		bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false)
		=> Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(new Dictionary<string, IReadOnlyList<TextEdit>>());

	public Task<bool> IsValidIdentifierAsync(DocumentId documentId, string name, CancellationToken cancellationToken) => Task.FromResult(false);

	public Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryGet(documentId, out var text)) return Task.FromResult<IReadOnlyList<DocumentOutlineNode>>(Array.Empty<DocumentOutlineNode>());
		var lines = text.Replace("\r", "").Split('\n');
		var nodes = PoAnalyzer.Outline(text).Select(item => {
			var length = item.Line - 1 < lines.Length ? lines[item.Line - 1].Length : 0;
			var span = new TextSpan(new TextPosition(item.Line, 1), new TextPosition(item.Line, length + 1));
			return new DocumentOutlineNode(item.Name, item.Kind, span, Array.Empty<DocumentOutlineNode>());
		}).ToList();
		return Task.FromResult<IReadOnlyList<DocumentOutlineNode>>(nodes);
	}

	static int OffsetOf(string text, TextPosition position)
	{
		var line = 1;
		var offset = 0;
		while (offset < text.Length && line < position.Line) {
			if (text[offset] == '\n') line++;
			offset++;
		}
		return Math.Min(text.Length, offset + position.Column - 1);
	}

	static TextPosition PositionOf(string text, int offset)
	{
		offset = Math.Max(0, Math.Min(offset, text.Length));
		var line = 1;
		var lineStart = 0;
		for (var index = 0; index < offset; index++)
			if (text[index] == '\n') { line++; lineStart = index + 1; }
		return new TextPosition(line, offset - lineStart + 1);
	}

	static TextSpan SpanOf(string text, int start, int end)
	{
		if (end < start) (end, start) = (start, end);
		return new TextSpan(PositionOf(text, start), PositionOf(text, end));
	}

	#region unsupported members

	public Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<TextEdit>>(Array.Empty<TextEdit>());

	public Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId documentId, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<SemanticToken>>(Array.Empty<SemanticToken>());

	public Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<CodeActionInfo>>(Array.Empty<CodeActionInfo>());

	public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(DocumentId documentId, string actionId, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(new Dictionary<string, IReadOnlyList<TextEdit>>());

	public Task<ExtractInterfaceInfo?> GetExtractInterfaceInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<ExtractInterfaceInfo?>(null);

	public Task<ExtractInterfaceResult?> ExtractInterfaceAsync(DocumentId documentId, int offset, string interfaceName,
		IReadOnlyList<string> memberIds, bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken)
		=> Task.FromResult<ExtractInterfaceResult?>(null);

	public Task<SymbolKindInfo?> GetSymbolKindAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<SymbolKindInfo?>(null);

	public Task<SymbolHierarchyResult?> GetBaseSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<SymbolHierarchyResult?>(null);

	public Task<SymbolHierarchyResult?> GetDerivedSymbolsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<SymbolHierarchyResult?>(null);

	public Task<string?> GetHelpKeywordAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<string?>(null);

	public Task<string?> GetContainingTypeNameAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
		=> Task.FromResult<string?>(null);

	public Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());

	public Task RefreshProjectAsync(DocumentId documentId, CancellationToken cancellationToken) => Task.CompletedTask;

	#endregion
}
