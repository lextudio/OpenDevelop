# NuGet Package Manager — Port Plan

Goal: a "Manage NuGet Packages" dialog for UnoDevelop (search, install, update, uninstall,
per-project and per-solution), plus (later slice) a PowerShell Package Console pad — ported from
MonoDevelop's NuGet addins, reusing their NuGet.Client-based business logic and rebuilding the UI
natively in WinUI/Uno (their UI is GTK#/Xwt, which cannot be ported as-is).

## Status (measured 2026-11-08 against the running IDE)

This plan predates the current add-in. The shipped add-in is the SharpDevelop-era
`src/AddIns/Misc/PackageManagement` (Manage Packages dialog, Restore, Options, sources, the
Package Manager Console hosted as a `pwsh` Terminal session, EnvDTE and Cmdlets), with search on
`NuGet.Protocol` v3. Compared with `MonoDevelop.PackageManagement`, found with DevFlow on a copy of
`tests/fixtures/NuGetFixture` after installing a package from the local feed:

| Area | State |
|---|---|
| Solution Explorer nodes | **Present**: Dependencies > Packages > `<Id> (<version>)`, next to Analyzers and Frameworks. Do not port MonoDevelop's `NodeBuilders`. |
| Project node menu | Has Manage Packages... and Restore Packages. |
| Packages folder / package node menus | The folder has its own `PackagesFolderNode` menu (Manage Packages..., Restore Packages). A package node has Update (opens the Updates tab filtered to that package and switches to its owning project), Manage Packages..., then Remove; `AddInTests.SearchAndInstallPackage_UpdatesProjectFile` invokes the real Update command and verifies its tab and canonical package-id filter. |
| Missing-package quick fix | **Present for unresolved namespaces**: `MissingPackageContextActionProvider` (PackageManagement add-in) reads CS0246/CS0234 at the caret in an SDK-style project, tries the qualified name at the diagnostic and its shorter prefixes as an exact NuGet package id against the active source, and offers \"Install NuGet package 'X' v in 'Project'\" in the light bulb; choosing it writes the PackageReference on the UI thread and runs `dotnet restore` in the background. Covered by `AddInTests.LightBulb_OffersToInstallMissingNuGetPackage_AndInstallsIt` (local feed, no network). Not covered: a bare type name such as `JsonConvert` (no package index to map it), non-SDK projects, and VB. Underneath it, `LanguageServiceContextActionsExtension` puts Roslyn's code actions in the bulb for `.cs` (the bulb had no providers at all because `CSharpBinding.csproj` compiles none of the old NRefactory context actions); the language service diffs only existing documents and flattens nested actions, so actions that create a file or ask a further choice (Generate type) are listed but report \"cannot be applied yet\" in the status bar. `ExtendedPopup` now sets `PlacementTarget` because LibreWPF refuses an owner-less popup. |
| Package Console, EnvDTE, batch operations | Present; they came from SharpDevelop, not from `monodevelop-nuget-extensions`. |

### `monodevelop-nuget-extensions` feature audit

The extension's README is a useful compatibility checklist, but not an implementation
dependency. The following maps every user-facing feature it advertised to the current
OpenDevelop package add-in.

| Former extension feature | OpenDevelop equivalent / disposition |
| --- | --- |
| Unified-search package install, including an explicit version | Manage Packages' Available search and version picker install to the selected project; the Package Manager Console also exposes `Install-Package <id> [version]`. The local-feed install journey is exercised by `AddInTests.SearchAndInstallPackage_UpdatesProjectFile`. |
| Batch install, update and uninstall across projects | `PackageViewModel.ManagePackage` asks `SelectProjectsService` for the target set and runs the package action for the selected projects. |
| PowerShell Package Console | The integrated Terminal starts `pwsh` with `OpenDevelop.PackageManagement.ps1`, which supplies `Get-Package`, `Install-Package`, `Update-Package`, and `Uninstall-Package`; `TerminalPadTests.PackageManagerConsole_LaunchesPwshAndLoadsPackageCommands` exercises its launch and script load. |
| Partial EnvDTE support for package scripts | The add-in carries the SharpDevelop `EnvDTE` shim and uses it in `PackageManagementProject`; like the former extension this remains compatibility support, not a promise that every legacy install script works. |
| List locally installed Portable Class Libraries | Intentionally not carried forward. This was a Mono-specific PCL utility; modern SDK-style projects target TFMs directly and PCL profiles/tooling are retired. It has no NuGet package-management analogue to preserve. |

Sources: `externals/monodevelop` and `externals/monodevelop-nuget-extensions` are no longer
submodules (nothing compiled from them except two Apache-2.0 launch-profile files, now copied
into the AspNetCore add-in). MonoDevelop's PackageManagement uses its IDE object model (GTK/Xwt),
so it is a reference to read, not code to link: clone `https://github.com/mono/monodevelop` into a
scratch directory when needed, and re-implement against OpenDevelop's own extension points.

## Open items (as of 2026-11-08)

Left over after the Packages menus, the light bulb for Roslyn code actions, and the missing-package
suggestion. Each is a separate, reviewable change.

**Performance / correctness risks of the light bulb**
- Every time the caret settles (about 500 ms) the language service now computes diagnostics and
  code actions, and the NuGet provider asks for diagnostics a second time. Not measured. A provider-local
  `UpsertDocumentAsync` cache is unsafe because outline, parser, and completion callers share the same
  service document; any optimization needs a service-owned document-version/content contract first.
- The bulb popup was verified only as "opens, has actions" (`od.editor.bulb-popup`). Its on-screen
  position and a real click were not checked; the popup is a separate window and is absent from the UI tree.
- After an install, the action refreshes the owning language-service project after restore. The
  missing-package integration journey verifies that its light-bulb offer disappears afterwards.
- Restore failure after "Install NuGet package" (offline feed, wrong source) is reported in the
  status bar but no test drives it.

**Language service limits**
- `DiffSolutionsToTextEditsAsync` diffs only documents that already exist and nested code actions are
  flattened, so "Generate type" and other actions that create a file or ask a further choice are
  listed but cannot be applied (status bar: "cannot be applied yet").
- The light bulb is registered for C# and VB. The missing-package provider applies the same SDK-style
  and exact-package-id restrictions to both languages.

**Missing-package suggestion**
- Only a namespace whose qualified name, or a shorter prefix, is an exact NuGet package id. A bare
  type name (`JsonConvert`) needs a package index (type-to-package map), which does not exist here.
- Always installs the latest stable version of the active source; no version choice, no
  prerelease, no package source mapping.
- SDK-style projects only (`MSBuildBasedProject.IsSdkStyleProject`); `packages.config` projects get no suggestion.

**Package nodes**
- The Update command selects the owner project from the project-tree context and opens the Updates
  tab filtered to the package id. The integration journey invokes that command through the real
  Project Browser menu item and verifies the selected tab and filter.

**Housekeeping**
- `externals/altcover` is not source-linked, but remains intentionally pinned because integration
  coverage consumes the `AltCover` NuGet package and its integration is documented in
  `doc/technotes/altcover.md`; do not drop the submodule based on the old "no reference" claim.
- `externals/vsmac-codecoverage` and `externals/vs-editor-api` are source-linked but frozen
  upstream; they could become LeXtudio GitHub Packages like sharpdbg.

## 0. Sources (historical; see Status above)

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
Historical slices below describe the original MonoDevelop-port investigation. They are not a
current backlog: the Status and feature-audit tables above supersede them. The shipped
SharpDevelop-era add-in already has install/uninstall/update, multi-project operations, source
management, and the Package Manager Console. Keep this list only for provenance when comparing
MonoDevelop's architecture.

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
