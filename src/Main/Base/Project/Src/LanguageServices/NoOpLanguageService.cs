using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices
{
    public sealed class NoOpLanguageService : ILanguageService
    {
        public static NoOpLanguageService Instance { get; } = new();

        NoOpLanguageService()
        {
        }

        public Task<CompletionResult> GetCompletionsAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CompletionResult.Empty);
        }

        public Task<QuickInfo?> GetQuickInfoAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<QuickInfo?>(null);
        }

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());
        }

        public Task<IReadOnlyList<NavigationTarget>> GoToDefinitionAsync(DocumentId documentId, int offset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());
        }

        public Task<IReadOnlyList<TextEdit>> FormatAsync(DocumentId documentId, TextSpan? span, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<TextEdit>>(Array.Empty<TextEdit>());
        }

        public void OnTextChanged(DocumentId documentId, TextChange change)
        {
        }

        public Task<IReadOnlyList<DocumentOutlineNode>> GetDocumentOutlineAsync(DocumentId documentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DocumentOutlineNode>>(Array.Empty<DocumentOutlineNode>());
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> RenameSymbolAsync(
            DocumentId documentId, int offset, string newName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(
                new Dictionary<string, IReadOnlyList<TextEdit>>());
        }

        public Task<IReadOnlyList<NavigationTarget>> FindMemberAsync(
            string typeFullName, string methodName, int? parameterCount, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<NavigationTarget>>(Array.Empty<NavigationTarget>());
        }

        public Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<CodeActionInfo>>(Array.Empty<CodeActionInfo>());
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(
            DocumentId documentId, string actionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>>(
                new Dictionary<string, IReadOnlyList<TextEdit>>());
        }
    }
}
