# CodeLens-style Reference/Implementation Counts for AvalonEdit

## Status: plan only, not started

Deferred idea from `doc/technotes/roslyn.md` session work — revisit after the VB Roslyn work
(cross-language go-to-definition check, VB snippets) settles.

## What it is

VS/VS Code's CodeLens: a small, clickable annotation rendered *above* a type/method/property
declaration's line (e.g. "3 references | 1 implementation"), clicking it shows/navigates the
results. Two independent pieces:

1. **Data**: reference/implementation counts per symbol.
2. **Rendering**: an inline annotation above the declaration's line, in the text view.

## Piece 1 — data: already free

`RoslynWorkspaceHelper.GetSolution()` (`src/Main/Base/Project/Roslyn/RoslynWorkspaceHelper.cs`) —
the same workspace `RoslynParser`/`RoslynCodeCompletionBinding` already use for both C# and VB
(see `doc/technotes/roslyn.md`) — already exposes everything needed via
`Microsoft.CodeAnalysis.FindSymbols.SymbolFinder`:

- `SymbolFinder.FindReferencesAsync(symbol, solution)` → reference count.
- `SymbolFinder.FindImplementationsAsync`/`FindDerivedClassesAsync`/`FindOverridesAsync` →
  implementation/override count (the same APIs `FindDerivedClassesOrOverrides.cs` already calls,
  per `csharp-roslyn.md` Phase 0).

No new infrastructure needed here; this piece is a thin wrapper around calls the codebase already
makes elsewhere, generic across C#/VB since neither call is language-specific.

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
  every scroll/keystroke would be the wrong default. `RoslynWorkspaceHelper` already has a
  `dirtyProjects`/incremental-sync story (`doc/technotes/csharp-roslyn.md`'s "Incremental workspace
  updates" note) to build on: only recompute counts for declarations whose containing file changed,
  cache the rest.
- **Scope of "declaration"**: decide up front whether CodeLens applies to every member (VS's
  default) or just types/methods (cheaper, less visual noise) — affects how many
  `FindReferencesAsync` calls happen per file.
- **VB parity**: since Piece 1 is already language-neutral (Roslyn `ISymbol`/`SymbolFinder`), a
  CodeLens implementation gets VB support for free the same way completion/go-to-definition did -
  worth keeping in mind as a design constraint (don't accidentally hardcode a C#-only declaration
  finder when scanning visible lines for candidate symbols).
- **Not started**: no code, no fixture, no test exists for this yet - this file is planning only.
