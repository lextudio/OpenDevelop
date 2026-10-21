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
| **VSTHRD200/110/100/002** thread-safety analyzer | ~346 | `Microsoft.VisualStudio.Threading.Analyzers`, pulled in transitively - it is not in `Directory.Packages.props`. Mixed: `VSTHRD200` (naming) is cosmetic; `002`/`003` (sync-over-async) and `110` (unobserved awaitable) mark real deadlock/loss risks. Triage per site before suppressing. |
| **MEF006** exported class without an `ImportingConstructor` | 94 | Every occurrence is in the absorbed ILSpy WPF code (`ILSpyAddIn/ILSpy/**`). Third-party source; the honest options are to fix the handful of composition roots or to suppress the code for that addin with this reason recorded. |
| **MSB3277 / MSB3243** reference conflicts | 129 + 94 | MSB3277 is `System.Security.Permissions` inside `externals/wpftoolkit`; MSB3243 is `System.Private.Windows.Core` 11 conflicts reached through the WPF references. Both are dependency-graph issues rather than source defects. |
| **CA1416** platform compatibility | 60 | Windows-only APIs called without a guard. Most legacy sites already carry a project-level `CA1416` NoWarn in their csproj; the remainder are in projects that do not. |
| **CS0618** obsolete members | ~41 | Real, small, and worth fixing individually rather than suppressing. |

## Rejected approach

`CentralPackageTransitivePinningEnabled=true` looked like a one-line fix for the whole vulnerable-
package family, and it did remove every NU1902/NU1903. It also turned ordinary version drift into
build errors: GtkDesigner failed with `NU1109` because its graph wants `SkiaSharp` 3.119.2 while the
central pin is 3.119.1. A repository-wide transitive pin therefore needs every central version to be
maximal and kept maximal; that is a dependency-policy change, not a warning cleanup, so it was
reverted in favour of the explicit per-project `MessagePack` references.
