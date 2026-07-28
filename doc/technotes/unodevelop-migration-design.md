# UnoDevelop Migration Design Notes

## 1. Migration Objective

Primary objective: migrate toward SharpDevelop with maximum upstream reuse, while keeping the Uno desktop app continuously runnable and visually usable.

Guiding constraints:

- Prefer NuGet packages for UnoDock and UnoEdit, avoid project references.
- Avoid mock-only UI; ship working interactions early.
- Keep build green at each migration batch.
- Move from custom local models toward SharpDevelop contracts and service flows.

## 2. Working Architecture

Current architecture follows a contract-first strategy:

- Core services through `ServiceSingleton.ServiceProvider`.
- Workbench integration via `IWorkbench` and `UnoWorkbenchService`.
- Project system integration via `IProjectService` and `UnoProjectService`.
- UI shell with UnoDock + UnoEdit in `MainPage`.

Why contract-first:

- Upstream WinForms ProjectBrowser UI is expensive to port directly.
- Base/Core interfaces from SharpDevelop can be linked quickly and safely.
- UI can be adapted in Uno while preserving upstream service surface.

## 3. Proven Migration Pattern

The pattern that worked repeatedly:

1. Link upstream interfaces/events/utility types first.
2. Fill unresolved heavy dependencies with minimal local placeholders.
3. Implement runtime behavior behind upstream contracts.
4. Validate with full solution build.
5. Replace placeholders in later slices when dependency graph is clearer.

This keeps velocity high and avoids long blocked branches.

## 4. Key Technical Decisions

### 4.1 Upstream Reuse Strategy

- Reuse upstream files when dependency fan-in is manageable.
- If upstream file drags large AddInTree/WinForms chains, use an upstream-aligned minimal implementation first.
- Preserve public contracts to enable later direct replacement.

### 4.2 Solution Explorer Strategy

- Build tree from `ISolutionItem`/`ISolutionFolder` projections, not concrete local types.
- Store node metadata (kind + bound solution item) for command routing.
- Raise project item events through `IProjectServiceRaiseEvents` to keep explorer refresh event-driven.

### 4.3 UI Delivery Strategy

- Deliver usable operations early: refresh/new/rename/delete/copy/open/set startup.
- Add status and selection feedback so actions are visible.
- Keep iteration cycles short: implement -> build -> adjust.

## 5. Lessons Learned

1. Contract-first migration is the fastest path for SharpDevelop reuse on Uno.
2. Placeholder types are acceptable if they are explicit, minimal, and tracked.
3. Service events are critical; polling/manual refresh quickly becomes a bottleneck.
4. UI scaffolding should be functional from day one; users reject static imitation.
5. Small platform differences (for example WrapPanel spacing support) should be handled pragmatically, not over-designed.

## 6. Anti-Patterns to Avoid

- Large one-shot port attempts of WinForms-heavy upstream UI modules.
- Deep custom local abstractions that diverge from SharpDevelop interfaces.
- Long periods without build verification.
- Silent behavior without explorer status/selection feedback.

## 7. Current Risks

- Some placeholder classes still exist in Base project layer.
- Rename flow still depends on basic message service input behavior.
- NU1903 warning on Microsoft.Build package remains unresolved.

## 8. Next High-Impact Steps

1. Replace remaining placeholder project-model types in priority order.
2. Add right-click context menu command parity for Solution Explorer.
3. Upgrade rename/input workflow to full Uno dialog interaction.
4. Continue reducing MainPage local logic by moving behavior behind service contracts.

## 9. Definition of Done for This Migration Track

The migration is considered successful when:

- Solution Explorer core workflows are feature-complete for daily work.
- Core project/workbench flows run through SharpDevelop-compatible contracts.
- Placeholder types are minimized or replaced with upstream implementations.
- The solution builds cleanly (except explicitly accepted external warnings).