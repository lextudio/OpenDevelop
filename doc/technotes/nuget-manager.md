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

Decision (2026-11-08): the light bulb suggests only packages that a project of the same solution already
references, at the version the solution already uses (`SolutionPackages`; under Central Package Management
the version is read from `Directory.Packages.props`, and the highest version wins when projects differ).
It makes no network request while the caret settles. A package no project of the solution has used yet is
added through Manage Packages, not suggested. What is left to do:

- **Bare type names (done 2026-11-08).** `JsonConvert` with no `using` is matched to a package the solution
  already references when that package declares a public type of that name (`PackageTypeIndex`): NuGet's own code
  finds the package in the global packages folder (`VersionFolderPathResolver`, `PackageFolderReader`, nearest
  `ref`/`lib` group for the project's framework) and `System.Reflection.Metadata` lists the public top-level
  types. It is offered for CS0246/BC30002 and for CS0103/BC30451 (an expression such as
  `JsonConvert.SerializeObject(...)`), only when the name is a single identifier, at most three packages, and the
  action's title says which type it declares. Covered by `PackageTypeIndexTests` and
  `AddInTests.LightBulb_OffersToInstallThePackageThatDeclaresABareTypeName` (a second fixture package,
  `OpenDevelop.TypeProbe`, declares `ProbeWidget`). Limits: only packages that are restored into the global
  packages folder (nothing is downloaded), only the packages' own compile assets (not their dependencies),
  only direct references of the solution's projects; the action adds the reference, and the `using` is then
  offered by Roslyn's own fix; an unresolved variable that happens to be named like a package's public type is
  also offered; the first lookup reads each candidate package's assemblies (cached afterwards), whose cost on
  a large solution was not measured. The test installs `OpenDevelop.TypeProbe` into the developer's real global
  packages folder (the restore needs it there).
- **Project shapes.** SDK-style projects only (`MSBuildBasedProject.IsSdkStyleProject`);
  `packages.config` projects get no suggestion. Visual Basic works for the namespace and type diagnostics
  (BC40056 for an `Imports`, BC30002 for an undefined type, both measured). An undeclared bare name
  (CS0103 / BC30451) is matched only through the type index above, never as a package id.
- **Cold start.** The first use of a package in a solution is not offered; deliberate, see above.

### 2. Light bulb: cost and verification

- **Cost per caret stop (measured 2026-11-08, no change needed).** About 500 ms after the caret settles, the
  light bulb asks every provider; both the Roslyn provider and the NuGet provider synchronise the document and
  read diagnostics. `UpsertDocumentAsync` is a no-op for a project document when the service's own copy already has the same text
  (a content check inside the service, which is where it is safe: outline, parser and completion share that
  document), so the second synchronisation is free. Measured with `od.editor.bulb-actions` in the IDE on the
  `ICSharpCode.SharpDevelop` project (747 source files, Debug build, Apple silicon, caret in a method body):
  the first two calls after loading took about 1.3 s and 1.1 s (the compilation warming up, once); afterwards a
  call took 0.03-0.07 s unchanged, and the first call after an edit 0.10-0.15 s. All of it runs off the UI
  thread. Not measured: a much larger solution, a Release build, a machine without a warm compilation.
- **The popup itself.** `od.editor.bulb-popup` opens the real popup the way the renderer does and
  `AddInTests.LightBulb_OffersRoslynCodeActions_AndAppliesOneToTheEditor` checks that it opens with the actions,
  uses absolute placement, lies inside the text area (offsets read from the popup, since its window is absent from
  the UI tree) and follows the caret: with the caret one line higher the popup is higher. Not covered: a real
  mouse click on an item (choosing an action is covered through its `ExecuteAsync`, which is what the click
  calls), keyboard navigation inside the popup, and pixel-exact placement.
- **Restore failure (covered).** After "Install NuGet package", a failed restore is reported in the status bar
  ("Added NuGet package 'X' v to the project, but restoring it failed: ...", never "Installed"), and the
  reference stays in the project. `AddInTests.LightBulb_InstallReportsAFailedRestoreInsteadOfSuccess` drives it
  with a solution that already uses the package at a version no feed has (9.9.9), which the install copies.
  (Breaking the feed would not work: a version already in the global packages folder is restored without
  asking the sources.)

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

### 5. Features the package manager does not have

Audited 2026-11-08 by searching `src/AddIns/Misc/PackageManagement` and `src/Main/Base/Project/Src/NuGet`
(an earlier version of this note counted keyword hits, which were only the repository's own build
pins, not features). Vulnerability and deprecation warnings did not exist then and do now (below). None of these exist:

- A transitive-dependency view (the dependency groups in package details are the only dependency data).
- A Consolidate view and package source mapping.
- Central Package Management awareness beyond installing (below): the Installed and Updates tabs do not
  read versions from `Directory.Packages.props`; not examined, so unknown whether they show the right version.

**Deprecation and vulnerability warnings in the details pane (done 2026-11-08).** Selecting a package shows, in
bold red above its description, what the package's feed says about that version: deprecated (with NuGet's reasons,
the author's message and the recommended alternative) and known vulnerabilities (severity and advisory link). The
data is NuGet.Protocol's own (`PackageMetadataResource`, i.e. the registration data Visual Studio and
`dotnet list package --vulnerable` read), fetched by `NuGetPackageHealthService` the first time the details pane
binds `PackageViewModel.HealthText`, so only the selected package costs a request. The feeds asked are the one the
dialog is browsing (all enabled ones for the aggregate), taken from NuGet's configuration so credentials come
along. A feed that carries no such data (a local folder) or is down shows nothing: that is not a finding.
Covered by `NuGetPackageHealthServiceTests` (an in-process v3 feed, so NuGet.Protocol's own parsing is under test)
and `AddInTests.DetailsPane_WarnsAboutADeprecatedAndVulnerablePackage_AndOnlyAboutThat` (the real dialog
against `FakeNuGetV3Feed`: the flagged package shows the warning in the UI tree, a healthy one does not). Limits:
the details pane only (no marker on the list rows, which would cost one request per row); the Available tab is
what the integration test drives (the Installed and Updates tabs use the same view model and template, not
exercised); the version is the package's own (a floating or ranged reference is not looked up); nothing is
checked automatically at install or build time (`dotnet restore` already reports NU1901-NU1904 through NuGetAudit).

**Central Package Management install (fixed).** In a project that sets `ManagePackageVersionsCentrally`,
NuGet rejects a `Version` on a `PackageReference` (NU1008, reproduced with `dotnet restore`). The SDK-style
install path used to write `Version=` always, so installing into such a project (OpenDevelop's own
solution is one) broke its restore. `SdkStylePackageReferenceService` now detects it through the evaluated
`ManagePackageVersionsCentrally` and `DirectoryPackagesPropsPath`, writes the reference without a version and
adds or updates the `PackageVersion` entry in `Directory.Packages.props` (`CentralPackageVersionsFile`),
keeping its BOM, line endings and XML declaration, writing the props file before the project is touched, and
ignoring entries in conditional item groups. Covered by
`AddInTests.LightBulb_InstallsMissingNuGetPackage_UnderCentralPackageManagement` and the unit tests in
`tests/OpenDevelop.Base.Tests/CentralPackageVersionsFileTests.cs`. Not covered: updating an
existing central package from the Updates tab, `VersionOverride`, and a project whose props file is not found
(it falls back to writing `Version=`).

### 6. Verification gaps and package-source limits

- Covered by `AddInTests.SearchAndInstallPackage_UpdatesProjectFile`: from the package node and from the
  Packages folder, Manage Packages... opens the dialog (title `NuGetFixtureApp - Manage Packages` for the
  fixture's project), Update opens the Updates tab filtered to the package, and Restore Packages from the
  folder recreates `obj/project.assets.json`. With one project the title cannot tell the node's owning
  project from `PackageManagementSelectedProjects`' fallback to a solution's only project, so the mapping is
  covered separately by `AddInTests.PackageNodeCommands_OpenTheDialogForTheProjectThatOwnsTheNode_WithSeveralProjects`:
  two projects reference different packages, and Manage Packages... and Update on each package node open the
  dialog titled for that node's own project (`AppA` / `AppB`; with two projects the fallback would give the
  solution's name). Not covered: the Packages folder of a specific project (both folders are named `Packages` and
  `od.project-browser.invoke-context-menu` finds the first by name) and Restore Packages from a second
  project's node.
- **Package sources follow NuGet's own definition (done 2026-11-08).** The add-in used to read and write
  only `<solution>/.nuget/NuGet.Config` (`PortableNuGetSettings`, via `SettingsProvider`), the NuGet 2.x
  location. `dotnet restore` ignores that file (measured: a feed defined only there is not used, the same feed in
  `<solution>/NuGet.Config` is), and it also uses feeds from parent directories and from the user's
  `~/.nuget/NuGet/NuGet.Config`, which the dialog never listed. The restore after an install passed `--source`,
  which hid the mismatch. Now `RegisteredPackageSourceSettings` reads and saves sources, their enabled state and
  the active source through `NuGet.Configuration` (`Settings.LoadDefaultSettings` + `PackageSourceProvider`),
  the code Visual Studio and the dotnet CLI use: the merged hierarchy of the solution's directory, every
  directory above it, and the user's and machine's files, with `<clear/>` honoured; and the restore after an
  install passes no `--source`. There is no hand-written merge or write logic in the add-in for this (an earlier
  attempt at one was dropped; it also guessed wrong that `<remove>` and `value="false"` in a nearer file undo
  a farther file, which `dotnet nuget list source` shows they do not). What NuGet does on a save is what happens:
  removing an inherited source edits the file that defines it, a new source goes where NuGet puts new sources,
  and the active source is stored in the user-level file. Reading the active-source default no longer saves it
  (`RegisteredPackageRepositories` only remembers it), so merely opening the dialog does not write the user's
  configuration. Covered by `AddInTests.PackageSources_AreTheNuGetConfigurationOfTheSolution_AndRestoreUsesThem`
  (the feed is defined one directory above the solution; the assets file records that the restore used only it)
  and `RegisteredPackageSourceSettingsTests` (settings loaded from exactly the given files, never the user's).
  The integration tests put a `NuGet.Config` with `<clear/>` and the local feed into the temporary solution
  directory; the old `od.nuget.set-local-feed` action is gone because adding and activating a source through
  the provider would write the developer's own user-level configuration. Limits: sources a user had saved in
  the old `.nuget/NuGet.Config` are no longer listed (move them to `<solution>/NuGet.Config`);
  `packageSourceMapping` is not shown in the dialog (restore applies it); the nested-value settings used for
  packages.config (`repositoryPath`) are still read from the old single file.
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
| Missing-package quick fix | **Present for unresolved namespaces**: `MissingPackageContextActionProvider` (PackageManagement add-in) reads CS0246/CS0234 at the caret in an SDK-style project, matches the qualified name at the diagnostic, and its shorter prefixes, against the packages the solution's projects already reference (no network), and offers "Install NuGet package 'X' v in 'Project'" in the light bulb; choosing it writes the PackageReference on the UI thread and runs `dotnet restore` in the background. Covered by the `AddInTests.LightBulb_*` facts (a second project of the temporary solution already uses the package from the local feed; one fact checks that a package no project uses is not offered) and by `MissingPackageNamesTests`. A bare type name such as `JsonConvert` is matched through the type index of the packages the solution already uses. Not covered: non-SDK projects. It works for Visual Basic too (BC40056 and BC30002). Underneath it, `LanguageServiceContextActionsExtension` puts Roslyn's code actions in the bulb for C# and VB (`CSharpBinding.addin`, `VBBinding.addin`; the bulb had no providers at all because the bindings compile none of the old NRefactory context actions); the language service diffs only existing documents and flattens nested actions, so actions that create a file or ask a further choice (Generate type) are listed but report "cannot be applied yet" in the status bar. `ExtendedPopup` now sets `PlacementTarget` because LibreWPF refuses an owner-less popup. |
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
