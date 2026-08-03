#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.SharpDevelop.LanguageServices.OpenLens
{
	/// <summary>
	/// Generic <see cref="ILanguageService"/>-backed anchor provider, scoped to one file extension by
	/// whichever AddIn constructs and registers it (doc/technotes/codelens.md §17.1) - CSharpBinding
	/// registers one instance for ".cs", VBBinding a separate instance for ".vb", mirroring how
	/// RoslynCodeCompletionBinding-style classes are shared implementations registered per language
	/// AddIn rather than a single cross-language registration.
	///
	/// Anchor discovery is treated as cheap (doc §8.2/§12.1): this provider recomputes the full list
	/// from <see cref="ILanguageService.GetDocumentOutlineAsync"/> on every call rather than
	/// incrementally patching a previous result. Caching which anchors still need lens *resolution*
	/// is the OpenLens host's job, keyed by <see cref="OpenLensAnchor.AnchorId"/>, not this provider's.
	/// </summary>
	public sealed class LanguageOpenLensAnchorProvider : IOpenLensAnchorProvider
	{
		readonly string extension;

		public LanguageOpenLensAnchorProvider(string id, string extension)
		{
			Id = id ?? throw new ArgumentNullException(nameof(id));
			this.extension = extension ?? throw new ArgumentNullException(nameof(extension));
		}

		public string Id { get; }

		public bool CanHandle(OpenLensDocumentContext context) =>
			string.Equals(Path.GetExtension(context.FileName), extension, StringComparison.OrdinalIgnoreCase);

		public async Task<IReadOnlyList<OpenLensAnchor>> GetAnchorsAsync(
			OpenLensDocumentContext context, OpenLensRange? requestedRange, CancellationToken cancellationToken)
		{
			var registry = SD.GetService<LanguageServiceRegistry>();
			if (registry == null || !registry.TryGetService(context.FileName, out var languageService))
				return Array.Empty<OpenLensAnchor>();

			var outline = await languageService.GetDocumentOutlineAsync(context.DocumentId, cancellationToken).ConfigureAwait(false);

			var results = new List<OpenLensAnchor>();
			foreach (var type in outline) {
				AddAnchor(results, context, type, symbolKey: type.Name);
				foreach (var member in type.Children)
					AddAnchor(results, context, member, symbolKey: type.Name + "." + member.Name);
			}
			return results;
		}

		// SymbolKey is "Type.Member" (or just "Type" for a type anchor) - not namespace-qualified,
		// so two same-named types in different namespaces within one file would collide. A provider
		// that cross-references by name (e.g. a coverage or test-status lens matching this file's
		// anchors against a separately-discovered result set keyed by type/method name) accepts that
		// same tradeoff doc §17.2 already accepts for reference counts; getting a true assembly-
		// qualified name would mean threading the type's namespace through from Roslyn, which
		// DocumentOutlineNode doesn't carry today.
		static void AddAnchor(List<OpenLensAnchor> results, OpenLensDocumentContext context, DocumentOutlineNode declaration, string symbolKey)
		{
			results.Add(new OpenLensAnchor(
				AnchorId: declaration.Kind + ":" + declaration.Name,
				DocumentId: context.DocumentId,
				Range: new OpenLensRange(declaration.Span),
				Kind: ToAnchorKind(declaration.Kind),
				DisplayName: declaration.Name,
				SymbolKey: symbolKey,
				DocumentVersion: context.DocumentVersion,
				Overridability: declaration.Overridability));
		}

		static OpenLensAnchorKind ToAnchorKind(string kind) => kind switch {
			"Class" or "Interface" or "Struct" or "Enum" or "Delegate" or "Record" => OpenLensAnchorKind.Type,
			"Method" => OpenLensAnchorKind.Method,
			"Constructor" => OpenLensAnchorKind.Constructor,
			"Property" => OpenLensAnchorKind.Property,
			"Indexer" => OpenLensAnchorKind.Indexer,
			"Event" => OpenLensAnchorKind.Event,
			"Field" => OpenLensAnchorKind.Field,
			_ => OpenLensAnchorKind.Other,
		};
	}
}
