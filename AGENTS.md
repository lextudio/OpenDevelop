# Instructions for OpenDevelop

## DevFlow Usage

### Build and Test API Guide

Use these actions for build → test workflows. All actions are invoked via `POST /api/v1/invoke/actions/{name}`.

#### Build

```
od.build-solution → { success, result, errorCount, warningCount, diagnostics[], buildLog }
```

- Returns `result: "Success"|"Error"|"Cancelled"`, structured `diagnostics` array, and raw `buildLog` text.
- Parse the JSON response; do **not** treat empty/silent output as success.
- Use `od.output-text("Build")` to get the raw build log separately if needed.

#### Test Execution

```
od.unit-test.run → { started, completed, timedOut, passed, failed, skipped, failedTests[] }
```

- **Always use this** instead of `od.unit-test.run-start` + polling. It waits for completion and returns pass/fail/skip counts plus `failedTests` array with display names.
- Default timeout is 120 seconds; pass `timeoutSeconds` to extend.

```
od.unit-test.run-failed → { started, completed, reranCount, passed, failed, skipped, failedTests[] }
```

- Reruns only the tests that failed in the last run. Useful for debugging without re-running the full suite.

```
od.unit-test.run-start → { started }
```

- Starts tests without waiting. **Avoid** this for simple build→test→verify workflows; use `od.unit-test.run` instead.

#### Test Inspection

```
od.unit-test.tree → { available, tests[] }
```

- Returns the full test tree with `displayName`, `result`, `type`, `nestedTests` for each node.
- Use to find specific test names or check status of individual tests.

```
od.unit-test.output → { category, text }
```

- Returns the full UnitTesting output pad text (prose log of the test run).
- Useful for extracting detailed error messages not available in the tree.

#### Recommended Build→Test Workflow

```bash
# 1. Build and check result
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.build-solution \
  -H "Content-Type: application/json" -d '{"args":[]}'
# Parse JSON: check result=="Success", errorCount==0

# 2. Run tests and get results
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.unit-test.run \
  -H "Content-Type: application/json" -d '{"args":[]}'
# Parse JSON: check completed==true, failed==0, failedTests is empty

# 3. If tests failed, get detailed output
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.unit-test.output \
  -H "Content-Type: application/json" -d '{"args":[]}'
# Parse JSON: extract text field, search for error details

# 4. Optionally rerun only failed tests
curl -s -X POST http://localhost:9299/api/v1/invoke/actions/od.unit-test.run-failed \
  -H "Content-Type: application/json" -d '{"args":[]}'
```

### DevFlow UI Verification

- Agent/API readiness does not mean WPF/AvalonDock has completed layout and arrange. Never diagnose a startup layout problem from a single early bounds sample.
- Locate the target control by content or text, then walk its actual structural ancestor chain to the owning `LayoutAnchorablePaneControl`. Do not query every control of that type and infer ownership from coordinates or list order.
- Distinguish the tab/header bounds, selected-content bounds, and whole-pane bounds. A roughly 25 px tab row is not evidence that the pane itself is 25 px tall.
- Treat layout bounds as stable only after the same target pane reports consistent bounds in multiple consecutive samples. If visual observation conflicts with DevFlow output, first re-check sampling time and node ancestry before drawing a conclusion.
- Prefer semantic DevFlow actions (live layout model, visibility, selection, pane position) as corroborating evidence, but do not substitute a generic side/position result for the target pane's measured visual bounds.
- Do not call screenshot endpoints or trigger operating-system screenshots during test or diagnostic runs unless the user explicitly requests a screenshot.

### Drag/Drop and Pointer Input Debugging

Drag/drop and resize gestures are the most fragile integration-test surface. Every step must be verified individually — a single missed detail causes silent failure.

#### Checklist for every press/drag-move/release test

1. **Start point (press)** — Screen coordinates of the pointer-down. Verify against the live geometry probe (`surface-geometry`, `query-control-screen-bounds`, `query-element-screen-bounds`). Do NOT hardcode coordinates; always read them from the actual rendered bounds at the moment of the press.

2. **Mouse button state** — After `PressPointerAsync`, the button is held. Verify `IsMouseCaptured` or equivalent in the target control. If the capture was stolen by a ScrollViewer or parent, the downstream handlers never fire.

3. **Hit-target check** — Many controls gate drag on a hit test (`IsOverResizeHitTarget`, `IsOverMoveTarget`). The hit test compares the press point against the control's centre in a specific coordinate space (often root canvas via `TranslatePoint`). A mismatch between the probe's reported position and the WPF visual tree position causes a miss. Log both the input point and the resolved centre.

4. **Drag-move steps** — Each `DragMovePointerAsync` injects a mouse-move with the button held. The step count, step delay, and coordinate progression must be granular enough for the control to accumulate visible deltas. Too few steps or too large jumps can cause the WPF Thumb's `DragDelta` to report zero change.

5. **Release point** — The final pointer-up position. Verify that `ReleasePointerAsync` actually releases the button; a mismatched coordinate (e.g. releasing at the start point instead of the end point) resets the gesture.

6. **Event routing** — Two mutually exclusive paths exist for resize/move:
   - **Thumb path**: `DragStarted` → `DragDelta` → `DragCompleted`. Canceled if focus/capture is lost.
   - **PreviewMouse path**: `PreviewMouseLeftButtonDown` → `PreviewMouseMove` → `PreviewMouseLeftButtonUp`. Always fires if `IsOverResizeHitTarget` passes and mouse is captured.
   - A ScrollViewer can swallow bubbling events, making only the Preview path viable. Log which path fired.

7. **BoundsChanged → RPC round-trip** — After the gesture completes, `BoundsChanged` fires and the client calls `SetBoundsAsync`. Log the values sent and the values returned by the server. A stale `rootDesignSize` or `Sequence` number causes the adorner to revert.

8. **Show() early-return guard** — `RemoteFormsDesignerControl.Show()` skips rendering if `state.Render.Sequence <= lastFrameSequence`. If the server returns a stale sequence, the visual never updates and the resize appears to revert.

9. **UpdateAdorners reset** — After `Show()`, `UpdateAdorners()` resets `dragWidth`/`dragHeight` from the returned state's component dimensions. If the server returned the wrong width/height, the adorner immediately reverts to the old size.

10. **Coordinate space audit** — Three coordinate spaces coexist: screen coords (pointer API), surface coords (WPF canvas after zoom), and design coords (the form's logical pixels). A conversion error in any direction produces a press that hits empty canvas. Log all three at each step.

#### Debug logging

Use `ResizeDebugLog` (writes to `%TEMP%\opendevelop-resize-debug.log`) with timestamps and thread IDs. Instrument:
- PreviewLButtonDown: pass point, thumb centre, hit-test result
- PreviewMouseMove: accumulated dragW/dragH and pointer delta
- PreviewLButtonUp: whether it fired
- ThumbDragStarted/Completed: `e.Canceled`, `resizingDrag`, `previewResizeDrag`
- CompletePreviewResizeDrag: `renderedSelection`, committed width/height
- Show(): skip reasons (no render, stale sequence)
- UpdateAdorners: reset values from state
- SurfaceGeometry: frame, selection, handle, selected name
- Server-side SetBounds: element, dimensions, returned state
- Test-side: before/after frame bounds, poll results