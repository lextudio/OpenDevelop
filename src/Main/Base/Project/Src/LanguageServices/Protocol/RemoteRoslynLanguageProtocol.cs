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
	/// Sends a protocol request and returns the deserialised reply. Exists so the protocol client
	/// does not depend on any particular RPC library or process-management strategy: the shipping
	/// implementation forwards to the same StreamJsonRpc/TCP client the designers already use
	/// (Designer.Remote), and tests supply an in-memory one.
	/// </summary>
	public interface IRoslynProtocolTransport
	{
		Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken);
	}

	/// <summary>
	/// <see cref="IRoslynLanguageProtocol"/> over a transport - the out-of-process half of Phase 3.
	///
	/// There is deliberately no logic here beyond naming the method and passing arguments through.
	/// Everything this class can send was already made serialisable in Phase 2, and every consumer
	/// was already routed through the interface, so swapping
	/// <see cref="InProcessRoslynLanguageProtocol"/> for this one is a composition change rather
	/// than a rewrite of any call site.
	///
	/// **Cancellation is passed through on purpose.** A cancelled request must stop work in the
	/// host, not merely abandon the reply; otherwise a caret moving through a file leaves the host
	/// computing answers nobody will read - which in-process merely wastes CPU but across a
	/// boundary also blocks the queue behind it.
	/// </summary>
	public sealed class RemoteRoslynLanguageProtocol : IRoslynLanguageProtocol, IDisposable
	{
		readonly IRoslynProtocolTransport transport;
		readonly CancellationTokenSource lifetime = new();
		int disposed;

		public RemoteRoslynLanguageProtocol(IRoslynProtocolTransport transport)
		{
			this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
		}

		public Task TextDocumentDidChangeAsync(TextDocumentIdentifier document, string text, CancellationToken cancellationToken) =>
			InvokeAsync<object?>(RoslynProtocolMethods.DidChange, new DocumentUpdate(document, text), cancellationToken);

		public sealed record DocumentUpdate(TextDocumentIdentifier document, string text);
		public Task<ProtocolResult<IReadOnlyList<SemanticToken>>> TextDocumentSemanticTokensAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyList<SemanticToken>>>(RoslynProtocolMethods.SemanticTokens, new { document }, cancellationToken);
		public Task<ProtocolResult<string?>> RoslynSymbolNameAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<string?>>(RoslynProtocolMethods.SymbolName, new { document, offset }, cancellationToken);
		public Task<ProtocolResult<SymbolKindInfo?>> RoslynSymbolKindAsync(TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<SymbolKindInfo?>>(RoslynProtocolMethods.SymbolKind, new { document, offset }, cancellationToken);
		public Task<bool> RoslynValidIdentifierAsync(TextDocumentIdentifier document, string name, CancellationToken cancellationToken) =>
			InvokeAsync<bool>(RoslynProtocolMethods.ValidIdentifier, new { document, newName = name }, cancellationToken);
		public Task<WorkspaceDocumentInfo?> RoslynDocumentStatusAsync(TextDocumentIdentifier document, CancellationToken cancellationToken, bool includeDiagnostics = false) =>
			InvokeAsync<WorkspaceDocumentInfo?>(RoslynProtocolMethods.DocumentStatus, new { document, includeDiagnostics }, cancellationToken);
		public sealed record ProjectUpdate(LanguageServiceProjectSnapshot snapshot);
		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) != 0) return;
			try { lifetime.Cancel(); }
			finally {
				try { (transport as IDisposable)?.Dispose(); }
				finally { lifetime.Dispose(); }
			}
		}

		async Task<T> InvokeAsync<T>(string method, object arguments, CancellationToken cancellationToken)
		{
			ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
			return await transport.InvokeAsync<T>(method, arguments, linked.Token).ConfigureAwait(false);
		}

		public Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> TextDocumentDefinitionAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyList<NavigationTarget>>>(
				RoslynProtocolMethods.Definition, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<SymbolReferencesResult?>> TextDocumentReferencesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<SymbolReferencesResult?>>(
				RoslynProtocolMethods.References, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<CompletionResult>> TextDocumentCompletionAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<CompletionResult>>(
				RoslynProtocolMethods.Completion, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<QuickInfo?>> TextDocumentHoverAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<QuickInfo?>>(
				RoslynProtocolMethods.Hover, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<IReadOnlyList<LanguageDiagnostic>>> TextDocumentDiagnosticsAsync(
			TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyList<LanguageDiagnostic>>>(
				RoslynProtocolMethods.Diagnostics, new { document }, cancellationToken);

		public Task<ProtocolResult<IReadOnlyList<DocumentOutlineNode>>> TextDocumentSymbolAsync(
			TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyList<DocumentOutlineNode>>>(
				RoslynProtocolMethods.DocumentSymbol, new { document }, cancellationToken);

		public Task<ProtocolResult<IReadOnlyList<TextEdit>>> TextDocumentFormattingAsync(
			TextDocumentIdentifier document, TextSpan? span, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyList<TextEdit>>>(
				RoslynProtocolMethods.Formatting, new { document, span }, cancellationToken);

		public Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> TextDocumentRenameAsync(
			TextDocumentIdentifier document, int offset, string newName, CancellationToken cancellationToken, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false) =>
			InvokeAsync<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>>(
				RoslynProtocolMethods.Rename, new { document, offset, newName, renameOverloads, renameInStrings, renameInComments }, cancellationToken);

		public Task<ProtocolResult<IReadOnlyList<CodeActionInfo>>> TextDocumentCodeActionAsync(
			TextDocumentIdentifier document, TextSpan span, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyList<CodeActionInfo>>>(
				RoslynProtocolMethods.CodeAction, new { document, span }, cancellationToken);

		public Task<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>> CodeActionApplyAsync(
			TextDocumentIdentifier document, string actionId, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>>(
				RoslynProtocolMethods.CodeActionApply, new { document, actionId }, cancellationToken);

		public Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySupertypesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<SymbolHierarchyResult?>>(
				RoslynProtocolMethods.Supertypes, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<SymbolHierarchyResult?>> TypeHierarchySubtypesAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<SymbolHierarchyResult?>>(
				RoslynProtocolMethods.Subtypes, new { document, offset }, cancellationToken);

		public Task RoslynProjectLoadAsync(LanguageServiceProjectSnapshot snapshot, CancellationToken cancellationToken) =>
			InvokeAsync<object?>(RoslynProtocolMethods.ProjectLoad, new ProjectUpdate(snapshot), cancellationToken);

		public Task RoslynSolutionClosedAsync(CancellationToken cancellationToken) =>
			InvokeAsync<object?>(RoslynProtocolMethods.SolutionClosed, new { }, cancellationToken);

		public Task<WorkspaceStatus> RoslynStatusAsync(CancellationToken cancellationToken) =>
			InvokeAsync<WorkspaceStatus>(RoslynProtocolMethods.Status, new { }, cancellationToken);

		public Task<LensDocumentResult> RoslynLensDocumentAsync(TextDocumentIdentifier document, CancellationToken cancellationToken) =>
			InvokeAsync<LensDocumentResult>(RoslynProtocolMethods.LensDocument, new { document }, cancellationToken);

		public Task<ProtocolResult<IReadOnlyList<NavigationTarget>>> RoslynFindMemberAsync(
			string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<IReadOnlyList<NavigationTarget>>>(
				RoslynProtocolMethods.FindMember, new { typeFullName, methodName, parameterCount }, cancellationToken);

		public Task<ProtocolResult<string?>> RoslynHelpKeywordAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<string?>>(
				RoslynProtocolMethods.HelpKeyword, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<string?>> RoslynContainingTypeAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<string?>>(
				RoslynProtocolMethods.ContainingType, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<ExtractInterfaceInfo?>> RoslynExtractInterfaceInfoAsync(
			TextDocumentIdentifier document, int offset, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<ExtractInterfaceInfo?>>(
				RoslynProtocolMethods.ExtractInterfaceInfo, new { document, offset }, cancellationToken);

		public Task<ProtocolResult<ExtractInterfaceResult?>> RoslynExtractInterfaceApplyAsync(
			TextDocumentIdentifier document, int offset, string interfaceName, IReadOnlyList<string> memberIds,
			bool addInterfaceToClass, bool includeComments, CancellationToken cancellationToken) =>
			InvokeAsync<ProtocolResult<ExtractInterfaceResult?>>(
				RoslynProtocolMethods.ExtractInterfaceApply,
				new { document, offset, interfaceName, memberIds, addInterfaceToClass, includeComments }, cancellationToken);
	}
}
