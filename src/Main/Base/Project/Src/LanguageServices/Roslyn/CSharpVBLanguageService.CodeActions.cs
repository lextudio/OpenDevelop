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
using ICSharpCode.Core;

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
                    catch (Exception ex)
                    {
                        LoggingService.Warn($"CodeFixProvider '{provider.GetType().FullName}' threw computing fixes: {ex.Message}");
                    }
                }
            }

            var pending = new Dictionary<string, CodeAction>(StringComparer.Ordinal);
            var results = new List<CodeActionInfo>(registeredActions.Count);
            for (var i = 0; i < registeredActions.Count; i++)
            {
                var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                pending[id] = registeredActions[i];
                results.Add(new CodeActionInfo(id, registeredActions[i].Title));
            }

            _pendingCodeActionsByDocument[documentId] = pending;
            return results;
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<TextEdit>>> ApplyCodeActionAsync(
            DocumentId documentId, string actionId, CancellationToken cancellationToken)
        {
            var noEdits = new Dictionary<string, IReadOnlyList<TextEdit>>();
            if (!_pendingCodeActionsByDocument.TryGetValue(documentId, out var pending) || !pending.TryGetValue(actionId, out var action))
                return noEdits;

            var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
            if (document is null)
                return noEdits;

            ImmutableArray<CodeActionOperation> operations;
            try
            {
                operations = await action.GetOperationsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"CodeAction '{action.Title}' failed to compute its edits: {ex.Message}");
                return noEdits;
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
                LoggingService.Warn($"Failed to discover Roslyn code fix providers: {ex.Message}");
                return Array.Empty<CodeFixProvider>();
            }
        }
    }
}
