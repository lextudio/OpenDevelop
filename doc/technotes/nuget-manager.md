# NuGet package management: status and remaining work

How packages are managed in OpenDevelop and what is still missing. The code is the add-in
`src/AddIns/Misc/PackageManagement` (inherited from SharpDevelop and extended), plus the light-bulb
pieces in `src/Main/Base/Project/Src/LanguageServices/LanguageServiceContextActions.cs` and
`src/AddIns/DisplayBindings/AvalonEdit.AddIn/Src/ContextActions/`. Earlier versions of this note
planned a WinUI port for UnoDevelop; that project is retired and the plan was dropped (see git history).

## Remaining work

Ordered by what a user would notice first. Each item is a separate, reviewable change. "Not verified"
means no test or measurement covers it, not that it is known to be broken.

### 1. Missing-package suggestion: reach

The light-bulb action (`MissingPackageContextActionProvider`) covers only part of the problem.

- **Bare type names.** `JsonConvert` with no `using` gets no suggestion: mapping a type or namespace to
  a package needs a package index (VS ships a downloaded database for this). Without one, only a
  namespace whose qualified name, or a shorter prefix, is an exact package id is found. Decide whether a
  small curated map of popular packages is worth carrying, or whether to consume nuget.org's own
  search by namespace if it offers one.
- **Version and source choice.** It always installs the latest stable version from the active source.
  There is no version picker, no prerelease, no package source mapping.
- **Project shapes and languages.** SDK-style projects only (`MSBuildBasedProject.IsSdkStyleProject`);
  `packages.config` projects get no suggestion. VB is registered but never fires: the provider matches
  only CS0246/CS0234. VB's equivalents (for example BC30002 and BC40056; confirm the ids) need adding,
  and the namespace extraction must read VB syntax (`Imports Newtonsoft.Json`).

### 2. Light bulb: cost and verification

- **Cost per caret stop.** About 500 ms after the caret settles, the language service computes
  diagnostics and code actions, and the NuGet provider asks for diagnostics a second time. Not
  measured. A provider-local cache of `UpsertDocumentAsync` is unsafe because outline, parser and
  completion callers share the same service document; any saving needs a service-owned
  document-version/content contract first.
- **The popup itself.** Verified only as "opens and has actions" (`od.editor.bulb-popup`). Its on-screen
  position and a real click are not checked (it is a separate window and absent from the UI tree).
- **Restore failure.** After "Install NuGet package", a failed restore (offline feed, wrong source) is
  reported in the status bar, but no test drives that branch.

### 3. Language service: actions it cannot apply

`DiffSolutionsToTextEditsAsync` diffs only documents that already exist, and nested code actions are
flattened. Actions that create a file or ask a further choice ("Generate type", "Move type to file")
are listed in the bulb but report "cannot be applied yet" in the status bar. Fixing it means extending
the code-action contract beyond text edits (added documents, and a choice step), which also touches the
remote and LSP language-service implementations.

### 4. Engine: how install and uninstall are done

From reading the code, not measured end to end:

- Search runs on `NuGet.Protocol` v3. Install, uninstall and update still go through the legacy
  `NuGet.Core` (v2) engine kept in `RequiredLibraries/`; for SDK-style projects the add-in bypasses it by
  writing the `PackageReference` itself (`SdkStylePackageReferenceService`) and shelling out to
  `dotnet restore`. `packages.config` projects use the legacy engine.
- Moving to NuGet.Client (`NuGet.PackageManagement`'s `NuGetPackageManager` over a
  `NuGetProject` adapter for `IProject`) would give one engine, real dependency resolution and proper
  restore results instead of an exit code. It is the largest item here and not urgent while the
  SDK-style path works.

### 5. Features to audit against MonoDevelop and Visual Studio

Present as keywords in the code but not examined, so their depth is unknown: vulnerability and
deprecation warnings in package details, transitive dependencies, central package management
(`Directory.Packages.props`). Not found at all: a Consolidate view, package source mapping.
Audit each in the running IDE before deciding whether it is a gap.

### 6. Verification gaps and a behaviour to decide

- Covered by `AddInTests.SearchAndInstallPackage_UpdatesProjectFile`: from the package node and from the
  Packages folder, Manage Packages... opens the dialog (title `NuGetFixtureApp - Manage Packages` for the
  fixture's project), Update opens the Updates tab filtered to the package, and Restore Packages from the
  folder recreates `obj/project.assets.json`. **Not covered: which project a node resolves to when the
  solution has several.** The fixture has one project, and `PackageManagementSelectedProjects` falls back
  to a solution's only project when `CurrentProject` is null, so the title cannot prove the mapping; that
  needs a second project in a fixture.
- **Restore Packages ignores the active package source.** `NuGetPackageRestoreCommandLine` runs
  `dotnet restore <solution>`, so only `nuget.config` decides the feeds, while the install path
  (`SdkStylePackageReferenceService.Restore`) passes `--source <active source>`. A package that exists only in a source chosen in the Manage Packages dialog can therefore be installed and then fail a later
  Restore Packages. Read from the code, not reproduced. Decide whether Restore should follow the active
  source, or the dialog should write the chosen source into `nuget.config`.
- After an install, the owning language-service project is refreshed after the restore, and the
  missing-package journey checks the offer disappears; whether the editor's red squiggle clears is
  not checked.

### 7. Housekeeping

- `externals/altcover` stays: integration coverage consumes the `AltCover` NuGet package and
  `doc/technotes/altcover.md` documents it. Its lack of a project reference does not make it unused.
- `externals/vsmac-codecoverage` and `externals/vs-editor-api` are source-linked but frozen upstream;
  they could be published as LeXtudio GitHub Packages, as sharpdbg was.

## What ships today (measured 2026-11-08 against the running IDE)

The package add-in is the SharpDevelop-era
`src/AddIns/Misc/PackageManagement` (Manage Packages dialog, Restore, Options, sources, the
Package Manager Console hosted as a `pwsh` Terminal session, EnvDTE and Cmdlets), with search on
`NuGet.Protocol` v3. Compared with `MonoDevelop.PackageManagement`, found with DevFlow on a copy of
`tests/fixtures/NuGetFixture` after installing a package from the local feed:

| Area | State |
|---|---|
| Solution Explorer nodes | **Present**: Dependencies > Packages > `<Id> (<version>)`, next to Analyzers and Frameworks. Do not port MonoDevelop's `NodeBuilders`. |
| Project node menu | Has Manage Packages... and Restore Packages. |
| Packages folder / package node menus | The folder has its own `PackagesFolderNode` menu (Manage Packages..., Restore Packages). A package node has Update (opens the Updates tab filtered to that package and switches to its owning project), Manage Packages..., then Remove; `AddInTests.SearchAndInstallPackage_UpdatesProjectFile` invokes the real Update command and verifies its tab and canonical package-id filter. |
| Missing-package quick fix | **Present for unresolved namespaces**: `MissingPackageContextActionProvider` (PackageManagement add-in) reads CS0246/CS0234 at the caret in an SDK-style project, tries the qualified name at the diagnostic and its shorter prefixes as an exact NuGet package id against the active source, and offers "Install NuGet package 'X' v in 'Project'" in the light bulb; choosing it writes the PackageReference on the UI thread and runs `dotnet restore` in the background. Covered by `AddInTests.LightBulb_OffersToInstallMissingNuGetPackage_AndInstallsIt` (local feed, no network). Not covered: a bare type name such as `JsonConvert` (no package index to map it), non-SDK projects, and VB (the provider is registered for `.cs;.vb` but matches only the C# diagnostics CS0246/CS0234, so VB gets no suggestion). Underneath it, `LanguageServiceContextActionsExtension` puts Roslyn's code actions in the bulb for C# and VB (`CSharpBinding.addin`, `VBBinding.addin`; the bulb had no providers at all because the bindings compile none of the old NRefactory context actions); the language service diffs only existing documents and flattens nested actions, so actions that create a file or ask a further choice (Generate type) are listed but report "cannot be applied yet" in the status bar. `ExtendedPopup` now sets `PlacementTarget` because LibreWPF refuses an owner-less popup. |
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

Upstream reference: `externals/monodevelop` and `externals/monodevelop-nuget-extensions` were removed
as submodules (nothing compiled from them except two Apache-2.0 launch-profile files, now copied into
the AspNetCore add-in). MonoDevelop's `MonoDevelop.PackageManagement` is built on its own IDE object
model (GTK/Xwt), so it is something to read, not code to link: clone `https://github.com/mono/monodevelop`
into a scratch directory when needed and re-implement against OpenDevelop's extension points.
Upstream's `main/COPYING` is the LGPL-2.1 text (checked against `mono/monodevelop` on 2026-11-08; GitHub
reports no repository-level license) while individual files carry their own headers, for example the
Apache-2.0 launch-profile files copied into AspNetCore (`doc/technotes/licensing-audit.md`). Read the
header of any file before copying text from it.
