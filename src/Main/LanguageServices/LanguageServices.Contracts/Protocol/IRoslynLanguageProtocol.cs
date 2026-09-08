// Copyright (c) 2026 LeXtudio Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol
{
	/// <summary>
	/// Identifies a document for a protocol request, including which target framework's view of it
	/// is meant.
	///
	/// The TFM discriminator is one of the two things that make plain LSP the wrong shape here
	/// (doc/technotes/roslyn-host-process.md §4): LSP has exactly one view of a document, while
	/// OpenDevelop keeps one Roslyn document per TFM slice and has an active-TFM concept. Null means
	/// "the active TFM", which is what every existing caller wants.
	/// </summary>
	public sealed record TextDocumentIdentifier(string Uri, string? TargetFramework = null)
	{
		public DocumentId ToDocumentId() => new DocumentId(Uri, TargetFramework);
	}

	/// <summary>
	/// A protocol response together with the readiness of the state it was derived from.
	///
	/// Every result carries this, by design (§5.3): a host answers before it is warm, and a caller
	/// that caches has to be able to tell "not ready" from "genuinely nothing". Wrapping it into the
	/// envelope rather than leaving it to each payload means a new method cannot forget it.
	/// </summary>
	public sealed record ProtocolResult<T>(T Value, DocumentReadiness Readiness)
	{
		public static ProtocolResult<T> Ready(T value) => new(value, DocumentReadiness.Ready);
	}

	/// <summary>Per-project state, as reported by <c>roslyn/status</c>.</summary>
	public sealed record ProjectStatus(string ProjectFilePath, DocumentReadiness Readiness);

	/// <summary>Reply to <c>roslyn/status</c>.</summary>
	public sealed record WorkspaceStatus(IReadOnlyList<ProjectStatus> Projects)
	{
		public long SyntaxIndexDiskHits { get; init; }
		public long SyntaxIndexBuilds { get; init; }
	}

	/// <summary>
	/// The language surface OpenDevelop needs from Roslyn, shaped as a wire protocol.
	///
	/// **Why this exists before there is any wire.** The proposal's Phase 2 is to define this
	/// surface and implement it in-process as a thin adapter over the existing language service,
	/// then route consumers through it. That is where the design gets proven: if some consumer
	/// cannot be expressed here, it is discovered now - cheaply, with a debugger and no transport -
	/// rather than after a process boundary exists. Everything crossing this interface is already
	/// serialisable, so making it remote later is a transport change, not a redesign.
	///
	/// **Why not plain LSP.** Method names follow LSP where LSP fits, so an LSP-literate reader can
	/// navigate it. The <c>Roslyn*</c> members are the documented extensions (§4); the two that make
	/// a stock LSP server wrong rather than merely incomplete are multi-targeting (above) and
	/// project-model ownership - MSBuild evaluation stays in the IDE, so the host must be TOLD the
	/// project graph instead of discovering it.
	/// </summary>
	public interface IRoslynLanguageProtocol
	{
		Task<ProtocolResult<IReadOnlyList<SemanticToken>>> TextDocumentSemanticTokensAsync(TextDocumentIdentifier document, CancellationToken cancellationToken);
		Task<ProtocolResult<string?>> RoslynSymbolNameAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);
		Task<ProtocolResult<SymbolKindInfo?>> RoslynSymbolKindAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);
		Task<bool> RoslynValidIdentifierAsync(TextDocumentIdentifier document, string name, CancellationToken cancellationToken);
		Task<WorkspaceDocumentInfo?> RoslynDocumentStatusAsync(TextDocumentIdentifier document, CancellationToken cancellationToken, bool includeDiagnostics = false);
		// --- Maps onto standard LSP -------------------------------------------------------------

		/// <summary>LSP <c>textDocument/didOpen</c> / <c>didChange</c>: the client owns the buffer.</summary>
		Task TextDocumentDidChangeAsync(TextDocumentIdentifier document, string text, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/definition</c>.</summary>
		Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> TextDocumentDefinitionAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/references</c>.</summary>
		Task<ProtocolResult<SymbolReferencesResult?>> TextDocumentReferencesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/completion</c>.</summary>
		Task<ProtocolResult<CompletionResult>> TextDocumentCompletionAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/hover</c>.</summary>
		Task<ProtocolResult<QuickInfo?>> TextDocumentHoverAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/publishDiagnostics</c>, pulled rather than pushed.</summary>
		Task<ProtocolResult<IReadOnlyList<LanguageDiagnostic>>> TextDocumentDiagnosticsAsync(
			TextDocumentIdentifier document, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/documentSymbol</c>.</summary>
		Task<ProtocolResult<IReadOnlyList<DocumentOutlineNode>>> TextDocumentSymbolAsync(
			TextDocumentIdentifier document, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/formatting</c> and <c>rangeFormatting</c> (null span = whole document).</summary>
		Task<ProtocolResult<IReadOnlyList<TextEdit>>> TextDocumentFormattingAsync(
			TextDocumentIdentifier document, TextSpan? span, CancellationToken cancellationToken);

		/// <summary>LSP <c>textDocument/rename</c>, keyed by absolute file path.</summary>
		Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> TextDocumentRenameAsync(
			TextDocumentIdentifier document, int offset, string newName, CancellationToken cancellationToken, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false);

		/// <summary>LSP <c>textDocument/codeAction</c>.</summary>
		Task<ProtocolResult<IReadOnlyList<CodeActionInfo>>> TextDocumentCodeActionAsync(
			TextDocumentIdentifier document, TextSpan span, CancellationToken cancellationToken);

		/// <summary>
		/// LSP <c>codeAction/resolve</c> + apply. Throws <see cref="StaleCodeActionException"/> when
		/// the id was issued against different text (§5.2) - explicitly, rather than returning an
		/// empty edit map the caller would report as success.
		/// </summary>
		Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> CodeActionApplyAsync(
			TextDocumentIdentifier document, string actionId, CancellationToken cancellationToken);

		/// <summary>LSP <c>typeHierarchy/supertypes</c>.</summary>
		Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySupertypesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary>LSP <c>typeHierarchy/subtypes</c>.</summary>
		Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySubtypesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		// --- Roslyn-specific extensions (§4) ----------------------------------------------------

		/// <summary>
		/// <c>roslyn/project/load</c>: the IDE pushes an evaluated project.
		///
		/// This is the second reason a stock LSP server is the wrong answer (§4). An LSP server
		/// discovers projects itself; here MSBuild evaluation stays in the IDE, so the host must be
		/// TOLD the graph. A host that evaluated projects on its own would build a second project
		/// model, and the two would drift - which is exactly the class of bug that produced wrong
		/// references before RAR was fixed (§7a).
		/// </summary>
		Task RoslynProjectLoadAsync(LanguageServiceProjectSnapshot snapshot, CancellationToken cancellationToken);

		/// <summary><c>roslyn/solution/closed</c>: drop all project state.</summary>
		Task RoslynSolutionClosedAsync(CancellationToken cancellationToken);

		/// <summary><c>roslyn/status</c>: per-project readiness. The state LSP has no way to express.</summary>
		Task<WorkspaceStatus> RoslynStatusAsync(CancellationToken cancellationToken);

		/// <summary>
		/// <c>roslyn/lens/document</c>: every lens anchor and count for a document in one call.
		/// See §6 - the batched form is the whole point, for correctness as much as for speed.
		/// </summary>
		Task<LensDocumentResult> RoslynLensDocumentAsync(TextDocumentIdentifier document, CancellationToken cancellationToken);

		/// <summary><c>roslyn/findMember</c>: by name, not by position, so LSP cannot express it.</summary>
		Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> RoslynFindMemberAsync(
			string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken);

		/// <summary><c>roslyn/helpKeyword</c> (F1).</summary>
		Task<ProtocolResult<string?>> RoslynHelpKeywordAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary><c>roslyn/containingType</c>: one string, where documentSymbol would be a whole-document round trip.</summary>
		Task<ProtocolResult<string?>> RoslynContainingTypeAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary><c>roslyn/extractInterface/info</c>.</summary>
		Task<ProtocolResult<ExtractInterfaceInfo?>> RoslynExtractInterfaceInfoAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken);

		/// <summary><c>roslyn/extractInterface/apply</c>.</summary>
		Task<ProtocolResult<ExtractInterfaceResult?>> RoslynExtractInterfaceApplyAsync(
			TextDocumentIdentifier document, int offset, string interfaceName, IReadOnlyList<string> memberIds,
			bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken);
	}
}
