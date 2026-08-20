# Design-space gridlines don't render (WPF and WinUI/Uno designers)

Status: **fixed** via the workaround below (`GridlineOverlay` in `Designer.Presentation`, shared by
both designers). The LibreWPF-on-macOS platform gap itself (tiled `DrawingBrush` not rendering) is
still unaddressed - this only routes around it at the app level.

Date: 2026-08-19. Fixed 2026-08-20.

## Symptom

Toggling the shared designer toolbar's grid button (`DesignerCanvas`'s `gridButton`) visibly
switches to the pressed state and does fire `GridRequested`/`SetGridlines(true)`, but no grid
pattern ever appears on the design surface - confirmed by hand in both the WPF designer and the
WinUI/Uno designer (`od.winui-designer.gridlines` reports `gridlines: true`, i.e. the state flag
is set correctly, but the rendered surface stays a plain flat background).

## Root cause

Both `WpfSurfaceDesignerControl.SetGridlines`/`CreateGridBrush` and
`UnoDesignSurfaceControl.SetGridlines`/`CreateGridBrush` implement the grid identically: a
`DrawingBrush` with `TileMode.Tile` (two `GeometryDrawing` line segments per tile, retiled on
zoom via `UpdateGridBrush`), assigned as a `Grid`/`Canvas`'s `Background`.

Confirmed via DevFlow's own `/api/v1/ui/tree` inspection that the wiring is NOT the problem: after
toggling grid on, the overlay element's `Background` genuinely reports as the (non-null)
`DrawingBrush`, with the correct size (matching the rendered frame's bounds exactly) - i.e. the
C# logic runs correctly and assigns real, correctly-sized content. Despite that, a close crop of
the design surface region shows zero grid lines, in both designers.

This points to a platform-level gap: tiled `DrawingBrush` rendering (`TileMode.Tile`) does not
actually paint under this app's LibreWPF-on-macOS host, even though the property assignment
itself succeeds. This is the same general class of native-rendering gap already documented
elsewhere in this codebase (e.g. `RenderTargetBitmap`/`wpfgfx_cor3` not existing on macOS) - a
WPF primitive that depends on a native compositor path LibreWPF doesn't (yet) implement, not a
logic bug in either designer's own code.

## Fix (implemented)

Replaced the tiled-`DrawingBrush` approach with real child `Line` elements in a plain `Canvas`
(no tiling involved at all - `Line` is an ordinary, well-supported WPF shape), as
`GridlineOverlay` in `src/Main/Designer/Designer.Presentation/GridlineOverlay.cs`. Shape actually
implemented (slightly simpler than the original sketch - no separate width/height/cellSize/step
params, `Update` takes the surface size and scale directly):

```csharp
public sealed class GridlineOverlay
{
    const double GridCellSize = 20;

    public Canvas Visual { get; } = new Canvas { IsHitTestVisible = false };

    public void Update(double width, double height, double scale, bool show)
    {
        Visual.Children.Clear();
        if (!show || width <= 0 || height <= 0 || scale <= 0) return;
        var step = GridCellSize * scale;
        if (step < 2) return; // guard against an unbounded line count at extreme zoom-out
        for (var x = 0.0; x <= width; x += step) Visual.Children.Add(CreateLine(x, 0, x, height));
        for (var y = 0.0; y <= height; y += step) Visual.Children.Add(CreateLine(0, y, width, y));
    }
    // CreateLine: new Line { X1, Y1, X2, Y2, Stroke = gray, StrokeThickness = 1 }
}
```

Shared between both designers via `src/Main/Designer/Designer.Presentation/` (alongside
`SelectionAdornerLayer`, which already follows the same "shared drawing-only helper, each backend
keeps its own gesture/state" pattern). Both `WpfSurfaceDesignerControl.cs` and
`UnoDesignSurfaceControl.cs` replaced their own `gridBrush`/`CreateGridBrush`/`UpdateGridBrush`
fields and methods with a `GridlineOverlay` instance, added to their visual tree in place of the
`Grid`/`Canvas` whose `Background` was set to the (non-functional) `DrawingBrush`. On the Uno side,
the overlay's `Visual` is a new canvas added into `viewportCanvas` alongside the pre-existing
`overlay` canvas (which still hosts the selection adorner and no longer touches `Background`).

## Verification plan for the fix

- Live via DevFlow in both designers: toggle grid on, screenshot/crop the design surface region,
  confirm visible grid lines (not just a state flag).
- Check line count stays bounded at extreme zoom levels (the `step < 2` guard above, or similar).
- Confirm gridlines re-tile correctly on zoom/fit changes (existing `UpdateGridBrush`-equivalent
  call sites need to call the new overlay's `Update` instead).
