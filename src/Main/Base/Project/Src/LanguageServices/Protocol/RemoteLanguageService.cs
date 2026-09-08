#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices.Protocol
{
    /// <summary>IDE-facing service backed exclusively by one remote workspace.</summary>
    public sealed class RemoteLanguageService : ILanguageService, IDisposable
    {
        readonly object sync = new();
        readonly Dictionary<DocumentId, string> buffers = new();
        readonly Dictionary<DocumentId, WorkspaceDocumentInfo> states = new();
        readonly Func<DocumentId, CancellationToken, Task> refreshProject;
        Task updates = Task.CompletedTask;
        long workspaceRevision;
        bool disposed;
        public IRoslynLanguageProtocol Protocol { get; }

        public RemoteLanguageService(IRoslynLanguageProtocol protocol, Func<DocumentId, CancellationToken, Task> refreshProject)
        {
            Protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            this.refreshProject = refreshProject ?? throw new ArgumentNullException(nameof(refreshProject));
        }

        static TextDocumentIdentifier Document(DocumentId id) => new(id.FileName, id.TargetFramework);
        public WorkspaceDocumentInfo? GetWorkspaceDocumentInfo(DocumentId id)
        {
            lock (sync) return states.TryGetValue(id, out var state) ? state : null;
        }
        public DocumentReadiness GetDocumentReadiness(DocumentId id) => GetWorkspaceDocumentInfo(id)?.Readiness ?? DocumentReadiness.Unknown;
        public long GetWorkspaceRevision() => Interlocked.Read(ref workspaceRevision);

        public Task UpsertDocumentAsync(DocumentId id, string text, CancellationToken token)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Interlocked.Increment(ref workspaceRevision);
                buffers[new DocumentId(id.FileName)] = text;
                foreach (var key in new List<DocumentId>(states.Keys))
                    if (StringComparer.OrdinalIgnoreCase.Equals(key.FileName, id.FileName)) states.Remove(key);
                return updates = SendUpdateAsync(updates, id, text, token);
            }
        }

        public Task LoadProjectAsync(LanguageServiceProjectSnapshot snapshot, CancellationToken token)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Interlocked.Increment(ref workspaceRevision);
                return updates = LoadProjectCoreAsync(updates, snapshot, token);
            }
        }

        async Task LoadProjectCoreAsync(Task previous, LanguageServiceProjectSnapshot snapshot, CancellationToken token)
        {
            try { await previous.ConfigureAwait(false); } catch { }
            await Protocol.RoslynProjectLoadAsync(snapshot, token).ConfigureAwait(false);
            KeyValuePair<DocumentId, string>[] current;
            lock (sync)
            {
                current = new List<KeyValuePair<DocumentId, string>>(buffers).ToArray();
                states.Clear();
            }
            // Project preparation reads disk. Restore unsaved buffers after it, in the same
            // ordered stream used by editor updates and solution close.
            foreach (var buffer in current)
                await SendUpdateAsync(Task.CompletedTask, buffer.Key, buffer.Value, token).ConfigureAwait(false);
        }

        public Task CloseSolutionAsync(CancellationToken token)
        {
            lock (sync)
            {
                buffers.Clear();
                states.Clear();
                Interlocked.Increment(ref workspaceRevision);
                return updates = CloseSolutionCoreAsync(updates, token);
            }
        }

        async Task CloseSolutionCoreAsync(Task previous, CancellationToken token)
        {
            try { await previous.ConfigureAwait(false); } catch { }
            await Protocol.RoslynSolutionClosedAsync(token).ConfigureAwait(false);
        }

        async Task SendUpdateAsync(Task previous, DocumentId id, string text, CancellationToken token)
        {
            // A failed update must be observed by its caller, but must not poison all subsequent
            // full-text updates. Recovery retains the latest parent-owned text for replay.
            try { await previous.ConfigureAwait(false); } catch { }
            await Protocol.TextDocumentDidChangeAsync(Document(id), text, token).ConfigureAwait(false);
            var state = await Protocol.RoslynDocumentStatusAsync(Document(id), token).ConfigureAwait(false);
            lock (sync)
                if (!disposed && state != null && buffers.TryGetValue(new DocumentId(id.FileName), out var current) && current == text)
                    states[id] = state;
        }

        async Task<T> Query<T>(DocumentId id, Func<Task<ProtocolResult<T>>> request)
        {
            Task pending;
            lock (sync) pending = updates;
            await pending.ConfigureAwait(false);
            var result = await request().ConfigureAwait(false);
            lock (sync)
            {
                if (states.TryGetValue(id, out var state)) states[id] = state with { Readiness = result.Readiness };
            }
            return result.Value;
        }

        public void OnTextChanged(DocumentId id, TextChange change)
        {
            Task pending;
            lock (sync)
            {
                if (!buffers.TryGetValue(new DocumentId(id.FileName), out var text)) return;
                int start = Offset(text, change.Span.Start), end = Offset(text, change.Span.End);
                pending = UpsertDocumentAsync(id, text.Substring(0, start) + change.NewText + text.Substring(end), CancellationToken.None);
            }
            _ = ObserveUpdateAsync(pending);
        }

        static async Task ObserveUpdateAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex) { ICSharpCode.Core.LoggingService.Warn("Roslyn document update failed: " + ex.Message); }
        }

        static int Offset(string text, TextPosition position)
        {
            int offset = 0;
            for (int line = 1; line < position.Line; line++)
            {
                while (offset < text.Length && text[offset] != '\r' && text[offset] != '\n') offset++;
                if (offset == text.Length) throw new ArgumentOutOfRangeException(nameof(position));
                if (text[offset++] == '\r' && offset < text.Length && text[offset] == '\n') offset++;
            }
            int end = offset;
            while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
            if (position.Column - 1 > end - offset) throw new ArgumentOutOfRangeException(nameof(position));
            return offset + position.Column - 1;
        }

        public Task<CompletionResult> GetCompletionsAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.TextDocumentCompletionAsync(Document(id), offset, token));
        public Task<QuickInfo?> GetQuickInfoAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.TextDocumentHoverAsync(Document(id), offset, token));
        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId id, CancellationToken token) => Query(id, () => Protocol.TextDocumentDiagnosticsAsync(Document(id), token));
        public Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.TextDocumentDefinitionAsync(Document(id), offset, token));
        public Task<SymbolReferencesResult?> FindReferencesAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.TextDocumentReferencesAsync(Document(id), offset, token));
        public Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId id, TextSpan? span, CancellationToken token) => Query(id, () => Protocol.TextDocumentFormattingAsync(Document(id), span, token));
        public Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId id, CancellationToken token) => Query(id, () => Protocol.TextDocumentSymbolAsync(Document(id), token));
        public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(DocumentId id, int offset, string newName, CancellationToken token, bool renameOverloads = false, bool renameInStrings = false, bool renameInComments = false) => Query(id, () => Protocol.TextDocumentRenameAsync(Document(id), offset, newName, token, renameOverloads, renameInStrings, renameInComments));
        public Task<string?> GetSymbolNameAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.RoslynSymbolNameAsync(Document(id), offset, token));
        public Task<SymbolKindInfo?> GetSymbolKindAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.RoslynSymbolKindAsync(Document(id), offset, token));
        public Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(DocumentId id, CancellationToken token) => Query(id, () => Protocol.TextDocumentSemanticTokensAsync(Document(id), token));
        public Task<bool> IsValidIdentifierAsync(DocumentId id, string name, CancellationToken token) => Protocol.RoslynValidIdentifierAsync(Document(id), name, token);
        public Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId id, TextSpan span, CancellationToken token) => Query(id, () => Protocol.TextDocumentCodeActionAsync(Document(id), span, token));
        public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(DocumentId id, string actionId, CancellationToken token) => Query(id, () => Protocol.CodeActionApplyAsync(Document(id), actionId, token));
        public Task<ExtractInterfaceInfo?> GetExtractInterfaceInfoAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.RoslynExtractInterfaceInfoAsync(Document(id), offset, token));
        public Task<ExtractInterfaceResult?> ExtractInterfaceAsync(DocumentId id, int offset, string interfaceName, IReadOnlyList<string> memberIds, bool addInterfaceToClass, bool includeComments, CancellationToken token) => Query(id, () => Protocol.RoslynExtractInterfaceApplyAsync(Document(id), offset, interfaceName, memberIds, addInterfaceToClass, includeComments, token));
        public Task<SymbolHierarchyResult?> GetBaseSymbolsAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.TypeHierarchySupertypesAsync(Document(id), offset, token));
        public Task<SymbolHierarchyResult?> GetDerivedSymbolsAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.TypeHierarchySubtypesAsync(Document(id), offset, token));
        public Task<string?> GetHelpKeywordAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.RoslynHelpKeywordAsync(Document(id), offset, token));
        public Task<string?> GetContainingTypeNameAsync(DocumentId id, int offset, CancellationToken token) => Query(id, () => Protocol.RoslynContainingTypeAsync(Document(id), offset, token));
        public Task RefreshProjectAsync(DocumentId id, CancellationToken token)
        {
            lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
            return refreshProject(id, token);
        }
        public async Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(string typeFullName, string methodName, int? parameterCount, CancellationToken token)
        {
            Task pending;
            lock (sync) pending = updates;
            await pending.ConfigureAwait(false);
            return (await Protocol.RoslynFindMemberAsync(typeFullName, methodName, parameterCount, token).ConfigureAwait(false)).Value;
        }
        public async Task<LensDocumentResult> GetLensDocumentAsync(DocumentId id, CancellationToken token)
        {
            Task pending;
            lock (sync) pending = updates;
            await pending.ConfigureAwait(false);
            return await Protocol.RoslynLensDocumentAsync(Document(id), token).ConfigureAwait(false);
        }
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                buffers.Clear();
                states.Clear();
            }
            (Protocol as IDisposable)?.Dispose();
        }
    }
}
