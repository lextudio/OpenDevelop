#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Host.Mef;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn
{
    // Roslyn-native code fixes (externals/OpenDevelop/doc/technotes/language-services.md §8.3). Split into its own partial-class
    // file since it's a self-contained concern (MEF provider discovery) with its own lifecycle
    // state, not because CSharpVBLanguageService.cs was reorganized.
    public sealed partial class CSharpVBLanguageService
    {
        // Built lazily and kept for this service's lifetime: composing a CompositionHost over
        // every MefHostServices.DefaultAssemblies (the same assembly set the Roslyn Workspace
        // itself composes from, externals/OpenDevelop/doc/technotes/language-services.md §8.3) isn't free, and the set of
        // available CodeFixProviders can't change without a process restart anyway.
        CompositionHost? _codeFixHost;

        public async Task<IReadOnlyList<CodeActionInfo>> GetCodeActionsAsync(DocumentId documentId, TextSpan span, CancellationToken cancellationToken)
        {
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return Array.Empty<CodeActionInfo>();

            var sourceText = await document.GetTextAsync(cancellationToken);
            var roslynSpan = ToRoslynSpan(sourceText, span);
            var actions = await ComputeCodeActionsAsync(document, roslynSpan, cancellationToken);
            return actions.Select(pair => new CodeActionInfo(pair.Key, pair.Value.Title)).ToArray();
        }

        async Task<Dictionary<string, CodeAction>> ComputeCodeActionsAsync(
            Microsoft.CodeAnalysis.Document document, Microsoft.CodeAnalysis.Text.TextSpan roslynSpan,
            CancellationToken cancellationToken)
        {
            var sourceText = await document.GetTextAsync(cancellationToken);
            var diagnostics = await ComputeRoslynDiagnosticsAsync(document, cancellationToken);
            var applicableDiagnostics = diagnostics.Where(d => d.Location.SourceSpan.IntersectsWith(roslynSpan)).ToImmutableArray();

            var registeredActions = new List<CodeAction>();
            foreach (var provider in GetCodeFixProviders(document.Project.Language))
            {
                var providerDiagnostics = applicableDiagnostics
                    .Where(d => provider.FixableDiagnosticIds.Contains(d.Id))
                    .ToImmutableArray();
                if (providerDiagnostics.IsEmpty)
                    continue;

                // One CodeFixContext per distinct diagnostic span - a provider expects every
                // diagnostic passed to a single context to share the same span (that's the
                // contract CodeFixContext documents), which isn't guaranteed across diagnostics
                // from different providers/rules that both happen to touch this range.
                foreach (var diagnosticsAtSpan in providerDiagnostics.GroupBy(d => d.Location.SourceSpan))
                {
                    var context = new CodeFixContext(
                        document,
                        diagnosticsAtSpan.Key,
                        diagnosticsAtSpan.ToImmutableArray(),
                        (action, _) => registeredActions.Add(action),
                        cancellationToken);

                    try
                    {
                        await provider.RegisterCodeFixesAsync(context);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        System.Diagnostics.Trace.TraceWarning($"CodeFixProvider '{provider.GetType().FullName}' threw computing fixes: {ex.Message}");
                    }
                }
            }

            // Content-addressed ids, not array indices (roslyn-host-process.md §5.2).
            //
            // The id used to be the action's position in this list, resolvable only through a
            // dictionary of live Roslyn CodeAction objects. That makes the token meaningless to
            // anyone but the exact process instance that issued it: after a host restart - or
            // simply after the document changed - "3" still resolves to *something*, and applying
            // it silently produces the wrong edit or none at all while reporting success.
            //
            // Encoding the document version and the requested span into the id instead makes a
            // stale token detectable rather than merely wrong, which is what ApplyCodeActionAsync
            // now checks.
            var documentVersion = ComputeDocumentVersion(sourceText);
            var pending = new Dictionary<string, CodeAction>(StringComparer.Ordinal);
            for (var i = 0; i < registeredActions.Count; i++)
            {
                var id = FormatCodeActionId(documentVersion, roslynSpan, registeredActions[i], i);
                pending[id] = registeredActions[i];
            }

            return pending;
        }

        /// <summary>
        /// A stable fingerprint of the document text a code-action id was issued against. Any edit
        /// changes it, which is exactly the condition that must invalidate the id.
        /// </summary>
        static string ComputeDocumentVersion(Microsoft.CodeAnalysis.Text.SourceText sourceText)
        {
            var text = sourceText.ToString();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash, 0, 8);
        }

        /// <summary>
        /// <c>v1|{documentVersion}|{spanStart}:{spanLength}|{equivalenceKeyOrOrdinal}</c>.
        ///
        /// EquivalenceKey is Roslyn's own identity for "the same fix", so where a provider supplies
        /// one the id survives recomputation; the ordinal is only a fallback for providers that do
        /// not, and is still scoped by version and span.
        /// </summary>
        static string FormatCodeActionId(string documentVersion, Microsoft.CodeAnalysis.Text.TextSpan span, CodeAction action, int ordinal)
        {
            var key = string.IsNullOrEmpty(action.EquivalenceKey)
                ? "#" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : action.EquivalenceKey;
            return string.Concat("v1|", documentVersion, "|", span.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ":", span.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", key);
        }

        /// <summary>The document-version field of an id produced by <see cref="FormatCodeActionId"/>.</summary>
        internal static string? TryGetCodeActionIdVersion(string actionId)
        {
            if (string.IsNullOrEmpty(actionId) || !actionId.StartsWith("v1|", StringComparison.Ordinal))
                return null;
            var parts = actionId.Split('|');
            return parts.Length < 4 ? null : parts[1];
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(
            DocumentId documentId, string actionId, CancellationToken cancellationToken)
        {
            var noEdits = new Dictionary<string, IReadOnlyList<TextEdit>>();
            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                throw new StaleCodeActionException(actionId);

            // Refuse a token issued against different text. Without this the cached action is
            // applied to text it was never computed for, and the caller is told it succeeded -
            // the silent breakage §5.2 describes. Failing loudly lets the UI recompute and retry.
            var expectedVersion = TryGetCodeActionIdVersion(actionId);
            var text = await document.GetTextAsync(cancellationToken);
            if (expectedVersion == null || expectedVersion != ComputeDocumentVersion(text))
                throw new StaleCodeActionException(actionId);
            var range = actionId.Split('|')[2].Split(':');
            if (range.Length != 2
                || !int.TryParse(range[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var start)
                || !int.TryParse(range[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length)
                || start > text.Length || length > text.Length - start)
                throw new StaleCodeActionException(actionId);

            // Recompute against this immutable document/solution, never a previous process's
            // object cache. The same token works after replay, but a disappeared fix is stale.
            var actions = await ComputeCodeActionsAsync(document,
                new Microsoft.CodeAnalysis.Text.TextSpan(start, length), cancellationToken);
            if (!actions.TryGetValue(actionId, out var action))
                throw new StaleCodeActionException(actionId);

            ImmutableArray<CodeActionOperation> operations;
            try
            {
                operations = await action.GetOperationsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"CodeAction '{action.Title}' failed to compute its edits: {ex.Message}");
                throw;
            }

            var originalSolution = document.Project.Solution;
            var changedSolution = originalSolution;
            foreach (var operation in operations.OfType<ApplyChangesOperation>())
                changedSolution = operation.ChangedSolution;

            if (ReferenceEquals(changedSolution, originalSolution))
                return noEdits;

            return await DiffSolutionsToTextEditsAsync(originalSolution, changedSolution, cancellationToken);
        }

        /// <summary>
        /// Discovers built-in <see cref="CodeFixProvider"/>s for <paramref name="language"/> via
        /// MEF composition over the same assembly set the Roslyn Workspace itself was built from
        /// (<see cref="MefHostServices.DefaultAssemblies"/>) — Roslyn has no public "get me the
        /// fix providers" API outside VS's own internal <c>CodeFixService</c>. Third-party
        /// analyzer assemblies loaded via <c>AnalyzerFileReference</c> (§2.2) may ship their own
        /// fix providers too, but discovering those needs a second, per-project composition over
        /// each project's analyzer assemblies — not done yet, so only the built-in fixer set is
        /// available today.
        /// </summary>
        IReadOnlyList<CodeFixProvider> GetCodeFixProviders(string language)
        {
            try
            {
                var host = _codeFixHost ??= new ContainerConfiguration()
                    .WithAssemblies(MefHostServices.DefaultAssemblies)
                    .CreateContainer();

                return host.GetExports<CodeFixProvider>()
                    .Where(provider => provider.GetType().GetCustomAttribute<ExportCodeFixProviderAttribute>() is { } export
                        && export.Languages.Contains(language))
                    .ToArray();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Failed to discover Roslyn code fix providers: {ex.Message}");
                return Array.Empty<CodeFixProvider>();
            }
        }
    }
}
