# AddIn distribution through GitHub Releases (design)

**Status (2026-11-17): implemented.** The AddIn Manager lists, installs and updates add-ins from the
GitHub Releases of the repositories the user trusts; `PackOdAddin` in the Addin SDK builds the
`.odaddin`; the NuGet gallery is gone from the AddIn Manager. What is still open is listed at the end
("Open questions" and "Not covered yet").

## Why replace the NuGet gallery

- The only default source is the hard-coded `https://www.myget.org/F/sharpdevelop/`
  (`PackageRepositories.DefaultRepositorySource`), the old SharpDevelop team's feed. It holds none
  of today's add-ins.
- The legacy `NuGet.Core` HTTP engine cannot run on Unix (its settings lock uses a Windows named
  mutex), so on macOS and Linux every remote source is replaced by
  `UnavailableRemotePackageRepository` and reports no packages. Online install and update work only
  on Windows.
- There is no trust model: one global source, no notion of "the publishers I trust".

## Model

1. Each add-in author owns a GitHub repository.
2. Each version is a GitHub **Release** whose tag is `v` followed by a [Semantic Version 2.0.0](https://semver.org/spec/v2.0.0.html)
   (`vMAJOR.MINOR.PATCH[-prerelease][+build]`) and whose assets include exactly one `*.odaddin` file.
3. A user keeps a list of **trusted repositories** (`owner/repo`) in Tools > Options.
4. The AddIn Manager lists the add-ins published by those repositories, offers *Install* for new
   ones and *Update* when a newer release exists for an installed add-in.

No server, index or registry is operated by us. NuGet and dependency resolution are not involved.

## The `.odaddin` asset

`.odaddin` is a zip. It **replaces** `.sdaddin`, which is dropped, not kept alongside: packages built
for SharpDevelop reference assemblies (`ICSharpCode.*` on .NET Framework, WPF, old AvalonEdit) that
the current host no longer provides, so an old `.sdaddin` cannot load and accepting it would only
move the failure from install time to load time. The new extension also marks a package that
`PackOdAddin` produced and that always carries a host range and a tag-matching version.

Code to change: in `AddInSetup.InstallAddIn` ([AddInSetup.cs](../../src/Main/Base/Project/Src/AddInManager/AddInSetup.cs))
replace `case ".sdaddin": case ".zip":` with `case ".odaddin":` alone (`LoadAddInFromZip` stays; a bare `.zip` has no host range or tag check, so it is dropped too);
change the install dialog's filter `AddInManager2.SDAddInFileFilter` in every
`StringResources.*.resx` to `*.odaddin`; update `AddInManager2.Tests/AddInSetupTests.cs`, whose
offline-package test installs a `*.sdaddin`. Opening an `.sdaddin` then reports an unsupported
package type instead of trying to load it. Layout:

```
<Name>.odaddin (zip)
  <Name>.addin        # manifest, exactly one, at the root
  *.dll, resources    # everything the manifest's <Runtime> needs
```

- A bare `.addin` is not a valid release asset: it is only the manifest, the code is the DLLs.
- A release with zero or more than one `*.odaddin` asset is skipped and logged, never guessed at.
- Dependencies on other add-ins are declared with the manifest's `<Dependency>` element, which
  `AddInManager` already evaluates. A dependency that is not installed blocks the install with a
  message; it is not fetched automatically in the first version. `PackOdAddin` packages only
  `InProcess` add-ins; a supporting library (`InProcessDependency`) is never published on its own,
  it ships inside the package of the add-in that needs it.

## Identity and versioning

- The manifest's `<Identity name>` is the add-in's unique key. The repository is only where it comes
  from.
- The release tag is the version source of truth. Before installing, the package's
  `<Identity version>` must equal the tag (without the leading `v`) as a Semantic Version; a
  mismatch rejects the install, and so does a package whose version is not a Semantic Version.
  A tag that is not a strict Semantic Version (`v1.9`, `v1.2.3.4`, `v01.2.3`, `v1.2.3-01`) makes the
  release invisible to the manager; the reason is listed among the skipped releases.
- Versions are parsed and ordered by NuGet's `NuGet.Versioning.SemanticVersion` and `VersionComparer.Default` (no parser of our own),
  which follow Semantic Versioning precedence (spec item 11): the numeric parts, then a
  pre-release before the same version without one (`1.0.0-rc.2` < `1.0.0`), pre-release identifiers
  one by one (numeric ones numerically, alphanumeric ones in ASCII order, a numeric one before an
  alphanumeric one). Build metadata (`+build`) is ignored. NuGet differs from the specification in two corners
  that real versions do not reach: pre-release labels compare case-insensitively, and numeric identifiers that
  do not fit an integer compare wrongly. `PackOdAddin` refuses a release version that does not match the
  specification's own regular expression.
- *Update available* means: the installed add-in with that identity has a version lower, in that
  order, than the newest eligible release of a trusted repository, so an installed `0.1.1-rc1` is
  offered `0.1.1`. An installed version that is not a Semantic Version (the four-part versions of
  the built-in add-ins) counts as its numeric `MAJOR.MINOR.PATCH`.
- The add-in tree itself still compares `System.Version`: `AddIn.Version` is the numeric part of
  the Identity version (`1.2.3` for `1.2.3-rc.1`) and `AddIn.SemanticVersion` the full Semantic Version, or
  null. In a `<Dependency>` or `<Conflict>` a `-` separates a version range (`5.9-5.10`) and is not a
  pre-release.
- Releases flagged *prerelease* on GitHub or whose tag has a pre-release part (or drafts) are ignored
  unless `AddInManager2.ShowPrereleases` is on. Drafts are never shown.

## Host compatibility

The in-process designer protocol changes between OpenDevelop versions and breaks out-of-tree
add-ins at runtime (`../AGENTS.md`, "Keeping an external add-in compatible"). The manifest therefore
declares the host range it was built for with the existing manifest mechanism, no schema change:

```xml
<Manifest>
  <Identity name="Vendor.MyAddIn" version="1.2.3" />
  <Dependency addin="SharpDevelop" version="5.9" />          <!-- or a range: version="5.9-5.10" -->
</Manifest>
```

`SharpDevelop` is the host's own identity; its version is `@SharpDevelopCoreVersion`, the core
version (a `v5.9.0` build is 5.9.x.y). `AddInReference.Check` (`AddInTree.cs`) already enforces the
range when an add-in loads, comparing major.minor.build.revision and ignoring unspecified fields, so
`5.9` accepts every 5.9.x. Every current in-repo add-in says only `<Dependency addin="SharpDevelop"/>`,
which means "any version"; that is fine inside the host's own tree, not for a published package.

`PackOdAddin` refuses a package whose manifest has no `SharpDevelop` dependency with a version or
range (implemented, see below). The manager still has to show an add-in whose range excludes the
running host as *incompatible* and disable Install/Update for it, instead of letting the IDE fail on
the next start (a pre-install `AddInReference.Check` against the package's manifest; part of the
manager step).

## Trust and security

An add-in is arbitrary code in the IDE process with the user's full rights. Trusting a repository
means trusting its owner's whole release pipeline. First-version rules:

- Adding a repository shows a warning saying exactly that and requires confirmation.
- Only `owner/repo` is accepted (no arbitrary URLs); all traffic is HTTPS to `api.github.com` and
  GitHub's asset download hosts.
- Before extraction, the downloaded file's SHA-256 must match the `digest` field GitHub reports for
  the asset. A missing digest blocks the install (no silent downgrade to "unverified").
- Extraction is confined to the add-in's own folder (no `..`, no absolute entries, no symlinks).
- Not in the first version: package signing, publisher verification, a revocation list. State this
  limit in the Options panel text and in the user docs.

## Packaging: `PackOdAddin` in the Addin SDK

The `.odaddin` is produced by a new target in `src/SDK/OpenDevelop.Addin.Sdk/Sdk/Sdk.targets`, not by
a script per add-in repository. The SDK already deploys the `.addin` manifest and trims
host-provided assemblies from `$(OutputPath)` (`OpenDevelopTrimAddinOutput`), so the trimmed output
folder is exactly the content of the package.

`dotnet build -t:PackOdAddin` (name provisional):

- requires `-p:OpenDevelopDistributionBuild=true` (errors otherwise) so `ref/`, XML documentation and
  foreign-platform runtimes are pruned; pick the runtimes to keep with `OpenDevelopDistributionRidFamily`
  or `OpenDevelopDistributionRuntimeIdentifier`;
- zips `$(OutputPath)` (from the add-in's own output, never from `$(OpenDevelopRoot)AddIns/...`,
  which CoreWF and Stride dirty) into `<Name>.odaddin`, the asset name without a version (one asset
  per release; the version lives in the tag);
- fails the build unless: the add-in kind is `InProcess`, exactly one `.addin` is at the root,
  `<Identity version>` is a literal equal to the version being released, and the build has no
  RuntimeIdentifier (implemented);
- requires the manifest to carry `<Dependency addin="SharpDevelop" version="...">` as a version
  (`5.9`) or range (`5.9-5.10`) (implemented). The author states the range; the SDK does not stamp
  one, because it cannot know how far forward the protocol stays compatible, and a wrong automatic
  range would be worse than a missing one;
- **planned, not implemented yet:** support add-ins that carry an out-of-process child host folder
  (MAUI's native host bundles are ~100 MB each; check against GitHub's 2 GiB asset limit and warn
  above a threshold). Today `PackOdAddin` refuses every kind except `InProcess`.

### The SDK needs no separate distribution

A machine that has OpenDevelop installed already has the SDK. **Verified 2026-11-16** on macOS
against `/Applications/OpenDevelop.app`: a project with
`<Project Sdk="Microsoft.NET.Sdk;OpenDevelop.Addin.Sdk">` and no version builds and deploys its
`.addin`, once MSBuild is told where the installed resolver is:

```bash
export MSBUILDADDITIONALSDKRESOLVERSFOLDER=/Applications/OpenDevelop.app/Contents/MacOS/SdkResolvers
dotnet build --disable-build-servers
```

Both `MSBUILDADDITIONALSDKRESOLVERSFOLDER` and the `_NET` variant used by `build/common.psm1` work.
Without the variable the build fails with `MSB4236: The SDK 'OpenDevelop.Addin.Sdk' specified could
not be found`. **Pitfall:** a running MSBuild build server keeps its first answer, so a build
started before the variable was set kept failing after it was set; use `--disable-build-servers`
(or `dotnet build-server shutdown`) in CI and in any diagnosis.

A release workflow therefore does: install the matching OpenDevelop (download the release's
`OpenDevelop-linux-x64.tar.xz` on a Linux runner, the cheapest), export the variable, `dotnet build`,
`PackOdAddin`, `gh release create vX.Y.Z <Name>.odaddin`. The `.odaddin` is thus tied to the
OpenDevelop version it was built with; a new host version means a new add-in release.

Gaps found while verifying:

- The **Windows payload did not ship** `Sdks/` and `SdkResolvers/OpenDevelop.Addin.SdkResolver`
  (confirmed by reading `dist.ps1`: it only handled the NuGet resolver manifest, and
  `SharpDevelop.csproj` references the resolver project for build order only). `dist.ps1` now has
  `Copy-AddinSdkIntoPayload` (called by `Invoke-WindowsPayload` after the data mirror, same layout as
  the macOS bundle) and `Test-AddinSdkInPayload` (called by `Test-WindowsDistributionPayload`, so a
  payload without them fails the package build). The two functions were exercised on macOS with
  PowerShell against the real build output; the Windows packaging path itself has not run, so a
  `release.yml` rehearsal (`gh workflow run release.yml --ref master`) is still the proof.
- **The installed IDE did not ship `OpenDevelop.host-assemblies.txt`**, the list the SDK trims an add-in's
  output by (the file names of the host's own `*.dll`, written by the dev build from SharpDevelop's
  `TargetDir`). Without it the SDK skipped the trim, so a package built against an installed IDE carried a
  second copy of the host and of everything the host's references dragged in (CoreWF: 435 MB). The macOS
  bundle, the Linux payload and the Windows payload now write it at the application root, next to `Sdks/`
  (`Write-HostAssemblyManifest` in `dist.ps1`, the two shell scripts), and `Sdk.targets` derives
  `OpenDevelopHostBin` from its own location (`<root>/Sdks/OpenDevelop.Addin.Sdk/Sdk/` -> `<root>`) when that file
  is there, so an add-in needs no property to find the host. Covered by `AssertInstalledLayout` in
  `tests/OpenDevelop.AddinSdk.Tests`. Releases up to v5.9.1 lack the file; an add-in built against them must
  prune the host payload itself (CoreWF's `WorkflowDesignerPruneHostPayload`).
- CoreWF, Stride and MAUI Designer import `Sdk.props`/`Sdk.targets` by path from `$(OpenDevelopRoot)`
  (a source checkout), not through `Sdk=`. To publish from an installed OpenDevelop they must switch
  to the `Sdk=` form (or point `OpenDevelopRoot` at the install's `Sdks/` layout).

## Rate limits and caching

**No authentication, ever.** The IDE sends no token and stores none: only public repositories are
supported, and nothing secret has to be kept. (So no secret storage is needed; the repository has
none today.)

Unauthenticated GitHub API access is 60 requests/hour per IP (`x-ratelimit-limit: 60`, measured
2026-11-17), which a handful of repositories plus background update checks can exhaust, and an
office or VPN address is shared by every user behind it.

**Measured, and contrary to the usual advice:** without authentication a conditional request that
answers `304 Not Modified` still counts against the limit (`x-ratelimit-remaining` went 59 -> 58 on
the 304). An `ETag` therefore saves bandwidth only, not quota. What actually protects the quota:

- One release-list request per trusted repository per check, nothing more (the list response
  carries every asset's `name`, `size`, `browser_download_url` and `digest` (`"sha256:<hex>"`;
  checked 2026-11-17 on a public repository), so there is no per-asset call).
- Cache the parsed release list on disk; background checks at most once per day (and not at all when
  the cache is younger than that), manual *Refresh* always allowed. Keep the `ETag` anyway to save
  bandwidth.
- Download assets from the release's `browser_download_url`, never through an API endpoint, so
  installing does not spend quota.
- Rate-limit exhaustion is a visible, non-fatal state ("limit reached, retry after HH:mm", from
  `x-ratelimit-reset`), never a crash or an empty list shown as success.

## Settings

Reuse the PropertyService pattern of `AddInManagerSettings`:

| Key | Value |
|---|---|
| `AddInManager2.TrustedRepositories` | `string[]` of `owner/repo` |
| `AddInManager2.ShowPrereleases`, `AutoSearchForUpdates`, `ShowPreinstalledAddIns` | unchanged |

The old `AddInManager2.PackageRepositories` key is ignored once the NuGet path is removed; no
migration is attempted (the only entry users could have is the dead MyGet default).

## Code plan

**NuGet is replaced, not abstracted.** An earlier plan put a small `IAddInSource` in front of the
NuGet code and kept NuGet behind it. That was dropped after reading the code: NuGet types run through
the whole manager, not only the source layer, so an adapter would be written and then deleted.

- `IAddInSetup` signatures carry `IPackage` (`InstallAddIn(IPackage, ...)`,
  `CompareAddInToPackageVersion`, `GetNuGetPackageForAddIn`, `GetAddInForNuGetPackage`).
- `AddInSetup` (929 lines) tracks installed state through NuGet's local repository
  (`RemoveUnreferencedNuGetPackages`) and patches manifests from package metadata
  (`PatchAddInManifest`, `PatchAddInProperties`).
- `INuGetPackageManager` exposes `IPackageManager`, `PackageOperation`, `IPackageOperationResolver`
  (dependency resolution, which is out of scope here).
- Eleven AddInManager2 ViewModels use NuGet types; `NuGetPackageViewModel` and
  `NuGetAddInsViewModelBase` are built around `IPackage` and package operations.

New model, no NuGet types anywhere (in `src/Main/Base/Project/Src/AddInManager/`):

- `AddInRelease`: identity, version, tag, asset name and URL, digest, notes, prerelease flag.
- `IAddInSource` and `GitHubReleaseSource`: list releases of a trusted repository (ETag, cache,
  rate-limit state), download an asset.
- `AddInSetup` keeps what is source-independent (install from `.odaddin`, uninstall, enable, suspend,
  manifest loading, pending operations) and loses everything NuGet. Installed state is "the installed
  add-in's Identity and version"; *update available* compares it with the newest eligible release.

What was built, in the order it was committed (each step reviewed on its own):

0. `PackOdAddin` in the Addin SDK; `.odaddin` replaces `.sdaddin` and `.zip`; filters.
1. The model: `RepositoryReference`, `AddInRelease`, `GitHubReleaseParser`, `ReleaseCache`,
   `GitHubReleaseSource` (listing with an on-disk cache, rate-limit and stale handling; download with
   size and SHA-256 verification).
2. The installer and the service: `OdAddinPackage` (safe unpacking, identity rules, source attributes),
   `AddInSetup.InstallAddIn(file, release)`, `AddInCompatibility` (host range and dependencies, checked
   at install time), `AddInReleaseService` (trusted repositories, entries, updates, one installation
   per repository at a time).
3. The UI: Available and Updates lists from the release service, a Refresh button, the Trusted
   repositories options panel, `UpdateNotifier` on the new source.
4. Removal: the NuGet view models, views, licence flow and paging in AddInManager2; in the shared Base
   `NuGetPackageManager`, `PackageRepositories`, `UnavailableRemotePackageRepository`, the NuGet half
   of `AddInSetup`, the NuGet events, the `NuGet.Core` and `SharpZipLib` references; the legacy
   `AddInManager2.Tests` project (it tested only the removed classes).

Tests: the maintained suite is in `tests/OpenDevelop.Base.Tests` (`AddInReleaseSourceTests`,
`OdAddinPackageTests`, `AddInReleaseServiceTests`, `AddInManagerViewModelTests`, and
`AddInReleaseLoopbackTests`, which runs the whole chain over real HTTP against a local fake of GitHub).
In the running application the add-in's DevFlow probes `od.addin-manager.show`, `.close` and
`.options-panel` construct the real window and options panel and report what they bound.

### Not covered yet

- No journey clicks through the real window (Install, Update, Remove, the options panel's Add) against
  a fake GitHub in the running IDE; the view-model tests and the loopback tests stand in for it.
- The AddIn Manager still shows the NuGet package icon (`AddInManagerImages.Package`, see
  `Resources/license.txt`) for entries from GitHub.
- Strings added for the new UI exist in English only (other languages fall back to English).
- Only the 5 newest releases of a repository are read (one request per repository, by design): a stable release hidden behind more than five newer pre-releases would not be offered.

## Open questions

- Whether the three LeXtudio add-ins (Workflow Designer, Stride, MAUI Designer) become the first
  published releases, and who owns the release workflow that builds the `.odaddin`.
- Whether the Windows payload now ships the SDK: needs a `release.yml` rehearsal.
- A packaged-app journey that builds a project with `Sdk="OpenDevelop.Addin.Sdk"` against the
  installed IDE on each platform (today only verified by hand on macOS).
