# NuGet Package Management — Migration Plan

## Why this exists

While adding integration test coverage for the (already-ported) `PackageManagement` addin
(`src/AddIns/Misc/PackageManagement/`), the addin turned out to be fundamentally broken on
non-Windows hosts (this repo is developed on arm64 macOS). Two independent crashes on addin load:

1. `PackageManagement.dll`'s `<Runtime>` import pulls in `RequiredLibraries/NuGet.Console.Types.dll`
   (referenced by `PackageManagement.csproj` as a `HintPath` `<Reference>`, used only to support the
   PowerShell Package Manager Console pad). That file is a **Windows x86 (PE32) assembly**
   (`file` reports "Intel 80386 ... for MS Windows") — loading it in an arm64 (or any non-x86)
   process throws `FileLoadException: The assembly architecture is not compatible with the current
   process architecture`, which aborts the whole addin's `ICSharpCode.Core.Runtime.Load()`.
2. Independently, `SettingsProvider.LoadSettings()` (`src/AddIns/Misc/PackageManagement/Project/Src/SettingsProvider.cs`)
   calls into the legacy `NuGet.Core` `NuGet.Settings` class, which synchronizes `nuget.config`
   reads with a **named `Mutex`** using Windows-only syntax (`Global\<hash>`). `new Mutex(...)`
   with that name throws `IOException` on macOS/Linux .NET. This one is *not* PowerShell-console-specific
   — it's hit by ordinary package-source/config loading.

Both are symptoms of the same root cause: this addin is a literal port of SharpDevelop's original
Windows-only, legacy-`NuGet.Core`-based NuGet integration (pre-dating NuGet.org's modern,
fully-portable `NuGet.Client` libraries and PowerShell Core). It was never going to work
cross-platform as ported.

## What already works despite this

The WPF UI layer (`ManagePackagesView.xaml`/`PackagesView.xaml` and their view models
`ManagePackagesViewModel`/`PackagesViewModel`/`AvailablePackagesViewModel`/`PackageViewModel`) is
in reasonable shape and **does not need to be rebuilt** — unlike UnoDevelop (see below), which had
no existing UI at all and had to design one from scratch. The bindings (`SearchTerms`,
`SearchCommand`, `PackageViewModels`, `IsReadingPackages`, `AddPackageCommand`, `IsAdded`, ...) are
sound MVVM shapes; they just currently call down into the legacy `NuGet.Core` engine
(`IPackageManagementProject`, `SharpDevelopPackageManager`, `RegisteredPackageRepositories`, ...).
The migration is an **engine swap under an existing UI**, not a UI rewrite.

Already done in support of testing this addin (kept, not blocked on the migration below):
- `AutomationProperties.AutomationId` added to the search box, results list/rows, and per-row
  Add/Added controls in `PackagesView.xaml` (search: `PackageSearchTextBox`, results:
  `PackageResultsListBox` + per-row `PackageRow_<Id>`, add button: `AddPackageButton`, added icon:
  `PackageAddedIcon`) — see `doc/technotes/integration-testing.md` for why UI-tree-visible state
  (not backend state) is what these tests assert on.
- `PackageManagementDevFlowActions.cs` (`od.nuget.set-local-feed`, `od.nuget.open-dialog`,
  `od.nuget.set-search-text`, `od.nuget.search`, `od.nuget.status`, `od.nuget.install`,
  `od.nuget.close-dialog`) — drives the dialog through the same `SearchCommand`/`AddPackageCommand`
  bindings the UI uses. These call VM-level members, not `IPackageManagementProject` directly, so
  they should keep working largely unchanged once the VMs are re-wired to the new engine — the
  method *names* (`AvailablePackagesViewModel.SearchCommand`, `PackageViewModel.AddPackageCommand`,
  `.Id`, `.IsAdded`) are the contract; their *implementation* is what changes.
- `tests/fixtures/LocalNuGetFeed/` — a throwaway `OpenDevelop.TestPackage` v1.0.0 nupkg + its
  packable source project, so install tests don't depend on nuget.org over the network.
- `tests/fixtures/NuGetFixture/` + `tests/OpenDevelop.IntegrationTests/NuGetAddInTests.cs` — an
  end-to-end test (open project → set local feed → search → assert real UI-tree row state → click
  the real per-row Add button → assert `IsAdded`/on-disk `.csproj` PackageReference/Project Browser
  tree node) that is currently blocked on the two crashes above, not on anything test-side.

## Sources

- `externals/monodevelop-nuget-extensions` (submodule, `mrward/monodevelop-nuget-extensions`,
  added this session) — **not** the core NuGet manager; it's MonoDevelop's PowerShell Package
  Console *add-on*. Its `MonoDevelop.PackageManagement.PowerShell`/`.PowerShell.Cmdlets` projects
  host real `Install-Package`/`Get-Package`/`Find-Package` PowerShell Core cmdlets against modern
  `NuGet.PackageManagement`/`NuGet.Protocol`/`NuGet.Configuration` (referenced from MonoDevelop's
  own bin dir, not vendored/forked) — this cmdlet-hosting code is UI-framework-agnostic and is the
  genuinely reusable part for a *future* Console pad slice (see slice 8 below). Its actual Console
  pad UI (`MonoDevelop.PackageManagement.Gui/PackageConsolePad.cs`,
  `PackageConsoleViewController.cs`) is built on Xwt/GTK plus a nested submodule
  `external/vsmac-console` (`Microsoft.VisualStudio.Components.ConsoleViewController`, a Mac-native
  Cocoa terminal-input widget) — **not reusable as-is** for a WPF app; would need a WPF
  reimplementation of the same REPL-pad shape, likely on top of the AvalonEdit-based skeleton
  `PackageManagementConsolePad`/`ICSharpCode.Scripting` already provides in this addin.
- `/Users/lextm/uno-tools/UnoDevelop` (sibling project, `docs/nuget-manager.md`) — already did the
  "swap legacy engine for modern NuGet.Client" move, from a blank slate (no prior NuGet UI). Its
  `src/Main/Base/Src/NuGet/UnoNuGetProject.cs` (implements `NuGet.ProjectManagement.NuGetProject`
  over its own `IProject` abstraction) and `NuGetPackageSourceCatalog.cs`
  (`NuGet.Configuration.Settings.LoadDefaultSettings` for `nuget.config` resolution) are the closest
  precedent for OpenDevelop's own `IProject`-based adapter — read these before writing OpenDevelop's
  equivalent, but note OpenDevelop's project model, while also SharpDevelop-derived, has since
  diverged (CPS-based `IProjectTree`, see `src/Main/SharpDevelop/Services/SharpDevelopProjectTreeProvider.cs`) -
  confirm the actual shape matches before assuming a drop-in.
- `NuGet.PackageManagement`/`NuGet.Protocol`/`NuGet.Configuration`/`NuGet.Frameworks`/
  `NuGet.Packaging`/`NuGet.Resolver`/`NuGet.ProjectManagement` — real, current packages from
  nuget.org (same ones UnoDevelop and MonoDevelop's own addin both reference this way). No submodule
  needed for these; a plain `<PackageReference>` in `PackageManagement.csproj` is the correct move,
  same as `ICSharpCode.SharpDevelop.Uno.csproj` already does in UnoDevelop.

## Scope

`grep -rl "using NuGet;" src/AddIns/Misc/PackageManagement/Project/Src/*.cs` → **166 of 401** files
in the addin reference the legacy `NuGet.Core` namespace; **86** touch core repository/package types
directly (`IPackageRepository`, `IPackage`, `SemanticVersion`, `PackageSource`, `IPackageManager`).
This is not a small patch — it's a from-the-engine-up rewrite of most of the addin's non-UI layer,
comparable in size to UnoDevelop's multi-slice effort, just starting from an existing UI instead of
none.

## Proposed slice order

Numbered independently of UnoDevelop's (different starting point: UI already exists here).

1. **Unblock loading, no engine change yet.** Remove the PowerShell Console pad's `<Pad>`/`<Class>`
   Codons from `PackageManagement.addin` and the `NuGet.Console.Types`/`Microsoft.Web.XmlTransform`
   `<Reference>`s + `Microsoft.PowerShell.SDK` `<PackageReference>` from `PackageManagement.csproj`
   (mirrors `MonoDevelop.PackageManagement.PowerShell*` being split out as an *extension*, not core,
   in the MonoDevelop lineage this was forked from). This alone fixes crash #1 but not #2 (Settings
   Mutex) since ordinary package-source loading still goes through legacy `NuGet.Settings` — expect
   this slice to still fail before search/install works, but it isolates crash #2 as the only
   remaining blocker and is a good checkpoint to verify against `GitAddInTests`-style "does the
   addin even load" smoke coverage (`od.addins` contains `PackageManagement.addin`) before going
   further.
2. **Package source / settings engine swap.** Replace `SettingsProvider`'s use of legacy
   `NuGet.Settings`/`RegisteredPackageRepositories`'s `PackageSource` with
   `NuGet.Configuration.Settings.LoadDefaultSettings` + `NuGet.Configuration.PackageSource`
   (UnoDevelop's `NuGetPackageSourceCatalog.cs` is the concrete precedent). Fixes crash #2. At this
   point the addin should load cleanly and `od.nuget.set-local-feed` should work end-to-end for
   *reading* configured sources (not yet search/install).
3. **Project adapter.** Implement `NuGet.ProjectManagement.NuGetProject` against this codebase's
   `IProject` (own new code, informed by both `UnoNuGetProject.cs` and the existing
   `IPackageManagementProject`'s method shape so the rest of the addin's call sites change as little
   as possible) — `GetInstalledPackagesAsync` from evaluated `PackageReference` items (same data
   `SharpDevelopProjectTreeProvider.cs` already extracts for the Solution Explorer tree).
4. **Search.** Replace `RegisteredPackageRepositories`/`IPackageRepository.Search(...)` with
   `NuGet.Protocol.PackageSearchResource` against the resolved sources from slice 2
   (`NuGetPackageSearchService.cs` in UnoDevelop is the concrete precedent). `AvailablePackagesViewModel`
   keeps its shape (`PackageViewModels`, `IsReadingPackages`) — only what populates the collection
   changes. This is the slice that unblocks `NuGetAddInTests.SearchAndInstallPackage_...`'s search
   half.
5. **Install / uninstall.** Replace `IPackageManagementProject.InstallPackage`/
   `SharpDevelopPackageManager` with real `NuGetPackageManager.InstallPackageAsync`/
   `UninstallPackageAsync` against slice 3's project adapter, writing the `PackageReference` back
   via MSBuild and triggering a restore. `PackageViewModel.AddPackageCommand`/`IsAdded` keep their
   shape. This unblocks the rest of `NuGetAddInTests`.
6. **Update.** Diff installed vs. latest/matching-range from search; `UpdatedPackagesViewModel`
   already exists as a tab, currently unpopulated by real data.
7. **Restore command.** `RestorePackagesCommand.cs` currently shells out via the legacy
   `NuGet.exe`/`NuGet.Core` restore path — replace with `NuGet.PackageManagement`'s restore APIs
   (or, simpler, shell out to `dotnet restore`, which is what actually resolves `PackageReference`
   items in modern SDK-style projects regardless of what this addin does).
8. **PowerShell Package Console pad** (optional, from `externals/monodevelop-nuget-extensions`,
   *not* required for the core dialog to work) — port the cmdlet-hosting logic from
   `MonoDevelop.PackageManagement.PowerShell.Cmdlets` (PowerShell Core, cross-platform, calls the
   same `NuGet.PackageManagement` APIs slice 3-5 already wire up) against a new WPF pad built on
   this addin's existing `PackageManagementConsolePad`/AvalonEdit skeleton — do **not** attempt to
   reuse `PackageConsoleViewController`/`vsmac-console` (Xwt/Cocoa, not WPF).

Deferred / open questions, not yet scoped:

- **`RegisteredPackageSourcesView`/`PackageManagementOptionsView`** (Tools > Options panels) also
  reference legacy types (`PackageSource`, `RegisteredPackageSources`) and will need updating
  alongside slice 2.
- **EnvDTE / `install.ps1`/`uninstall.ps1` script support** — same open question UnoDevelop flagged;
  likely skippable, revisit only if slice 8 is attempted and real-world packages are found needing it.
- **Licensing**: `monodevelop-nuget-extensions` — confirm its license (check its own LICENSE file)
  is compatible before porting code verbatim in slice 8, same diligence as `externals/SharpDevelop`.
- Whether to keep `RequiredLibraries/NuGet.Core.dll`/`NuGet.exe` around at all after slice 5, or
  remove them entirely once nothing references the legacy engine.
