# CodeLens-style Reference/Implementation Counts for AvalonEdit

## Status: in progress

Deferred idea from `doc/technotes/roslyn.md` session work — revisited now that the VB Roslyn work
and the `ILanguageService` unification (`doc/technotes/csharp-vb-binding.md`) have settled. Piece 1
(data) is updated below to go through the unified contract; implementation starting with Piece 1,
then the adorner-based renderer (Piece 2, approach 1).

## What it is

VS/VS Code's CodeLens: a small, clickable annotation rendered *above* a type/method/property
declaration's line (e.g. "3 references | 1 implementation"), clicking it shows/navigates the
results. Two independent pieces:

1. **Data**: reference/implementation counts per symbol.
2. **Rendering**: an inline annotation above the declaration's line, in the text view.

## Piece 1 — data: already free, but go through `ILanguageService`, not `RoslynWorkspaceHelper`

**Updated 2026-08-02** (see `doc/technotes/csharp-vb-binding.md`, `doc/technotes/language-services.md`):
the unified language-service contract has since been built out, and the explicit rule now is that
`RoslynWorkspaceHelper` must not become a long-term API sitting alongside it. This plan's original
"just call `RoslynWorkspaceHelper.GetSolution()` + `SymbolFinder`" data layer would be new debt of
exactly the kind that's being actively removed elsewhere (`RenameSymbolCommand`, `FindReferencesCommand`,
`ExtractInterfaceCommand`, `DefinitionViewPad`, etc. all moved off it for the same reason) - and it
would only ever work for Roslyn-backed languages, not any LSP-backed one, and wouldn't respect
CSharpBinding/VBBinding's enable/disable lifecycle the way every other feature now does.

Use the already-built `ILanguageService` contract instead, obtained the same way every other
feature does - `LanguageServiceRegistry.GetService(fileName)` (or `TryGetService`), keyed by
`DocumentId`, no Roslyn or LSP types crossing into CodeLens code:

- Reference count → `ILanguageService.FindReferencesAsync(documentId, offset, ct)` →
  `SymbolReferencesResult.References.Count`.
- Implementation/override count → `GetDerivedSymbolsAsync`/`GetBaseSymbolsAsync(documentId, offset, ct)`
  → `SymbolHierarchyResult.Nodes.Count`.

This is still effectively "free" - no new contract surface needed, these three methods already
exist and are already exercised by `FindReferencesCommand`/`DeclaringTypeSubMenuBuilder`-adjacent
code. It's also strictly better than the original plan: it's backend-neutral (an LSP-backed
language gets real counts for free the moment its backend implements those two methods, and a
harmless empty result until then, instead of CodeLens needing a Roslyn-only special case), and it
automatically stops computing anything for a disabled language the same way completion/diagnostics/
rename already do - no bespoke enable/disable wiring needed in CodeLens itself.

## Piece 2 — rendering: no built-in AvalonEdit widget, but the right hooks exist

AvalonEdit (the vendored fork at `src/Libraries/AvalonEdit/ICSharpCode.AvalonEdit/`) has no
"annotation line above a declaration" concept out of the box (unlike VS Code's editor, which has
native CodeLens support). The building blocks it *does* have, already used elsewhere in this
codebase for similar "overlay content positioned at a specific line" problems:

- `Rendering/IBackgroundRenderer.cs` / `TextView.cs` (`TextView.VisualLines`, line
  Y-coordinates) — used today by `Search/SearchResultBackgroundRenderer.cs` and
  `Rendering/CurrentLineHighlightRenderer.cs` for per-line background painting.
  `ChangeMarkerMargin.cs` (`src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/ChangeMarkerMargin/`)
  is the closest existing precedent for "compute a screen position from a document line, then
  render a WPF popup/adorner there" (its diff popup does exactly this) — read that file first
  before designing the CodeLens renderer, it's the most directly reusable pattern in-tree.
- `Rendering/VisualLineElementGenerator.cs` (used by `Folding/FoldingElementGenerator.cs`,
  `Rendering/SingleCharacterElementGenerator.cs`) — for inserting inline visual elements *within*
  a line; less directly applicable to an *above-the-line* annotation, but worth checking whether
  inserting a zero-height/collapsed line via a generator is cleaner than a floating adorner.

Two realistic implementation shapes, roughly in increasing order of visual fidelity vs. effort:

1. **Adorner layer approach** (cheapest, closest to `ChangeMarkerMargin`'s existing pattern): a
   `TextView`-layer (`KnownLayer.Background` or similar) that, for each visible declaration line
   (found by walking Roslyn symbols in the visible range, not by re-parsing text), draws a small
   text run just above the line's top Y-coordinate. Click handling via a transparent `Border`/
   `TextBlock` positioned at that point, same idea as the diff popup.
2. **Reserved-line approach** (higher fidelity, matches real CodeLens more closely): actually
   reserve vertical space above qualifying lines (via a custom `VisualLineElementGenerator` or a
   `TextView` line-height transform) so the annotation doesn't overlap text above it. More
   invasive — needs care around scrolling/line-number-margin sync — genuinely more IDE-shaped work,
   not a quick add-on.

Start with (1); only build (2) if the overlap/visual-quality problems from (1) turn out to matter
in practice.

## Cost/risk notes

- **Perf**: computing reference/implementation counts per visible declaration must be
  incremental/cached and off the UI thread — recomputing `FindReferencesAsync` for every symbol on
  every scroll/keystroke would be the wrong default. This caching is the backend's job (e.g.
  `CSharpVBLanguageService`'s own incremental workspace sync), not CodeLens code reaching into
  `RoslynWorkspaceHelper`'s `dirtyProjects` internals directly - CodeLens should just call
  `ILanguageService` per visible declaration and let whichever backend answers own its own caching
  story; at most, CodeLens itself should debounce/cache its own last-computed counts per document
  version so a scroll that reveals no new declarations doesn't re-call at all.
- **Scope of "declaration"**: decide up front whether CodeLens applies to every member (VS's
  default) or just types/methods (cheaper, less visual noise) — affects how many
  `FindReferencesAsync`/`GetDerivedSymbolsAsync` calls happen per file.
- **Language parity**: since Piece 1 is now the same `ILanguageService` contract every other
  feature uses, a CodeLens implementation gets VB support for free (as before), and additionally
  degrades gracefully rather than crashing for any language whose backend hasn't implemented
  `GetDerivedSymbolsAsync`/`GetBaseSymbolsAsync` yet (LSP currently returns null for both) - it
  simply shows a reference count with no implementation count for those, rather than needing a
  hardcoded C#/VB-only declaration finder.
- **Status**: no code, no fixture, no test exists for this yet - implementation starts now.
