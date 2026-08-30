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