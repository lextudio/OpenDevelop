# XAML Language Server: one server per XAML framework

Status: **implemented 2026-09-29.** Every XAML runtime has its own server executable; see
[Implementation](#implementation-2026-09-29). The original proposal is kept below for its reasoning.
One refinement over it: the unit is the **runtime**, not the markup dialect. LibreWPF and Microsoft
WPF are both WPF markup, and Microsoft WinUI and Uno are both WinUI markup, but each
reads different assemblies, so each has its own server - they are never mixed.

## The shape

The unit of selection is the **project**, not the file extension and not an abstract
"framework". A project is unambiguously one XAML framework, so:

- Each XAML framework gets its **own** language-server process, which is passed its framework
  profile at launch. Microsoft WPF uses `WinUiXaml.LanguageServer --framework wpf`.
- The XAML **framework addin** owns that server: it starts it and contributes a binding that
  says "this server serves projects of this dialect". Enabling or disabling the addin therefore
  enables or disables the dialect's language support, with no extra bookkeeping.
- The IDE routes each `.xaml` file to the right server **by the owning project**, using the
  same detection result the designer uses. Nothing is guessed twice.

## Why this replaces a plugin architecture

The earlier draft of this note proposed one generic server loading per-dialect plugins. Per-project
selection removes the entire reason for it: a server that only ever serves one framework has no use
for a runtime framework-detection step, a plugin loader, a plugin assembly path, or a registry that
each host has to populate. Each server references only its own framework's profile.

## What already exists (verified)

| Piece | Where | State |
|---|---|---|
| WPF/WinUI/Uno language server | `externals/winappvsce/server/src/WinUiXaml.LanguageServer/` | profile-aware managed server, `net10.0` |
| Framework profiles | `.../FrameworkProfiles.cs` | Microsoft WPF, LibreWPF, WinUI and Uno (the external server's `MsBuildCli.cs` still reads `UseProGpuWinUI`, `externals/winappvsce/server/src/WinUiXaml.Workspace/MsBuildCli.cs` lines 562 and 602; OpenDevelop no longer registers a ProGPU profile since 2026-11-09) |
| Project-based detection | `src/Main/Base/Project/Src/LanguageServices/Xaml/XamlFrameworkDetector.cs` | exists, keyed on the owning project |
| Runtime dialect registry | `.../XamlDialectRegistry.cs` | exists, added for out-of-tree designers |
| Per-file language-service routing | `LanguageServiceRegistry.RegisterExtension(ext, Func<string, ILanguageService>)` | exists — the resolver receives the file name and may return `null` |

The MAUI profile already declares everything a MAUI-aware server needs: the presentation xmlns
`http://schemas.microsoft.com/dotnet/2021/maui`, the `MauiXaml` item names, the
`Microsoft.Maui.Controls.XmlnsDefinitionAttribute` names, the markup-extension namespaces, and
`x:DataType` / `OnPlatform` / `OnFormFactor` completions. **No MAUI detection or metadata code has
to be written** — only a server that consumes it.

## The two facts that shape the implementation

Both verified in `src/Main/Base/Project/Src/LanguageServices/LanguageServiceRegistry.cs`:

```csharp
public bool TryGetService(string fileNameOrExtension, out ILanguageService languageService)
{
    var extension = NormalizeExtension(ExtractExtension(fileNameOrExtension));
    if (_servicesByExtension.TryGetValue(extension, out var entry))
    {
        languageService = entry.Resolve(fileNameOrExtension);   // the resolver gets the FILE name
        return languageService != null;                          // null ⇒ no service
    }
    languageService = null!;
    return false;
}
```

1. **One entry per extension.** `_servicesByExtension[extension] = entry` is a single slot, so a
   second registration **replaces** the first. Two framework addins cannot each register `.xaml`;
   they would clobber each other.
2. **The resolver is per-file and may return `null`,** which means "no service", so
   `GetService` falls back to `FallbackService` (lexical-only highlighting).

Together these mean the routing belongs **inside one resolver**, not in several registrations.

## Design

Addins contribute *bindings* to a small router; the shell registers `.xaml` exactly once and the
resolver asks the router.

```csharp
// contributed by each XAML framework addin, during its own initialisation
xamlLspRouter.Register(new XamlLspBinding(
    dialect: "Maui",                                 // a key from the shared detection result
    launch: () => new LspServerLaunchSpec("xaml", "dotnet", dir, "exec", "maui-xaml-ls.dll", "--workspace", root),
    owns: file => XamlFrameworkDetector.Detect(file).Kind == XamlFrameworkKind.Maui));

// registered once, by the shell
LanguageServiceRegistry.RegisterExtension(".xaml", file => xamlLspRouter.Resolve(file));
```

`Resolve` returns the server for the owning project's dialect, or `null` when no installed addin
claims the file — which lands on lexical-only highlighting rather than a wrong server. That is the
rule `xaml-services.md` already states for designers ("open source-only without offering a wrong
designer"), applied to the language service.

Because the router and the designer both ask the **same** project-based detector, the requirement
that "the designer and the language server must consume the same detection result" holds by
construction, and the three detectors that had accumulated — `XamlFrameworkDetector`, the
per-framework `CanResolveFromProject` methods, and `XamlDialectRegistry` — collapse into one path.

## What MAUI actually requires

1. **A MAUI server bootstrap** — *the only thing still missing.* Mirroring
   `XamlLanguageServer.Wpf/Program.cs`: open the stdio transport, build the Tier-1 snapshot, run the
   Tier-2 MSBuild prewarm, serve. `Program.cs` is now a single
   `XamlLanguageServerHost.RunAsync(…)` call — the host lives in the shared engine
   (`XamlToCSharpGenerator.LanguageServer/Hosting/XamlLanguageServerHost.cs`) and takes the framework
   and its `ITier1ReferenceSet` as arguments, so a MAUI bootstrap is that one call plus MAUI's own
   reference set. There is no framework switch on the command line.
2. ~~**A Tier-1 reference set.**~~ *Done.* `ITier1ReferenceSet`
   (`XamlToCSharpGenerator.LanguageService/Workspace/Tier1/`) is the third capability alongside
   detection and metadata facts: it supplies the assemblies the fast compilation is built from, plus
   anchor types and a `Name`. `NuGetPackagesTier1ReferenceSet` resolves a pack's reference
   assemblies through a `Tier1ReferenceEnvironment`, so a package-shipped runtime needs no bespoke
   code. WPF supplies `WpfTier1ReferenceSet`
   (`XamlLanguageServer.Wpf/Workspace/WpfTier1ReferenceSet.cs`), which resolves, in order,
   `microsoft.windowsdesktop.app.ref` from the NuGet cache, `Microsoft.WindowsDesktop.App.Ref` under
   `packs/`, then the `Microsoft.WindowsDesktop.App` shared runtime, and returns
   `PresentationFramework`, `PresentationCore`, `WindowsBase` and `System.Xaml` from whichever it
   finds. It warns to stderr and yields an empty set rather than throwing when none is present, so a
   machine without the WPF reference pack still starts and still serves.
3. ~~**De-hardcoding the WPF metadata facts.**~~ *Done.* `WpfFastCompilationProvider.cs` is gone,
   superseded by `WpfTier1ReferenceSet`. The presentation xmlns, the `XmlnsDefinition` shim for
   assemblies lacking the attribute, and the WPF CLR namespaces are now framework data rather than
   server constants: the shim in `WpfLanguageFrameworkProvider`/`UnoLanguageFrameworkProvider`, and
   the seed namespaces in the profile's `Tier1SeedClrNamespaces` (`System.Windows`,
   `System.Windows.Controls`, `System.Windows.Controls.Primitives`, `System.Windows.Data`,
   `System.Windows.Documents`, `System.Windows.Input`, `System.Windows.Media`,
   `System.Windows.Navigation`, …). The framework id is likewise no longer a literal in
   `Program.cs` — `WpfTier1ReferenceSet.Name` carries it.

   The MAUI values for all of these already exist in `MauiLanguageFrameworkProvider`; a MAUI
   reference set only has to name MAUI's reference packs. A plugin needs assembly *metadata* only,
   never the framework runtime, so a MAUI server does not need MAUI installed to build or to serve
   completions.

## Decided: who registers the `.xaml` service

**Centralised routing, servers owned by their addins.** `XamlBinding` keeps the single `.xaml`
registration and only routes; each addin registers the server of every runtime it owns with
`XamlLanguageServers` (the WPF designer: Microsoft WPF and LibreWPF; the WinUI designer: Microsoft
WinUI and Uno; the MAUI addin: MAUI). Enabling or disabling an addin therefore
enables or disables its runtimes' language support, and there is no second `.xaml` registration to
clobber the first. See the implementation section below.

## Also worth fixing while in there

- ~~No MAUI tests.~~ Covered from OpenDevelop's side instead: the MAUI addin journey's step 0b
  and a live DevFlow check (MAUI completions stay correct across the Tier-2 prewarm).
- ~~`AvaloniaTypeIndex` lives in the WPF framework project~~: checked, it is the shared engine's
  (`XamlToCSharpGenerator.LanguageService/Symbols`), despite the name. The real defect was that
  the server built and primed it under the Avalonia framework; it is now always the served one.
- ~~The servers are `net10.0-windows`~~: checked, not a constraint. The TFM only names the WPF
  build; the same `wpf-xaml-ls.dll` builds and runs on macOS and serves WPF, WinUI, Uno and MAUI
  there (verified by the integration test above).
- ~~`xaml-services.md` marks Phase 2 partial~~: updated to done.

## Implementation (2026-09-29)

### One server per runtime

| Runtime (key) | Server | Lives in | Registered by | Tier-1 assemblies |
|---|---|---|---|---|
| Microsoft WPF (`MicrosoftWpf`) | `WinUiXaml.LanguageServer --framework wpf` | `externals/winappvsce` | WPF designer addin | Microsoft WPF project evaluation; a bounded `System.Windows` fallback supports the no-reference design-host fixture |
| LibreWPF (`LibreWpf`) | `WinUiXaml.LanguageServer --framework librewpf` | `externals/winappvsce` | WPF designer addin | LibreWPF project evaluation; a bounded `System.Windows` fallback supports an unavailable closure |
| Microsoft WinUI (`MicrosoftWinUI`) | `WinUiXaml.LanguageServer` | `externals/winappvsce` | WinUI designer addin | Microsoft WinUI XAML LSP project evaluation |
| Uno (`Uno`) | `WinUiXaml.LanguageServer --framework uno` | `externals/winappvsce` | WinUI designer addin | `Uno.WinUI` metadata and source-generator output |
| MAUI (`Maui`, the dialect key) | `maui-xaml-ls` | MAUI-Designer `opendevelop-addin/MAUIDesigner.LanguageServer` (MIT) | MAUI addin (bundled under `LanguageServer/`) | MAUI packages |

Microsoft WPF, LibreWPF, Microsoft WinUI and Uno share WinAppVSCE's managed server
and choose a strict profile at launch; MAUI retains its dedicated server. Metadata is read only, so
every server builds and runs on any OS (WinUI needs only a *restored* WinUI project on the machine -
`dotnet restore` works on macOS). A server with no Tier-1 assemblies on the machine runs on Tier 2
alone; it never borrows another runtime's.

The AXSG launchers formerly used for LibreWPF and ProGPU are removed. The WPF designer uses the
separate `externals/WpfDesigner` submodule for its design surface and XAML DOM; it is independent
from the retired language-service path.

### Routing (OpenDevelop)

- `XamlLanguageServers` (Base, `LanguageServices/Xaml`) maps a **server key** to a server dll.
  `ResolveKey(file)` returns a registered out-of-tree dialect's key (MAUI), else the owning
  project's runtime from `XamlFrameworkDetector` (the former `ProGpuWinUI` runtime was removed on
  2026-11-09 with the ProGPU designer host). `GetLaunchSpec(file)` builds
  `dotnet exec <server> --workspace <owning project dir>`.
- Addins register their own servers (`XamlLanguageServers.Register(key, dll)`) from an Autostart
  command; `FindDeployedServer(folder, dll)` locates one under `AddIns/LanguageServices/`.
- `XamlBinding` keeps the single `.xaml` resolver on `LanguageServiceRegistry` (the registry holds
  one per extension) and only routes. A file whose runtime has no server gets no language service
  (lexical highlighting), never another runtime's.
- `LspServiceManager.GetService(file, spec)` keys a process by language, command line and root, and
  logs one Info line per server started, naming the file that caused it.
- The XAML outline goes through `LanguageServiceRegistry` like every other feature; it used to ask
  `LspServiceManager` by extension, bypass routing and start a WPF server for every non-WPF page.
- `--workspace` is the owning project's directory. It used to be OpenDevelop's own source root for
  every document, so Tier 2 compiled one of OpenDevelop's projects.
- `od.xaml.language-server <file>` (DevFlow) reports a file's key, server and workspace.

### Tier 1 (wxsg `LanguageService/Workspace/Tier1`)

- `FastCompilationProvider` is framework-neutral: BCL reference pack + the set's assemblies, plus a
  synthetic `XmlnsDefinition` shim from the profile's `Tier1SeedClrNamespaces`.
- `ITier1ReferenceSet` supplies the assemblies, anchor types (see Tier 2 below) and a `Name` that
  keys the Tier-1 compilation and its disk cache - two servers can share a profile (LibreWPF and
  Microsoft WPF) while reading different assemblies, so a cache keyed by framework id would be
  overwritten by each in turn. `NuGetPackagesTier1ReferenceSet` covers package-shipped runtimes (one
  release, or each package at its own latest when versioned independently, as ProGPU's are).
- The type index is created, persisted and primed **under the served framework**. It used to be
  primed under the Avalonia key while the analysis looked it up under WPF's, so the WPF disk cache
  was never used.
- Version folders sort with prerelease labels compared segment by segment: dropping the label made
  every `0.1.0-preview.*` tie and picked an arbitrary one (LibreWPF got preview.57, not .65).

### Tier 2 must not lose Tier 1

`TieredCompilationProvider` used to upgrade to any Compilation MSBuild returned. An unrestored
project (or, on macOS, Microsoft WPF) still evaluates to one, just without its framework's
assemblies, so completions went empty the moment prewarm finished. It now upgrades only when the
full compilation contains the reference set's **anchor types** (`System.Windows.Controls.Grid`,
`Microsoft.UI.Xaml.Controls.Grid`, `Microsoft.Maui.Controls.Grid`, ...), and logs what is missing
otherwise.

### Profile fixes

- Uno 6 declares its mapping attribute as `Microsoft.UI.Xaml.XmlnsDefinitionAttribute` (not under
  `.Markup`), so the Uno profile indexed no control; the Uno and WinUI profiles now list it.
- `Microsoft.WinUI.dll` and `ProGPU.WinUI.dll` map no xmlns: WinUI's presentation xmlns is implicit
  in its XAML compiler. The WinUI profile names those `Microsoft.UI.Xaml.*` namespaces, and the type
  index falls back to a profile's `Tier1SeedClrNamespaces` when no assembly maps the presentation
  xmlns.

### Verification

- `AddInTests.XamlLanguageService_EachRuntime_UsesItsOwnServerAndControls`: LibreWpfSample,
  MicrosoftWpfSample, UnoXamlSample, WinUISample and MicrosoftWinUISample each
  route to their own server (`od.xaml.language-server`), with `--workspace` = the sample's
  directory, and `<Grid` completes to that runtime's own `Grid` type before and after the Tier-2
  prewarm. With WinUISample restored, the WinUI case exercises Tier 2 on the real Windows App SDK
  (`Microsoft.WinUI.dll` 2.3.6).
- The MAUI addin's journey (step 0b) does the same for MAUI (`Microsoft.Maui.Controls.Label`).
- `XamlDialectRegistryTests`, `LspServiceManagerTests`, `XamlFrameworkDetectorTests` (Base).

## Server lifetime (2026-09-30)

`LspServiceManager` keeps one server per language, command line and workspace root. Until this
date nothing ever stopped one: `LspLanguageService.DisposeAsync` existed but had no caller, and the
cache only grew. Closing a document or a solution left its server running for the rest of the IDE
session, so every workspace opened added a process. One integration run piled up 21 XAML servers,
one per test workspace, because the fixture keeps one IDE for a whole collection.

Measured before the fix (LibreWPF and Uno samples):

| Step | Server processes |
|---|---|
| open the WPF solution, use a .xaml | 1 |
| close every document | 1 |
| switch to the Uno solution, use a .xaml | 2 |
| switch back to WPF | 2 |
| IDE exits normally, or is `kill -9`ed | 0 |

The last row matters: a server exits on its own when stdin reaches EOF, so no process outlives the
IDE. The leak was only ever within one IDE session.

- **Release.** On `SolutionClosedMessageEventArgs` the manager stops every server whose workspace
  root is the solution's directory, one of its projects' directories, or below one. A loose file
  outside the solution keeps its server. A file that asks again starts a fresh server.
- **Stale holders.** `DisposeAsync` marks the instance unavailable first, and does not dispose its
  gates. A caller still holding the old instance gets "no language service", not an
  `ObjectDisposedException`. Disposing twice is a no-op, and the `shutdown` handshake waits at most
  3 s, so a wedged server cannot hold up the solution close.
- **Observability.** `od.lsp.server-count` reports how many servers are held.
  `XamlLanguageService_EachRuntime_UsesItsOwnServerAndControls` asserts it is 1 after each of its
  six solution switches.

### Workspace-root detection

`FindWorkspaceRoot` walks up from a file to the first directory holding a solution or project file.
It used the wildcards `*.sln*` and `*.*proj`, which .NET matches case-insensitively on macOS and
Windows, so any file whose name merely ends in "proj" counted. A hidden temp file
`.com.openai.codex.L2pRoJ` in `$TMPDIR` made the whole temp folder a workspace, and every temporary
project's server was rooted there. `IsWorkspaceFile` now accepts only `.sln`/`.slnx`/`.slnf` and a
letters-only `*proj` extension (`.csproj`, `.fsproj`, ...), and never a dotfile.
