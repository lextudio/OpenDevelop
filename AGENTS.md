# Instructions for OpenDevelop

## DevFlow Usage

### DevFlow UI Verification

- Agent/API readiness does not mean WPF/AvalonDock has completed layout and arrange. Never diagnose a startup layout problem from a single early bounds sample.
- Locate the target control by content or text, then walk its actual structural ancestor chain to the owning `LayoutAnchorablePaneControl`. Do not query every control of that type and infer ownership from coordinates or list order.
- Distinguish the tab/header bounds, selected-content bounds, and whole-pane bounds. A roughly 25 px tab row is not evidence that the pane itself is 25 px tall.
- Treat layout bounds as stable only after the same target pane reports consistent bounds in multiple consecutive samples. If visual observation conflicts with DevFlow output, first re-check sampling time and node ancestry before drawing a conclusion.
- Prefer semantic DevFlow actions (live layout model, visibility, selection, pane position) as corroborating evidence, but do not substitute a generic side/position result for the target pane's measured visual bounds.
- Do not call screenshot endpoints or trigger operating-system screenshots during test or diagnostic runs unless the user explicitly requests a screenshot.