# Dual-architecture packaging: where x64, ARM64 and AnyCPU assets must live

OpenDevelop ships **one** Windows payload that runs on both x64 and ARM64. That single decision
is what makes every rule in this document necessary, and it is why an asset in the wrong folder
does not degrade gracefully — it takes the application down before `Main`, with an error message
that names a file which is plainly present on disk.

Read this before changing anything that produces or consumes a NuGet package in the
`openavalon` feed (`LibreWPF.*`, `ProGPU.*`, `LibreWinForms.*`), or before diagnosing a
"Could not load file or assembly" at startup.

## The contract: one payload, two launchers, one managed tree

`dist.ps1` builds the launcher twice and keeps only the `.exe` from each pass
(`Build-OpenDevelopLaunchers`):

| file in the payload | architecture | role |
|---|---|---|
| `OpenDevelop.exe` | win-x64 | apphost; hands off to `dotnet exec OpenDevelop.dll` |
| `OpenDevelopARM64.exe` | win-arm64 | the same, for ARM64 |
| `OpenDevelop.dll` and every other managed assembly | **must load in both** | the actual IDE |
| `runtimes/win-x64/…`, `runtimes/win-arm64/…` | per-architecture | native libraries and the few managed assemblies that cannot be AnyCPU |

The host picks `runtimes/<rid>/` at startup from `deps.json`, so both architectures coexist in one
tree. Everything outside `runtimes/` is loaded by whichever launcher the user double-clicked, and
therefore has to be architecture-neutral.

`dist.ps1` passes `-RuntimeIdentifiers win-x64,win-arm64` explicitly for a reason worth keeping in
mind: without it the SDK resolves native assets for the RID of the machine *running the script*,
which once produced an ARM64-only package on an ARM64 dev box and looked like a successful build.

## The three-folder rule

A NuGet package in this feed makes a different promise in each folder. All three were violated at
once in September 2026, and only one of the three failed in the way the rule suggests.

| folder | rule | a violation shows up as |
|---|---|---|
| `lib/<tfm>/` | **AnyCPU only** — RID-neutral by layout | `FileNotFoundException: Could not load file or assembly 'X' … The system cannot find the file specified`, naming a file that is in the output folder |
| `ref/<tfm>/` | **AnyCPU only** — never loaded, only compiled against | **compile-time** `CS8012: Referenced assembly 'X' targets a different processor` |
| `runtimes/<rid>/lib/<tfm>/` | AnyCPU **or that RID's own architecture**, never another's | same load failure as `lib/`, but only on the other architectures |

Three consequences that are not obvious:

- **The CLR does not JIT around a wrong-architecture managed assembly.** It fails the load and
  reports "cannot find the file specified" for a file that exists. This is the single most
  misleading error in this codebase; treat it as "wrong architecture" until proven otherwise.
- **`ref/` violations are nearly invisible.** `CS8012` is only a warning in most projects, so an
  x64 `ref/` tree looks clean until it reaches a project with `TreatWarningsAsErrors` — in this
  repo, `src/Libraries/AvalonDock/source/Directory.Build.props`. It is latent as well: projects
  already compiled against the previous package do not recompile until something else invalidates
  them, so the break can surface days after the packaging change that caused it.
- **`runtimes/<rid>` duplication is deliberate.** `StageLibreWpfRidManagedTransportPayload` copies
  the whole managed payload into every RID folder so that a RID-filtered restore finds a complete
  set. That is correct only while the payload is AnyCPU: one architecture-stamped file in it lands
  in every RID folder, including the ones where it cannot load.

### What is legitimately architecture-specific

Only these. Everything else must be AnyCPU:

- **`DirectWriteForwarder.dll`** — C++/CLI, so it can never be AnyCPU. It ships under
  `runtimes/<rid>/lib/net10.0/` and, unavoidably, also in `lib/net10.0/` so that a RID-neutral
  restore resolves the reference at all. The `lib/` copy is a landmine by construction; the
  consumer SDK is what neutralises it (see below), and `dist.local.sh`'s audit exempts exactly
  this one path.
- **`System.Printing.dll`** — also C++/CLI. Its *reference* assembly under `ref/` is C# and must
  still be AnyCPU.
- **Native libraries** (`wgpu_native.dll`, `PresentationNative_cor3.dll`, `ijwhost.dll`, …) — these
  only ever live under `runtimes/<rid>/native/` or beside the RID managed assets.
- `LibreWinForms.WindowsFormsIntegration.dll` and a few ProGPU bridge assemblies are *currently*
  architecture-stamped. That is a known defect, not a design decision — see "Known violations".

## Producer side: how each package stays compliant

### LibreWPF managed assemblies — `eng/WpfArcadeSdk/Sdk/Sdk.props`

The Arcade build runs with `-property:Platform=x64` (native and C++/CLI projects need a real
platform), so **every managed project must opt back out** via `PlatformTarget=AnyCPU`. Two
mechanisms do that:

1. `LibreWpfArchNeutralTransportAssemblies` — an explicit whitelist of implementation assembly
   names. A project missing from it silently inherits `x64`. `Microsoft.Win32.SystemEvents` was
   missing and shipped x64 in `lib/` *and* in all three RID folders.
2. A rule matching the **`-ref` filename suffix**, applied unconditionally. The reference projects
   live at `src/Microsoft.DotNet.Wpf/src/*/ref/<name>-ref.csproj`, so `MSBuildProjectName` is
   `WindowsBase-ref`, which the whitelist above can never match. This is a wider rule on purpose:
   a reference assembly is compiled against and never loaded, so its machine field carries no
   information and can only do harm.

`PresentationBuildTasks` is deliberately excluded — it is an MSBuild task assembly built as x86.

### The per-RID payload — `eng/progpu-wpf-windows-managed-runtime.ps1`

This script is the **only** producer of `artifacts/windows-managed-runtime/<rid>/`, from which the
transport package takes its per-RID `PresentationCore` and `DirectWriteForwarder`. Two things
about it matter here:

- It builds `win-x64` and `win-arm64`. **`win-x86` is not produced or shipped**: the pinned
  `Microsoft.NETCore.App.Host.win-x86` pack cannot be restored from this feed (indexed, but 401 on
  content, so NuGet does not fall back), which makes `DirectWriteForwarder` unbuildable for x86
  with `NETSDK1114`. A `runtimes/win-x86` folder missing those two assemblies would be worse than
  no folder at all, because NuGet would select that incomplete set.
- **It must run before the RID-neutral transport build, not after.** Each per-RID pass stages its
  output into the *shared* `artifacts/packaging/Release/LibreWPF.Transport/lib/net10.0`, so
  whichever RID ran last decides the architecture of the RID-neutral `lib/` tree.
  `dist.local.sh` already orders it correctly (per-RID script → clear staging → build transport →
  pack); a hand-run sequence that inverts it silently ships an ARM64 `lib/DirectWriteForwarder.dll`.

### ProGPU packages — `external/ProGPU/eng/progpu-pack.sh`

These ship **only** `lib/net10.0/X.dll` — no `runtimes/` tree at all — so they must be AnyCPU,
full stop. The pack script passes no `Platform`, and each project's AnyCPU output does exist at
`src/<Project>/bin/Release/net10.0/`. Alongside it sit `bin/x64/` and `bin/ARM64/` from the
LibreWPF graph builds, and a pack that picks one of those ships an assembly that works on exactly
one architecture.

## Consumer side: `ProGPU.Wpf.Sdk.targets`

Two targets in the SDK decide what actually lands in an application's output folder, and they run
in this order:

1. `_ProGpuWpfSdkCopyManagedTransportRuntimeAssets` — copies the transport payload.
2. `_ProGpuWpfSdkCopyPackageRuntimeAssets` — flattens `@(RuntimeCopyLocalItems)` onto the app base,
   **after** the first, and therefore wins any conflict.

Both now prefer `runtimes/<effective rid>/` over `lib/` for every file that exists in both, which
is what makes the unavoidable `lib/DirectWriteForwarder.dll` harmless. Getting there took three
corrections, each of which looked right and was not:

- **Do not gate the preference on `'$(RuntimeIdentifier)' == ''`.** Real consumer projects evaluate
  to a concrete RID, so that guard skips exactly the branch that does the damage.
- **Apply it in the deps target too, not just the copy target.** `RuntimeCopyLocalItems` is not
  only the deps file's input; it is what target 2 physically copies. Fixing only the copy target
  put the correct per-RID assembly in place and let `lib/`'s copy overwrite it moments later — an
  x64 apphost beside an ARM64 `DirectWriteForwarder`.
- **Do not derive the per-RID root from `PkgLibreWPF_Transport` or the NuGet root alone.** Neither
  is reliably set inside those targets; the `lib/` root is commonly reached instead by rewriting a
  `ref/` path. With both empty the composed path is empty, the preference silently does nothing,
  and no diagnostic appears. Derive it from whichever `lib/` root was actually resolved.

**`PlatformTarget` is the wrong signal for "what architecture will this run as."** It reads like
the right one, but a project with `ProGpuWpfUseCurrentRuntimeIdentifier=true` derives its RID from
the architecture of the *building process*: the x64 IDE on an ARM64 machine emits an x64 apphost
while `PlatformTarget` still evaluates to `arm64` from the command line. Use `RuntimeIdentifier`,
then `NETCoreSdkRuntimeIdentifier`.

### Never copy out of `ref/`

Both targets filter any item whose resolved path contains a `\ref\` segment. A reference assembly
has no method bodies, so the runtime refuses it with
`BadImageFormatException: Reference assemblies cannot be loaded for execution`, thrown from the
bootstrap before any application code runs. This is not hypothetical: the roots above are partly
derived by rewriting a `ref/` path into a `lib/` one, and when the anchor property arrives as
`"<…>\ref\net10.0\X.dll;<…>\ref\net10.0"` the semicolon makes MSBuild read the `Include` as **two**
items, the first being a reference assembly. A 96 KB `WindowsBase.dll` then replaces the 1 MB
implementation in `bin`.

## The trap that hides all of the above: NuGet's global cache

**NuGet never re-extracts a package whose id + version it already has.** The local feed republishes
the same preview version on every run, so a corrected package can sit in `artifacts/local-feed`
while every consumer keeps building against the stale extracted copy in
`~/.nuget/packages/<id>/<version>/`.

This produced a failure that survived four rounds of "fixed and verified": 25 packages were stale,
and the fixture application kept loading ARM64 `ProGPU.*` assemblies into an x64 process long after
the feed contained correct AnyCPU ones.

**Find them by comparing timestamps, not contents:**

```bash
cd openavalon/artifacts/local-feed
for f in *.nupkg; do
  id=$(echo "$f" | sed -E 's/\.[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+\.nupkg$//')
  ver=$(echo "$f" | sed -E 's/^.*\.([0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+)\.nupkg$/\1/')
  c="$HOME/.nuget/packages/$(echo "$id" | tr 'A-Z' 'a-z')/$ver"
  [ -d "$c" ] && [ "$(stat -c %Y "$c")" -lt "$(stat -c %Y "$f")" ] && echo "STALE: $id/$ver"
done
```

Delete every directory it names, then restore. Do this after **every** `dist.local.sh` run, not
only when something looks wrong.

## Diagnosing a suspected architecture problem

Work from evidence, in this order. Every step here caught a real defect that the previous step
could not see.

1. **Read the PE machine field of the file named in the error**, in the consumer's `bin` — not in
   the package, and never by its presence or size alone:

   ```powershell
   $fs=[IO.File]::OpenRead($dll); $br=New-Object IO.BinaryReader($fs)
   $fs.Position=0x3C; $o=$br.ReadInt32(); $fs.Position=$o+4; '0x{0:X4}' -f $br.ReadUInt16()
   # 0x014C = AnyCPU/x86   0x8664 = x64   0xAA64 = arm64
   ```

2. **Read the apphost's machine field too.** The mismatch, not the assembly, is the bug. An x64
   `.exe` beside an ARM64 dependency is the signature.
3. **Run the feed audit**: `dist.local.sh`'s "Checking payload architectures" step covers `lib/`,
   `ref/` and every `runtimes/<rid>/lib/` entry, and prints `arch-stamped:` or
   `wrong-arch for <rid>:` per file. It reports rather than fails.
4. **Compare the feed package against the extracted cache copy** (previous section). They differ
   more often than seems possible.
5. **Trace the copy** when the file in `bin` is not the one you expect:
   `dotnet msbuild <project> -t:_ProGpuWpfSdkCopyManagedTransportRuntimeAssets -v:n` prints every
   `Copying file from …`. `Copy` preserves the source timestamp, so **the timestamp on the file in
   `bin` identifies which source folder it came from** — that is how `lib/` was caught overwriting
   `runtimes/win-arm64/`.

### The IDE builds with a different SDK than the command line

`OpenDevelop.exe` is x64, so the projects it builds resolve the **x64** SDK
(`C:\Program Files\dotnet\x64\sdk\…`), while a shell on an ARM64 machine uses the ARM64 one. The
same project therefore produces an x64 apphost under the IDE and an ARM64 one from the terminal.
Confirm which was used by reading `runtimeIdentifierGraphPath` out of the project's
`obj/project.assets.json`; do not assume the terminal reproduces what the IDE did.

This is also why "it works when I build it myself" is not evidence about an IDE-driven build.

### What this looks like from the outside

Two symptoms that read like completely different bugs, both caused by an architecture mismatch in
a launched application:

- **WPF Hot Reload sits at `state: "Connecting"` forever**, `lastError` is empty, and the agent log
  named in `diagnostics.agentLog` is never created or stops right after
  `Read line: {"kind":"query","query":"agent.ready"}`. Even that constant-valued query goes through
  `Dispatcher.Invoke`, so a UI thread that died during startup hangs it. The real exception is in
  the IDE's own stdout, `%TEMP%\od-test-logs\od-app-*.log`, as `[stderr] Unhandled exception`.
- **`Startup hook assembly '…WpfHotReload.Agent.dll' failed to load … The assembly architecture is
  not compatible with the current process architecture`** — the agent was built with a bare
  `dotnet build` instead of `./build.ps1`, so it was stamped for the build machine. Rebuild it with
  `./build.ps1 WpfHotReload.Agent` and confirm `0x014C`. This is the same trap
  [build-from-source.md](build-from-source.md) documents for every other project.

## Known violations, still open

`dist.local.sh`'s audit reports these today. They are producer-side bugs in packages that ship
**only** `lib/net10.0` and therefore have no correct architecture to fall back to:

| package | file | stamped |
|---|---|---|
| `ProGPU.DirectX` | `lib/net10.0/ProGPU.DirectX.dll` | arm64 |
| `ProGPU.Avalonia`, `ProGPU.Backend.Dawn`, `ProGPU.Layout`, `ProGPU.Media`, `ProGPU.Media.Scene`, `ProGPU.Virtualization`, `ProGPU.WinUI` | `lib/net10.0/*.dll` | x64 |
| `LibreWinForms.WindowsFormsIntegration` | `lib/` and `ref/` | arm64 |

Each will fail on the architecture it was not built for. The fix is the same in every case: pack
from the project's AnyCPU output (`bin/Release/net10.0/`), not from `bin/x64/` or `bin/ARM64/`.

## Rebasing openavalon onto upstream

`openavalon/LibreWPF` is `lextudio/LibreWPF`, branch `openavalon`, which carries fork
customizations on top of `dotnet/wpf`. Two of the four files that implement the rules above are
**upstream files we patch**, so they are what a rebase will conflict on.

| file | origin | rebase risk |
|---|---|---|
| `eng/WpfArcadeSdk/Sdk/Sdk.props` | upstream (`Onboarding new build infrastructure (#473)`) | **high** — upstream edits this file; our AnyCPU whitelist and `-ref` rule are appended blocks |
| `packaging/Microsoft.DotNet.Wpf.GitHub/Microsoft.DotNet.Wpf.GitHub.ArchNeutral.csproj` | fork copy of an upstream project | medium — follows upstream renames |
| `eng/progpu-wpf-windows-managed-runtime.ps1` | fork-only | low |
| `packaging/ProGPU.Wpf.Sdk/targets/ProGPU.Wpf.Sdk.targets` | fork-only | low |

### Steps

1. **Record the current payload shape before touching anything**, so you can tell a rebase
   regression from a pre-existing one:

   ```bash
   ./dist.local.sh 2>&1 | tee /tmp/pre-rebase-audit.log   # keep the audit section
   ```

2. **Fetch upstream and rebase the `openavalon` branch.** Take upstream's version of
   `eng/WpfArcadeSdk/Sdk/Sdk.props` on conflict and **re-apply our two blocks by hand** rather than
   resolving line by line — they are self-contained and heavily commented, and a half-merged
   whitelist is the failure mode that produces an architecture-stamped `ref/` tree without any
   build error.
3. **Check the whitelist against reality, do not assume it is still complete.** Upstream adds and
   renames assemblies; anything new that ships in `lib/` must be listed:

   ```bash
   # every managed transport project upstream now builds
   ls src/Microsoft.DotNet.Wpf/src/*/*.csproj | xargs -n1 basename
   # compare against LibreWpfArchNeutralTransportAssemblies in eng/WpfArcadeSdk/Sdk/Sdk.props
   ```

   The `-ref` rule needs no maintenance — it matches by filename suffix — but confirm upstream has
   not moved the reference projects out of `*/ref/`.
4. **Re-check the per-RID script against upstream's project list.** `$transportProjects`,
   `$themeProjects` and `$transitiveAssemblies` in `eng/progpu-wpf-windows-managed-runtime.ps1` are
   hand-maintained copies of the build graph. A project upstream added will simply be missing from
   `artifacts/windows-managed-runtime/<rid>/`, and the transport pack's own `Error` checks only
   cover `PresentationCore`, `DirectWriteForwarder` and `ijwhost`.
5. **Rebuild the feed in the documented order and clear the NuGet cache**:

   ```bash
   ./dist.local.sh                       # per-RID script runs BEFORE the transport build
   # then delete every stale ~/.nuget/packages entry (see the script in this document)
   ```

6. **Verify with the audit, then with a real consumer**: the audit must show no new
   `arch-stamped:` or `wrong-arch for <rid>:` entries beyond the known list above, and
   `OpenDevelop.exe` (x64) must start on an ARM64 machine. The audit alone is not sufficient — it
   cannot see the NuGet cache, which is where the last four failures actually lived.

### Things upstream will not warn you about

- Upstream builds with `Platform=x64` and does not care that `lib/` ends up x64-stamped, because
  upstream ships per-RID packages. Our arch-neutral packaging is the fork's constraint, so any
  upstream change to `Platform`/`PlatformTarget` handling needs re-testing on both architectures
  even when it looks unrelated.
- Upstream's `ref/` projects have no reason to be AnyCPU either. Expect the `-ref` rule to be
  dropped by an incautious conflict resolution.

## Related

- [librewpf.md](librewpf.md) — the local feed, version tracks, and the traps around repacking.
- [build-from-source.md](build-from-source.md) — why `./build.ps1` exists and what a bare
  `dotnet build` gets wrong.
- `openavalon/AGENTS.md` — the same rules stated for the producing repository.
