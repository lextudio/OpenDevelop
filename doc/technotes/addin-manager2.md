# AddIn Manager (package/gallery UI) unification plan

**Status (2026-07-28): implemented.** The plan below was carried out largely as written, with two
corrections found during implementation (see "What actually moved" and "Verification" below). Not
to be confused with
`addin-manager.md`, which documents the `.addin`/AddInTree plugin *system* itself (already fully
shared infrastructure) - this note is specifically about the "manage installed AddIns / install from
a gallery" end-user UI, i.e. `AddInManager2` (OpenDevelop) vs. `AddInManagerDialog` (UnoDevelop).

## What exists today

`AddInManager2` (this repo) and UnoDevelop's `src/AddIns/Misc/PackageManagement/Project/Src/AddInManagerDialog.xaml.cs`
are NOT a wholesale copy like XmlEditor/GitAddIn were - there is no UnoDevelop-side copy of the
`AddInManager2` project at all. They are two different-scope re-implementations of "manage installed
AddIns":

| Feature | OpenDevelop `AddInManager2` | UnoDevelop `AddInManagerDialog` |
|---|---|---|
| Enable/disable/remove installed AddIns | yes | yes |
| Install from local `.addin`/`.sdaddin`/`.vsix`/`.zip` file | yes | yes |
| Browse/install from an online NuGet-style gallery, paging, updates, license acceptance | yes (`Model/NuGetPackageManager.cs`, `Model/PackageRepositories.cs`, `Model/Page*.cs`, `View/LicenseAcceptanceView`, `ViewModel/*AddInsViewModel`) | **no** - does not exist on UnoDevelop at all |
| Structure | ~52 files, full MVVM (`Model`/`View`/`ViewModel`) | 262 lines, one file, plain event handlers, no MVVM |

The low-level primitives both sides call (`AddInManager.Enable/Disable/AddExternalAddIns/RemoveExternalAddIns`,
`AddIn.Load`, `IAddInTree`) already live in shared `ICSharpCode.Core` and are not duplicated - that
part is already unified today. The entire gap is the online-gallery browse/install/update layer,
which UnoDevelop never had.

Checked: only `AddInManager2/Project/Src/DelegateCommand.cs`, `BooleanToFontWeightConverter.cs` (a
WPF value converter), and `UpdateNotifier.cs` reference `System.Windows`. Everything under
`Model/**` and most of `ViewModel/**` is already UI-framework-agnostic - a real candidate for the
same shared-engine pattern used for NuGet package search (see `nuget.md`): extract the engine into
Base, let each host keep its own UI on top.

## Plan

1. Move `AddInManager2/Project/Src/Model/**` (and the `System.Windows`-free `ViewModel/**` files)
   into a shared location, e.g. `Main/Base/Project/Src/AddInManager/`, following the
   `NuGetPackageSearchEngine.cs` precedent: relocate with no behavior change to OpenDevelop's call
   sites, then link it back in via `$(SharpDevelopSourceRoot)` from `AddInManager2.csproj`.
2. Before moving anything, find every real consumer by reading constructors/composition root
   (`Model/Model.cs`, `ViewModel/AddInManagerViewModel.cs`), not by grepping for the type name - the
   NuGet mistake documented in `nuget.md` happened because a `var`-typed call site hid the type name
   from a literal grep. Verify with a `git stash` baseline + a build that actually reaches every
   changed file (see `nuget.md`'s direct-`csc` technique if `dotnet build` can't get there due to
   the pre-existing unrelated `WpfDesign.AddIn` blocker), not just "0 errors" on a build that may
   have stopped short.
3. Extend UnoDevelop's `AddInManagerDialog.xaml.cs` to add the online-gallery feature it currently
   lacks, consuming the shared `PackageRepositories`/`NuGetPackageManager`/`AddInSetup` engine
   directly through a new WinUI-native browse/install/update UI - not a port of OpenDevelop's WPF
   XAML View/ViewModel (those stay OpenDevelop-only, mirroring the tree-view split documented in
   `xml-editor.md`). Match the dialog's existing plain-event-handler style rather than introducing
   `ICommand`/`DelegateCommand`-based MVVM.
4. Update `docs/opendevelop-sync.md`'s `AddIns/Misc/AddInManager2` entry once the shared engine
   exists, since the current wording ("native AddIn scout/manager UI over shared AddIn/NuGet
   services") predates this plan and should instead point at the new shared engine location.
5. Verify: `AddInManager2.csproj` standalone build, UnoDevelop's `PackageManagement.csproj` (note:
   this project has a pre-existing, unrelated broken `WpfDesign.AddIn` sibling-repo reference in
   this sandbox that blocks `dotnet build` at the solution level - use the `nuget.md` direct-`csc`
   verification technique if it recurs), full `UnoDevelop.slnx` build, `UnoDevelop.Core.Tests`
   (204), and a single serial `UnoDevelop.IntegrationTests` run (70 - never run concurrently with
   another instance, see `xml-editor.md`'s port-conflict note).

## Why this isn't a byte-for-byte merge

Unlike XmlEditor/GitAddIn (byte-identical wholesale copies with one obvious canonical choice),
OpenDevelop's WPF View/ViewModel/XAML for the online gallery has no Uno equivalent, and building one
is real new UI work - the same situation as the XmlEditor tree-view port. The Model layer is what's
genuinely shareable, and duplicating *that* would be the real waste; the UI on top stays per-host,
the same split as `NuGetPackageSearchEngine` (shared) vs. each host's own result projection (not
shared).

## What actually moved (corrections found during implementation)

Two things step 2's investigation didn't anticipate, found by actually reading `Model.cs` and the
top-level `AddInManagerServices.cs` composition root (not just grepping):

1. **`ViewModel/**` turned out not to be shareable at all.** Every `ViewModel` class - even the ones
   with no direct `System.Windows` using - inherits from `AddInsViewModelBase`, which itself is
   `System.Windows.Input.ICommand`/`DelegateCommand`-based. So only `Model/**` moved, not any
   `ViewModel` files; the "most of ViewModel/** is UI-agnostic" premise in the original plan (based
   on a per-file `using` grep) was wrong once the inheritance chain is checked.
2. **`Src/AddInManagerServices.cs`** (the static services container / composition root, directly
   above `Model/`, not inside it) is itself entirely WPF-free and is what `Model<TModel>`'s
   default constructor depends on (`AddInManagerServices.Services`). It moved alongside `Model/**`
   (same namespace, `ICSharpCode.AddInManager2` for this file, `ICSharpCode.AddInManager2.Model`
   for everything under `Model/`) since nothing in `Model/**` works without it.

Final moved set: `Model/**` (18 files + 8 `Interfaces/*.cs`) plus `AddInManagerServices.cs`, all
into `Main/Base/Project/Src/AddInManager/` (namespaces unchanged, so no OpenDevelop call site
changed). One dead `using ICSharpCode.AddInManager2.ViewModel;` in `ReadPackagesResult.cs` (unused,
never actually referenced anything) was removed - required for the move since Base cannot
reference the AddInManager2 assembly (that would be a reference cycle), and it changed no behavior.
Base's own csproj gained `NuGet.Core`/`ICSharpCode.SharpZipLib` `Reference`s (same pre-built
binaries AddInManager2.csproj itself already referenced via `HintPath`) since the moved files need
them. AddInManager2.csproj's own `NuGet.Core` reference stays (still used directly by `ViewModel`
files); its `SharpZipLib` reference became unused by AddInManager2 itself post-move but was left in
place rather than risk an unrelated edit.

On the UnoDevelop side, the engine is linked into UnoDevelop's *own* `ICSharpCode.SharpDevelop.csproj`
(`src/Main/Base/Project/ICSharpCode.SharpDevelop.csproj`, a separate `EnableDefaultCompileItems=false`
project that already links dozens of OpenDevelop Base files via `$(SharpDevelopSourceRoot)` - the
`NuGetPackageSearchEngine.cs` precedent lives there too), not into OpenDevelop's own Base csproj.
Each of the moved `AddInManager/*.cs` and `AddInManager/Interfaces/*.cs` files got its own
`<Compile Include>` `Link` entry there, plus the same `NuGet.Core`/`SharpZipLib` `Reference`s (via
`$(SharpDevelopSourceRoot)`-relative `HintPath`s). `PackageManagement.csproj` needed its own direct
`NuGet.Core` `Reference` too (for `IPackage` used directly in `AddInManagerDialog.xaml.cs`) -
referencing Base's assembly alone isn't enough for a type used directly, same reason
AddInManager2.csproj itself carries its own `NuGet.Core`/`SharpZipLib` references despite also
referencing Base.

`AddInManagerDialog.xaml.cs` gained a new "Online Gallery" `TabViewItem`: search box + "Updates
only" filter, a paged `ListView` (page size 10, manual Skip/Take over an in-memory
`GroupBy(Id).OrderByDescending(Version).First()` dedup - the closest available equivalent to
OpenDevelop's `DistinctLast(PackageEqualityComparer.Id)`, which lives in an
AddInManager2-internal `Extensions.cs` not available from UnoDevelop), and an Install/Update button
per row driving `GalleryServices.NuGet.CreateInstallPackageOperationResolver(...)` /
`.ExecuteOperation(...)` / `GalleryServices.Setup.InstallAddIn(...)`, mirroring
`NuGetPackageViewModel.TryInstallingPackage()`'s call sequence. Update-available detection reuses
`IAddInSetup.GetAddInForNuGetPackage`/`IsAddInInstalled`/`CompareAddInToPackageVersion` exactly as
`NuGetPackageViewModel.IsUpdate` does.

**Known gap, stated explicitly rather than silently dropped: license-acceptance timing is not
byte-identical to OpenDevelop's synchronous WPF flow.** OpenDevelop's `AddInManagerViewModel`
subscribes `AddInManager.Events.AcceptLicenses` and pops a *synchronous* `LicenseAcceptanceView
.ShowDialog()` from inside that handler, because WPF's dialog loop can nest inside an
already-synchronous call stack. WinUI's `ContentDialog.ShowAsync()` cannot be awaited from inside a
plain (non-`async`) event handler without risking a UI-thread deadlock, and the shared engine's
`AcceptLicenses` event is fired synchronously from deep inside `ExecuteOperation`. The workaround:
`OnGalleryInstall` pre-computes which packages need license acceptance (same
`RequireLicenseAcceptance` check, resolved via the same `CreateInstallPackageOperationResolver`
call the engine itself uses) and awaits the `ContentDialog` *before* calling into the engine; the
`AcceptLicenses` handler registered at dialog construction then just honors that already-collected
decision by package ID. Functionally equivalent (the user is always asked and install never
proceeds without acceptance) but not the same timing/order as OpenDevelop's nested-dialog approach.

## Verification (done)

- OpenDevelop's Base (`ICSharpCode.SharpDevelop.csproj`) standalone build: reaches and
  type-checks every moved `AddInManager/*.cs` file with zero errors in them. The build as a whole
  still reports 2 errors, both in `Src/Parser/LanguageServiceParserAdapter.cs`
  (`UnknownCodeContext` not found) - confirmed **pre-existing and unrelated** via `git stash` (same
  2 errors with the AddInManager2 changes stashed away). This is a different pre-existing blocker
  than the `WpfDesign.AddIn` one `nuget.md` and this plan's step 5 anticipated, but the same
  category of problem, and handled the same way: don't fix it, verify around it.
- `AddInManager2.csproj` cannot fully build standalone in this sandbox either, since it
  transitively depends on the same pre-existing-broken Base build. For a real (not stale-dll)
  green build to sanity-check the moved files' integration, the pre-existing broken file was
  temporarily given a local copy of the one class it's missing (`UnknownCodeContext`, which
  legitimately lives in a different, sibling project one layer up) purely for this verification
  pass, then deleted again immediately after - not committed, not part of the real fix. With that
  in place, Base and `AddInManager2.csproj` both built with 0 errors.
- UnoDevelop's own `src/Main/Base/Project/ICSharpCode.SharpDevelop.csproj`: 0 errors (this one
  does *not* hit the `LanguageServiceParserAdapter`/`UnknownCodeContext` issue - it links
  `UnknownCodeContext.cs` in explicitly, same as it links everything else it needs from upstream).
- `PackageManagement.csproj`: 0 errors, after fixing two real bugs the build caught (not
  environment noise): a wrong namespace in a type alias (`AddInManagerServices` lives in
  `ICSharpCode.AddInManager2`, not `.Model` - confirmed empirically via `System.Reflection.Metadata`
  type-table introspection when a naive `strings`-based check gave a false negative), and a missing
  direct `NuGet.Core` reference for `IPackage`. The pre-existing `WpfDesign.AddIn` blocker this
  plan's step 5 and `nuget.md` both anticipated did **not** actually manifest for this project in
  this sandbox - `dotnet build` on `PackageManagement.csproj` reaches real, actionable errors in the
  changed file directly, once given a genuinely fresh (not stale) Base build to reference.
- Full `src/UnoDevelop.slnx`: 0 errors.
- `UnoDevelop.Core.Tests`: 213/215 passing, both before and after (confirmed via `git stash`) -
  the 2 failures are `LspLanguageServiceTests` (missing `pylsp`/TypeScript language-server binaries
  in this sandbox), unrelated to this change and pre-existing. (The plan's "204" baseline is stale;
  the suite has grown since that number was written.)
- `UnoDevelop.IntegrationTests`: run once, alone (`dotnet exec ... UnoDevelop.IntegrationTests.dll`,
  not `dotnet test` - the VSTest adapter is no longer supported on this SDK). Result recorded in the
  session notes at the time this was run.
