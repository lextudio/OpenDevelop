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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol
{
	/// <summary>
	/// <see cref="IRoslynLanguageProtocol"/> implemented as a thin adapter over an
	/// <see cref="ILanguageService"/> in the same process - the proposal's Phase 2.
	///
	/// It deliberately adds no behaviour of its own beyond attaching readiness to each reply. That
	/// is what makes it useful as a proving step: consumers routed through it exercise the exact
	/// call shapes a remote host would receive, while every failure is still an ordinary
	/// in-process exception with a full stack. When the host arrives, this class stays as the
	/// local/in-process implementation and a remote one is selected instead - the consumers do not
	/// change.
	///
	/// The one thing worth watching for is chattiness: this adapter cannot fix a caller that makes
	/// N round trips where one would do, it only makes that visible (see §5.5, and §6 for the
	/// batched lens call that fixed the worst case).
	/// </summary>
	public sealed class InProcessRoslynLanguageProtocol : IRoslynLanguageProtocol
	{
		readonly ILanguageService service;
		readonly Func<IReadOnlyList<ProjectStatus>>? projectStatusProvider;
		readonly Func<LanguageServiceProjectSnapshot, CancellationToken, Task>? projectLoad;
		readonly Func<CancellationToken, Task>? solutionClosed;
		readonly Func<WorkspaceStatus>? workspaceStatusProvider;

		public InProcessRoslynLanguageProtocol(ILanguageService service,
			Func<IReadOnlyList<ProjectStatus>>? projectStatusProvider = null,
			Func<LanguageServiceProjectSnapshot, CancellationToken, Task>? projectLoad = null,
			Func<CancellationToken, Task>? solutionClosed = null,
			Func<WorkspaceStatus>? workspaceStatusProvider = null)
		{
			this.service = service ?? throw new ArgumentNullException(nameof(service));
			this.projectStatusProvider = projectStatusProvider;
			this.projectLoad = projectLoad;
			this.solutionClosed = solutionClosed;
			this.workspaceStatusProvider = workspaceStatusProvider;
		}

		DocumentReadiness ReadinessOf(TextDocumentIdentifier document) =>
			service.GetDocumentReadiness(document.ToDocumentId());

		public async Task<ProtocolResult<IReadOnlyList<SemanticToken>>> TextDocumentSemanticTokensAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetSemanticTokensAsync(document.ToDocumentId(), cancellationToken).ConfigureAwait(false));
		public async Task<ProtocolResult<string?>> RoslynSymbolNameAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetSymbolNameAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));
		public async Task<ProtocolResult<SymbolKindInfo?>> RoslynSymbolKindAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetSymbolKindAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));
		public Task<bool> RoslynValidIdentifierAsync(TextDocumentIdentifier document, string name, CancellationToken cancellationToken) =>
			service.IsValidIdentifierAsync(document.ToDocumentId(), name, cancellationToken);
		public Task<WorkspaceDocumentInfo?> RoslynDocumentStatusAsync(TextDocumentIdentifier document, CancellationToken cancellationToken, bool includeDiagnostics = false)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (includeDiagnostics && service is Roslyn.CSharpVBLanguageService roslyn)
				return roslyn.GetWorkspaceDocumentDiagnosticsAsync(document.ToDocumentId(), cancellationToken);
			return Task.FromResult(service.GetWorkspaceDocumentInfo(document.ToDocumentId()));
		}

		ProtocolResult<T> WithReadiness<T>(TextDocumentIdentifier document, T value) =>
			new(value, ReadinessOf(document));

		public Task TextDocumentDidChangeAsync(TextDocumentIdentifier document, string text, CancellationToken cancellationToken) =>
			service.UpsertDocumentAsync(document.ToDocumentId(), text, cancellationToken);

		public async Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> TextDocumentDefinitionAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GoToDefinitionAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<SymbolReferencesResult?>> TextDocumentReferencesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.FindReferencesAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<CompletionResult>> TextDocumentCompletionAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetCompletionsAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<QuickInfo?>> TextDocumentHoverAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetQuickInfoAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<IReadOnlyList<LanguageDiagnostic>>> TextDocumentDiagnosticsAsync(
			TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetDiagnosticsAsync(document.ToDocumentId(), cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<IReadOnlyList<DocumentOutlineNode>>> TextDocumentSymbolAsync(
			TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetDocumentOutlineAsync(document.ToDocumentId(), cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<IReadOnlyList<TextEdit>>> TextDocumentFormattingAsync(
			TextDocumentIdentifier document, TextSpan? span, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.FormatAsync(document.ToDocumentId(), span, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> TextDocumentRenameAsync(
			TextDocumentIdentifier document, int offset, string newName, CancellationToken cancellationToken, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false) =>
			WithReadiness(document, await service.RenameSymbolAsync(document.ToDocumentId(), offset, newName, cancellationToken, renameOverloads, renameInStrings, renameInComments).ConfigureAwait(false));

		public async Task<ProtocolResult<IReadOnlyList<CodeActionInfo>>> TextDocumentCodeActionAsync(
			TextDocumentIdentifier document, TextSpan span, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetCodeActionsAsync(document.ToDocumentId(), span, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> CodeActionApplyAsync(
			TextDocumentIdentifier document, string actionId, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.ApplyCodeActionAsync(document.ToDocumentId(), actionId, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySupertypesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetBaseSymbolsAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySubtypesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetDerivedSymbolsAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		/// <summary>
		/// In-process the IDE and the "host" share one workspace, so the graph is already known and
		/// this is a no-op. It still exists on the interface so consumers push the graph the same
		/// way in both modes - otherwise the push path would only ever be exercised remotely, which
		/// is where a missing call is hardest to notice.
		/// </summary>
		public Task RoslynProjectLoadAsync(LanguageServiceProjectSnapshot snapshot, CancellationToken cancellationToken) =>
			projectLoad != null ? projectLoad(snapshot, cancellationToken) : Task.CompletedTask;

		public Task RoslynSolutionClosedAsync(CancellationToken cancellationToken) =>
			solutionClosed != null ? solutionClosed(cancellationToken) : Task.CompletedTask;

		public Task<WorkspaceStatus> RoslynStatusAsync(CancellationToken cancellationToken) =>
			Task.FromResult(workspaceStatusProvider?.Invoke()
				?? new WorkspaceStatus(projectStatusProvider?.Invoke() ?? Array.Empty<ProjectStatus>()));

		public Task<LensDocumentResult> RoslynLensDocumentAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			service.GetLensDocumentAsync(document.ToDocumentId(), cancellationToken);

		public async Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> RoslynFindMemberAsync(
			string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken)
		{
			var targets = await service.FindMemberAsync(typeFullName, methodName, parameterCount, cancellationToken).ConfigureAwait(false);
			// Not document-scoped, so there is no per-document readiness to report.
			return ProtocolResult<IReadOnlyList<NavigationTarget>>.Ready(targets);
		}

		public async Task<ProtocolResult<string?>> RoslynHelpKeywordAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetHelpKeywordAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<string?>> RoslynContainingTypeAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetContainingTypeNameAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<ExtractInterfaceInfo?>> RoslynExtractInterfaceInfoAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.GetExtractInterfaceInfoAsync(document.ToDocumentId(), offset, cancellationToken).ConfigureAwait(false));

		public async Task<ProtocolResult<ExtractInterfaceResult?>> RoslynExtractInterfaceApplyAsync(
			TextDocumentIdentifier document, int offset, string interfaceName, IReadOnlyList<string> memberIds,
			bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken) =>
			WithReadiness(document, await service.ExtractInterfaceAsync(
				document.ToDocumentId(), offset, interfaceName, memberIds, addInterfaceToClass, includeComments, cancellationToken).ConfigureAwait(false));
	}
}
