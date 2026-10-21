# Build warnings inventory and triage

Produced by one full `./dist.macos.sh` run (Release, phases restore → host → addins → payload →
smoke → zip) on 2026-10-21 against `a5546eb00b` plus the working tree of that day. The raw log is
the source of every number below; re-run the same command and repeat the counts to refresh it.

```
grep -cE ": warning [A-Z]+[0-9]+" <log>                       # total warning lines
grep -E ": warning [A-Z]+[0-9]+" <log> | grep -c _wpftmp       # markup-compiler duplicates
grep -E ": warning [A-Z]+[0-9]+" <log> | grep -c /externals/   # raised inside a submodule
grep -oE ": warning [A-Z]+[0-9]+" <log> | sort | uniq -c       # per code
```

## What the raw total means

**8615 warning lines**, which is not 8615 distinct problems:

| Bucket | Lines | Why it is counted separately |
|---|---|---|
| `_wpftmp.csproj` | **2569** | The XAML markup compiler recompiles each project into a temporary project, so every warning a XAML-bearing project has is reported twice. |
| inside `externals/` | **5573** | Raised in a submodule's own dependency graph (cps, vscode-wpf/WpfDesigner, wxsg, wpftoolkit). Fixing them means changing that submodule, not this repository. |
| this repository | **~2237** | The actionable set, minus markup duplicates and submodule projects. Attribution is by the project path in the warning, so a submodule warning surfaced through one of our project references can land in either bucket. |

Top families by code across the whole run: CS8618 1389, CS8632 1014, CS8600 968, CS8625 771,
CS8602 656, CS8603 577, CS8604 458, CS8601 270, CS8622 261, NU1902 251, VSTHRD200 250, VSTHRD110
206, MEF006 188, MSB3277 129, CS8765 124, VSTHRD100 118, VSTHRD002 114, CA1416 114, MSB3243 94,
CS0618 84, NU1903 75, CS8612 70. The nullable families (CS86xx) account for roughly three quarters
of the total.

## Triage, family by family

| Family | Ours | Owner / disposition |
|---|---|---|
| **NU1902/NU1903** vulnerable packages | 203 + 75 | **MessagePack fixed here.** Central pin moved 3.1.8 → 3.1.12, and the 18 projects that resolved the vulnerable 2.5.x transitively now reference it directly, which is the pattern `UnitTesting` and `SharpDevelop` already documented. Verified: those three representative projects build with zero NU1902/1903 and zero NU1109. `System.Security.Cryptography.Xml/Pkcs` remain: every occurrence is in `externals/vscode-wpf` (WpfDesigner, wxsg), whose projects have their own central package policy, so our pins cannot reach them - that is a submodule change. `Tmds.DBus.Protocol` 0.21.2 arrives from `Uno.WinUI.Runtime.Skia.X11` (Linux-only runtime), so it needs an Uno upgrade rather than a pin. |
| **CS8632** nullable annotation outside a nullable context | 902 | **Fixed here.** Fourteen projects used `?` on reference types without enabling the annotation context: GtkDesigner, the three WinUI addin/host projects, MewUIDesigner, WpfDesign.AddIn, `ICSharpCode.SharpDevelop`, FormsDesigner (addin, remote, custom-control fixture), Widgets, ProGPUHost, `SharpDevelop`, ILSpyAddIn. Each now sets `<Nullable>annotations</Nullable>` - the annotations become legal and the warning context stays off, so no new CS86xx appears. Verified: GtkDesigner, FormsDesigner.Host and Designer.Server build with zero CS8632; GtkDesigner had 354. |
| **CS8618 / CS8600 / CS8625 / CS8602 / CS8603 / CS8604 / CS8601 / CS8622 / CS8765 / CS8612** | ~1700 of ours | Projects that already enable the nullable *warning* context on legacy code. Genuine but low-value per site; needs a decision per project (fix the hot spots, or downgrade the context) rather than a blanket change. Not touched. |
| **VSTHRD200/110/100/002** thread-safety analyzer | ~346 | `Microsoft.VisualStudio.Threading.Analyzers`, pulled in transitively - it is not in `Directory.Packages.props`. **Vendored part fixed** (`aa7265310f`): the 82 sites under `ILSpyAddIn/ILSpy` are ILSpy's own async idioms and are disabled for that path in `.editorconfig`. **Ours still open**: 97 sites, dominated by the DevFlow action files and the build engine. `VSTHRD200` (naming) is mechanical - rename to the `Async` suffix; `002`/`003` (sync-over-async) and `110` (unobserved awaitable) need per-site triage. |
| **MEF006** exported class without an `ImportingConstructor` | 94 | **Fixed** (`7651b881ff`): every occurrence is in the absorbed ILSpy WPF code, which uses System.ComponentModel.Composition's single-constructor convention. Disabled for that path only in `.editorconfig` - the build has no MEF006 outside it. |
| **MSB3277 / MSB3243** reference conflicts | 129 + 94 | MSB3277 is `System.Security.Permissions` inside `externals/wpftoolkit`; MSB3243 is `System.Private.Windows.Core` 11 conflicts reached through the WPF references. Both are dependency-graph issues rather than source defects. |
| **CA1416** platform compatibility | 60 | **Fixed** (`b5980a58ad`). The cause was not an unguarded Windows API: CodeQuality and LanguageServices.Roslyn were the only two projects that never linked `src/Main/GlobalAssemblyInfo.cs`, so they did not carry the product's `SupportedOSPlatform("windows")` declaration (nor the product version). Both link it now, with their duplicate attributes removed. |
| **CS0618** obsolete members | ~41 | **Fixed** (`abb0117d04`): the eleven subscription sites now use `MessageBus<...>`; the three compatibility facades and the NuGet template accessor keep the deprecated members under scoped pragmas whose reasons sit next to them. Zero CS0618 in Base, the shell and PackageManagement, and in a full shell build. |

## Transitive pinning: tried, rejected, then adopted

`CentralPackageTransitivePinningEnabled=true` was the obvious one-line fix for the vulnerable-package
family, and it did remove every NU1902/NU1903 - but it turns ordinary version drift into build
errors. On the first attempt GtkDesigner failed with `NU1109` because its graph wants `SkiaSharp`
3.119.2 while the central pin was 3.119.1, so the setting was reverted and the affected projects took
explicit `MessagePack` references instead.

The second attempt raised the conflicting pins first and the whole solution converged:
`SkiaSharp` 3.119.1 -> 3.119.2 (`LeXtudio.DevFlow.Agent.Core` requires it) and `System.Data.SqlClient`
4.8.6 -> 4.9.1 (`Microsoft.PowerShell.SDK` requires it). `dotnet restore OpenDevelop.Mvp.slnx` then
reported **zero NU1109 and zero errors**, and the shell builds clean, so the setting is now on: a
dependency can no longer drag an older release in behind the central pins.

What pinning still cannot reach is a package that a **submodule** declares directly - its own central
policy applies inside that project tree. In the same restore every remaining NU1902/NU1903 is one of
those: `MessagePack` 2.5.302 and `System.Security.Cryptography.Xml/Pkcs` in `externals/cps` and
`externals/vscode-wpf` (WpfDesigner, wxsg), plus `Tmds.DBus.Protocol` 0.21.2 in UnoHost, which comes
from `Uno.WinUI.Runtime.Skia.X11` (a Linux-only runtime). Those need the submodule or Uno to move, or
an explicit direct override validated on the runtime in question; they are not mitigable from this
repository's props.
