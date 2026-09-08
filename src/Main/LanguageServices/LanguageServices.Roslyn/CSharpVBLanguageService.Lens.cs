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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace ICSharpCode.SharpDevelop.LanguageServices.Roslyn
{
	partial class CSharpVBLanguageService
	{
		/// <summary>
		/// Every lens anchor of a document and its counts, resolved from ONE compilation.
		///
		/// The per-anchor path this replaces made a separate document load, semantic model and
		/// symbol lookup for each declaration, so a file with eight declarations did that work
		/// eight times over - and, worse, each resolution independently raced workspace loading, so
		/// some anchors could be answered against a warm workspace and others against a cold one
		/// and then cached (openlens.md §13.2; roslyn-host-process.md §6).
		///
		/// Here the document, semantic model and solution are obtained once and shared, and the
		/// single <see cref="LensDocumentResult.Readiness"/> describes the whole set, so a caller
		/// can no longer cache a half-warm answer.
		/// </summary>
		public async Task<LensDocumentResult> GetLensDocumentAsync(DocumentId documentId, CancellationToken cancellationToken)
		{
			var readiness = GetDocumentReadiness(documentId);
			var empty = new LensDocumentResult(readiness, Array.Empty<LensAnchorResult>());

			var document = await GetOrLoadDocumentAsync(documentId, cancellationToken);
			if (document is null)
				return empty;

			var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
			if (semanticModel is null)
				return empty;

			// The outline already decides what counts as a lensable declaration and what kind of
			// second lens (implementations vs overrides) each one gets; reusing it keeps the two
			// from drifting apart.
			var outline = await GetDocumentOutlineAsync(documentId, cancellationToken);
			if (outline.Count == 0)
				return empty;

			var solution = document.Project.Solution;
			var anchors = new List<LensAnchorResult>();
			foreach (var node in Flatten(outline))
			{
				cancellationToken.ThrowIfCancellationRequested();
				var anchor = await ResolveAnchorAsync(node, semanticModel, solution, cancellationToken);
				if (anchor != null)
					anchors.Add(anchor);
			}
			return new LensDocumentResult(readiness, anchors);
		}

		static IEnumerable<DocumentOutlineNode> Flatten(IReadOnlyList<DocumentOutlineNode> nodes)
		{
			foreach (var node in nodes)
			{
				yield return node;
				if (node.Children.Count > 0)
				{
					foreach (var child in Flatten(node.Children))
						yield return child;
				}
			}
		}

		async Task<LensAnchorResult?> ResolveAnchorAsync(
			DocumentOutlineNode node, SemanticModel semanticModel, Solution solution, CancellationToken cancellationToken)
		{
			var sourceText = await semanticModel.SyntaxTree.GetTextAsync(cancellationToken);
			int offset = ToOffset(sourceText, node.Span.Start);
			if (offset < 0 || offset > sourceText.Length)
				return null;

			ISymbol? symbol;
			try
			{
				symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, offset, _workspace, cancellationToken);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				System.Diagnostics.Trace.WriteLine("Lens: symbol lookup failed for '" + node.Name + "'. " + ex.Message);
				return null;
			}
			if (symbol is null)
				return null;

			int referenceCount = await CountReferencesAsync(symbol, solution, cancellationToken);
			int implementationCount = -1;
			int overrideCount = -1;
			// Only ask for the hierarchy the outline says this symbol actually offers - an
			// interface member has implementations, a virtual member has overrides, and nothing
			// else has either. Asking for both would double the searches for no result.
			if (node.Overridability == SymbolOverridability.Implementable)
				implementationCount = await CountImplementationsAsync(symbol, solution, cancellationToken);
			else if (node.Overridability == SymbolOverridability.Overridable)
				overrideCount = await CountOverridesAsync(symbol, solution, cancellationToken);

			return new LensAnchorResult(
				AnchorId: symbol.GetDocumentationCommentId() ?? (node.Name + "@" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture)),
				Range: node.Span,
				DisplayName: node.Name,
				SymbolKey: symbol.GetDocumentationCommentId(),
				Overridability: node.Overridability,
				ReferenceCount: referenceCount,
				ImplementationCount: implementationCount,
				OverrideCount: overrideCount);
		}

		static async Task<int> CountReferencesAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
		{
			try
			{
				var found = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);
				return found.SelectMany(r => r.Locations).Count(r => r.Location.IsInSource);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				System.Diagnostics.Trace.WriteLine("Lens: reference search failed for '" + symbol.Name + "'. " + ex.Message);
				return -1;
			}
		}

		static async Task<int> CountImplementationsAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
		{
			try
			{
				if (symbol is INamedTypeSymbol type)
					return (await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: cancellationToken)).Count();
				return (await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken)).Count();
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				System.Diagnostics.Trace.WriteLine("Lens: implementation search failed for '" + symbol.Name + "'. " + ex.Message);
				return -1;
			}
		}

		static async Task<int> CountOverridesAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
		{
			try
			{
				return (await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken)).Count();
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				System.Diagnostics.Trace.WriteLine("Lens: override search failed for '" + symbol.Name + "'. " + ex.Message);
				return -1;
			}
		}

		static int ToOffset(Microsoft.CodeAnalysis.Text.SourceText sourceText, TextPosition position)
		{
			int line = Math.Max(0, position.Line - 1);
			if (line >= sourceText.Lines.Count)
				return -1;
			var textLine = sourceText.Lines[line];
			return Math.Min(textLine.Start + Math.Max(0, position.Column - 1), textLine.End);
		}
	}
}
