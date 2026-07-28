# NuGet Package Manager — Port Plan

Goal: a "Manage NuGet Packages" dialog for UnoDevelop (search, install, update, uninstall,
per-project and per-solution), plus (later slice) a PowerShell Package Console pad — ported from
MonoDevelop's NuGet addins, reusing their NuGet.Client-based business logic and rebuilding the UI
natively in WinUI/Uno (their UI is GTK#/Xwt, which cannot be ported as-is).

## 0. Sources

Two submodules, both added this session:

- `externals/monodevelop` (`https://github.com/mono/monodevelop`, shallow clone, depth 1) — the
  main MonoDevelop/Xamarin Studio/VS for Mac IDE. The **core** NuGet addin lives at
  `main/src/addins/MonoDevelop.PackageManagement`. This is where the actual "Manage NuGet
  Packages" dialog and package-operation business logic live.
- `externals/monodevelop-nuget-extensions` (`https://github.com/mrward/monodevelop-nuget-extensions`)
  — **not** a NuGet manager itself; it's an *add-on* to the addin above. Contains a PowerShell
  Package Console pad, multi-project batch install/update/uninstall helpers, and unified-search
  install extras. Useful for a later slice, not the starting point.

**Correction to the initial ask**: "port NuGet manager from monodevelop-nuget-extensions" doesn't
quite work as stated — that repo has no package-browser/install UI to port. The actual manager is
in `externals/monodevelop`. Per user decision, both submodules are in, core manager first,
extensions repo's console/batch-ops as a follow-up slice.

## 1. What's in `MonoDevelop.PackageManagement` (`externals/monodevelop/main/src/addins/MonoDevelop.PackageManagement`)

Roughly 437 non-test `.cs` files across:

| Folder | Files | What it is | Portable? |
| --- | --- | --- | --- |
| `MonoDevelop.PackageManagement/` | 223 | Business logic: solution/project package operations, background operation monitoring, restore, event wiring, NuGet.Client project adapters (`PackageReferenceNuGetProject`, etc.) | Mostly yes — built on `NuGet.PackageManagement`/`NuGet.Protocol`/`NuGet.ProjectManagement` (portable .NET NuGet.Client libraries, no GTK dependency), but wired to MonoDevelop's own `Project`/`Solution`/`IdeApp` types throughout — needs adapting to `IProject`/`ISolution` (this codebase's SharpDevelop-derived project model), not a drop-in. |
| `MonoDevelop.PackageManagement.Gui/` | 22 | The actual dialogs: `ManagePackagesDialog(.UI).cs`, `AddPackageSourceDialog(.UI).cs`, `SelectProjectsDialog(.UI).cs`, `LicenseAcceptanceDialog.cs`, `SolutionClosingDialog(.UI).cs`, plus cell renderers/views | **No** — built with Xwt (MonoDevelop's cross-platform GTK-backed toolkit). No XAML anywhere in this addin. UI must be rebuilt from scratch in WinUI/Uno; only the *shape* (what fields/columns/actions each dialog has) is reusable as a spec, not the code. |
| `MonoDevelop.PackageManagement.Commands/` | 13 | Menu command wiring (`.addin.xml`-style command classes) | Shape reusable, code isn't — this codebase's own AddIn/command system (see `externals/OpenDevelop/doc/technotes/addin-manager.md`) differs from MonoDevelop's `CommandHandler` pattern. |
| `MonoDevelop.PackageManagement.NodeBuilders/` | 11 | Solution Explorer tree nodes for the "Packages" folder/package references | Shape reusable — UnoDevelop already has its own Solution Explorer tree + package-reference dependency nodes from `project-system.md`'s CPS work; this is a second, independent implementation of similar territory, needs reconciling rather than porting wholesale. |
| `NuGet.PackageManagement.UI/` | 28 | MonoDevelop's *own* copy/fork of NuGet's UI view-models (search results, package item view models, install actions) — vendored, not the NuGet.Client NuGet.PackageManagement.UI on NuGet.org | Mostly portable — view-model-shaped, not much Xwt/GTK leakage expected, but needs a read to confirm before assuming. |

## 2. Design direction

Same seam pattern as `language-services.md` and `project-system.md`: reuse the *engine* directly
where it's genuinely portable, rewrite the *host integration* and *UI* natively.

- **Engine**: pull in the real `NuGet.PackageManagement` NuGet package (from nuget.org, not the
  vendored copy) plus its transitive deps (`NuGet.Protocol`, `NuGet.Resolver`, `NuGet.Configuration`,
  `NuGet.Frameworks`, `NuGet.Packaging`, `NuGet.ProjectManagement`) — this is the same approach
  `MonoDevelop.PackageManagement.csproj` itself takes (`PackageReference Include="NuGet.PackageManagement"`).
  Real, current NuGet.Client, not a fork.
- **Project adapter**: implement `NuGet.ProjectManagement.NuGetProject` against `IProject`
  (this codebase's own project abstraction, already MSBuild-evaluated per
  `MSBuildBasedProject` — same integration point `language-services.md` used for Roslyn) instead
  of porting MonoDevelop's `Project`-based adapter. This is new code, informed by (not copied
  from) `MonoDevelop.PackageManagement/PackageReferenceNuGetProject.cs`.
- **UI**: new WinUI dialog(s) under `src/Main/SharpDevelop/AddIns/` or a new `NuGet/` folder,
  matching `ManagePackagesDialog`'s *shape* (installed / browse / updates tabs, search box,
  package list, package details pane, install/uninstall/update buttons, project-selection for
  solution-level operations) but built with WinUI controls, following this codebase's existing
  `ContentDialog` pattern (see `externals/OpenDevelop/doc/technotes/language-services.md` §2.2's rename dialog for a recent
  precedent) or a full pad/window if the dialog doesn't fit `ContentDialog`'s modal-only model
  (`ManagePackagesDialog` in MonoDevelop is a large non-modal-feeling window with tabs + a
  details pane — likely needs to be a `Window`/pad rather than a `ContentDialog`).
- **Solution Explorer integration**: reconcile with the CPS-derived dependency tree from
  `project-system.md` (PackageReference nodes already exist there) rather than porting
  `MonoDevelop.PackageManagement.NodeBuilders` — check for overlap/conflict before adding a
  second package-node implementation.

## 3. Proposed slice order

1. ✅ **DONE** (partial — no `nuget.config`/source resolution yet) — **NuGet.Client engine
   wiring, no UI**. `NuGet.PackageManagement` 7.6.0 (real NuGet.Client from nuget.org, pulls in
   `NuGet.Commands`/`Resolver`/`Protocol`/`Configuration`/`Frameworks`/`Packaging`/
   `ProjectManagement` transitively — no VS-specific or MonoDevelop-forked packages) is
   referenced from `ICSharpCode.SharpDevelop.Uno.csproj`. `UnoNuGetProject`
   (`src/Main/Base/Src/NuGet/UnoNuGetProject.cs`) implements `NuGet.ProjectManagement.NuGetProject`
   over this codebase's own project model: `GetInstalledPackagesAsync` reads already-evaluated
   `PackageReference` items via `IProject.GetItemsOfType(ItemType.PackageReference)` (the same
   data `project-system.md`'s dependency bridge extracts for the Solution Explorer tree — no
   separate NuGet-specific evaluation). `InstallPackageAsync`/`UninstallPackageAsync` are
   `NotSupportedException` stubs (slice 4's job). Following `LanguageServiceProjectSnapshot`'s
   precedent, the constructor takes an already-extracted package list rather than a live
   `IProject` directly, so it's unit-testable without a full MSBuild-evaluated project
   (`UnoNuGetProjectTests.cs`); `FromProject(IProject)` is the thin IProject-specific factory on
   top, mirroring `LanguageServiceProjectSnapshot.FromProject`. **Not yet done**: resolving
   package sources from `NuGet.Configuration.Settings` (`nuget.config` discovery) — needed before
   slice 3 (search) can hit real configured feeds.
2. ✅ **DONE** (pending full-build verification — see status note) — **Read-only "Installed"
   view**: `ManagePackagesDialog.ShowAsync(IProject)`
   (`src/Main/SharpDevelop/AddIns/ManagePackagesDialog.cs`) is a `ContentDialog` listing a
   project's installed packages (Id + normalized version) via slice 1's `UnoNuGetProject`, built
   in pure C# (no XAML file — avoids the `Binding.StringFormat`/property-on-wrong-element class
   of error already hit once in `AddInManagerDialog.xaml`). Wired to a new **"Manage NuGet
   Packages..."** entry on the Solution Explorer Project node context menu
   (`ManageNuGetPackagesSolutionExplorerCommand` in `SolutionExplorerAddInCommands.cs`,
   registered in `UnoDevelop.Explorer.addin`). No solution-level aggregation yet — one project at
   a time, matching slice 1's per-project adapter. **Status note**: written while unrelated,
   actively-in-progress T4/TextTemplating work in the same tree had `SharpDevelop.csproj`
   mid-broken; this dialog's own files showed no compiler errors, but a full build+test pass
   confirming it wasn't done — verify before relying on this being test-covered end-to-end.
3. ✅ **DONE (engine only, no "Browse" tab UI yet)** — package source resolution + search.
   `NuGetPackageSourceCatalog.LoadEnabledSources` (`src/Main/Base/Src/NuGet/NuGetPackageSourceCatalog.cs`)
   closes slice 1's deferred gap: it loads `nuget.config` the same way `dotnet`/VS do
   (`NuGet.Configuration.Settings.LoadDefaultSettings` walking up from a project/solution
   directory), filtering to enabled sources. `NuGetPackageSearchService`
   (`NuGetPackageSearchService.cs`) searches every given source via the real
   `NuGet.Protocol.PackageSearchResource` (not a hand-rolled call against the NuGet v3 API),
   deduplicating by package id across sources and skipping (with a logged warning, not a thrown
   exception) any source that fails to respond — one bad feed doesn't block the others. Both
   pieces were verified against real inputs outside the normal test project (see status note):
   `LoadEnabledSources` against a temp `nuget.config` with an explicitly disabled source (correctly
   excluded), and `NuGetPackageSearchService` against **real nuget.org** searching
   "Newtonsoft.Json" (returned 5 real results with live download counts). No "Browse" tab UI yet
   — that's `SharpDevelop.csproj` work, deferred until the unrelated T4 build blocker (below)
   clears. `NuGetPackageSourceCatalogTests.cs` is written and should pass once that unblocks.
   **Status note**: `SharpDevelop.csproj` (and therefore the test project, which references it)
   has been broken for several turns by unrelated, actively-in-progress T4/TextTemplating work
   (`TemplatingAppDomainRecycler`, a .NET-Framework-only AppDomain/Remoting API with no .NET
   Core equivalent — looks like a genuinely unfinished port, not a quick fix). Per direction,
   NuGet work stayed scoped to `ICSharpCode.SharpDevelop.Uno.csproj` (which builds cleanly) and
   verified new engine pieces with standalone throwaway harnesses instead of the normal test
   project.
4. **Install / uninstall** — the actual `NuGetPackageManager.InstallPackageAsync`/
   `UninstallPackageAsync` calls against the slice-1 adapter, writing back to the project file
   and triggering a restore.
5. **Update** — diff installed vs. latest/matching-range from search, surface an "Updates" tab.
6. **Multi-project / solution-level operations** — apply an install/update/uninstall across
   multiple selected projects in one step (this is exactly
   `monodevelop-nuget-extensions`' "batch operations for multiple projects" feature — port the
   *logic* from there once the single-project path from slices 1-5 works).
7. **Package sources management** — port `AddPackageSourceDialog`'s shape (add/remove/reorder
   NuGet feeds, credentials) as a WinUI options panel (this codebase already has an options-panel
   pattern — see `externals/OpenDevelop/doc/technotes/addin-manager.md`'s "Adding an Option Panel" section).
8. **PowerShell Package Console pad** (from `monodevelop-nuget-extensions`, not `monodevelop`) —
   last, since it's the most isolated feature (a terminal-style pad) and has the least dependency
   on the dialog UI from slices 1-7.

Deferred / open questions, not yet scoped:

- **EnvDTE / scripting support**: `monodevelop-nuget-extensions` ships `MonoDevelop.EnvDTE` (a
  Visual Studio automation-model shim) purely to support NuGet's `install.ps1`/`uninstall.ps1`
  PowerShell package scripts. Whether UnoDevelop needs this depends on whether slice 8's console
  needs to run legacy packages with install scripts — likely a small minority of packages today
  (most have moved to MSBuild targets/props), so may not be worth porting at all.
- **Solution Explorer node reconciliation** (§2) — needs its own investigation pass before slice
  6/7, to avoid two competing "Packages" tree implementations.
- **Licensing**: `externals/monodevelop` is MIT-licensed (matches this codebase's existing
  precedent of linking/porting from other MIT-licensed IDEs like SharpDevelop) — confirm the
  specific files touched carry compatible license headers before porting verbatim code, same
  diligence already applied to `externals/SharpDevelop`.
