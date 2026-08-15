# Adding ASP.NET Core project support

Notes from an investigation into what already exists in the codebase and what
would need to be built to support ASP.NET Core projects (SDK-style, Kestrel,
`dotnet run`) in OpenDevelop.

## 1. Existing AddIns landscape

`src/AddIns/` layout:

```
src/AddIns/
  Analysis/
  BackendBindings/    <- language/project-type addins
    AspNet.Mvc/       <- legacy ASP.NET MVC (System.Web/IIS Express era) - only web-related addin
    CSharpBinding/
    CppBinding/
    FSharpBinding/
    Scripting/
    TypeScript/
    VBBinding/
    WixBinding/
    XamlBinding/
  Debugger/
  DisplayBindings/    <- editors/designers
    ...
    WinUIXamlDesigner/
  Misc/
  VersionControl/
```

There is no `ProjectBrowser` addin (it's core, under
`src/Main/Base/Project/Src/Gui/Pads/ProjectBrowser/`), and no
`DotNetCore`/`SdkStyle`-named addin anywhere. `AspNet.Mvc` is the only
existing web-flavored addin, and it predates ASP.NET Core entirely.

## 2. How a project type is normally wired (CSharpBinding as reference)

- Manifest: `src/AddIns/BackendBindings/CSharpBinding/Project/CSharpBinding.addin`
  registers under `/SharpDevelop/Workbench/ProjectBindings`:
  ```xml
  <ProjectBinding id="C#" guid="{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"
                  supportedextensions=".cs" projectfileextension=".csproj"
                  class="CSharpBinding.CSharpProjectBinding" />
  ```
- Implementation: `src/AddIns/BackendBindings/CSharpBinding/Project/Src/CSharpProjectBinding.cs`
  — `CSharpProjectBinding : IProjectBinding`, whose `LoadProject`/`CreateProject`
  return a `CSharpProject` extending `MSBuildBasedProject`
  (`src/Main/Base/Project/Src/Project/MSBuildBasedProject.cs`, which implements
  `IProject`).
- Project templates (`*.xpt`) live per-addin, e.g.
  `src/AddIns/BackendBindings/FSharpBinding/Templates/FSharp3ConsoleProject.xpt`,
  `src/AddIns/BackendBindings/CppBinding/CppBinding/Templates/ConsoleProject.xpt`,
  `src/AddIns/BackendBindings/WixBinding/Project/Templates/EmptyWixProject.xpt`
  (Wix is a clean second example of the whole pattern, own `.wixproj`
  extension + GUID). CSharpBinding itself doesn't ship its own `.xpt` in this
  snapshot; template descriptors combine with
  `src/Main/SharpDevelop/Templates/Project/` (`ProjectDescriptor.cs`,
  `ProjectTemplateImpl.cs`, `SolutionDescriptor.cs`).
- Run/Debug is generic: all MSBuild-based projects share the same
  debugger/run pipeline in `src/Main/Base` — nothing language-specific needed
  beyond `IProject`/`MSBuildBasedProject`.

**Implication for ASP.NET Core**: since SDK-style `.csproj` (console, class
library, web) is just an ordinary `.csproj` loaded through
`CSharpProjectBinding`, no new `IProjectBinding`/GUID is strictly required —
an ASP.NET Core project *is already openable* as a generic C# project today.
What's missing is ASP.NET Core-*aware* UX and launch support (see below).

## 3. Closest existing analog

No dedicated `DotNetCore` addin exists. The closest analog is the generic
`CSharpBinding` (point 2) — SDK-style projects aren't differentiated at the
addin level at all.

The legacy `AspNet.Mvc` addin
(`src/AddIns/BackendBindings/AspNet.Mvc/Project/AspNet.Mvc.addin`) is
architecturally the closest *precedent* for "layer web-specific behavior on
top of the generic C# project binding" even though its tech stack is
obsolete: it does **not** register its own `ProjectBinding` — it piggybacks
on C# and adds:
- Razor/`.cshtml` syntax highlighting
- "Add Controller/View" context-menu commands
- `WebProjectOptionsPanel`
- `WebBehavior` (`ICSharpCode.AspNet.Mvc.WebBehavior`) keyed on legacy VS
  project-type GUIDs (`{349C5851-...}` MVC, `{E24C65DC-...}` Web
  Application)
- `WebProjectService.cs` — reads `HKLM\SOFTWARE\MICROSOFT\ASP.NET` registry
  keys and shells out to IIS Express (`IISExpressProcessStartInfo.cs`)

None of the runtime-launch code is reusable: ASP.NET Core has no registry
dependency and runs via `dotnet run`/Kestrel, not IIS Express. The
UX-layering *pattern* (menu commands, options panel, syntax highlighting
addition) is reusable; the process-launch plumbing needs to be rewritten
from scratch against `dotnet run` / the built exe + Kestrel, with the
dev-server URL parsed from stdout or `launchSettings.json`.

## 4. WinUIXamlDesigner as a structural reference (out-of-process pattern)

`src/AddIns/DisplayBindings/WinUIXamlDesigner/` tree:

```
WinUIXamlDesigner.AddIn/            (net10.0-windows, in-proc with the WPF host)
  WinUIXamlDesigner.addin           <- manifest: DisplayBindings + Autostart entries
  WinUIXamlDesignerDisplayBinding.cs
  WinUIXamlDesignerViewContent.cs
  WinUIXamlDocumentEditor.cs
  WinUIXamlHost.cs
  WinUIXamlToolbox.cs
  WinUIXamlDesignerDevFlowActions.cs

WinUIXamlDesigner.UnoDesignHost/    (in-proc client/orchestrator)
  UnoDesignRuntimeHost.cs           <- owns the TCP/JSON-RPC client to the child process
  UnoDesignClient.cs
  UnoDesignSurfaceControl.cs
  DesignProtocol.cs                 <- shared wire-protocol types (mirrored below)

WinUIXamlDesigner.UnoHost/          (net10.0-desktop, OutputType=Exe) <- SEPARATE PROCESS
  Program.cs                        <- entry point; loopback TCP port via --port/--appbin args
  DesignHost.cs
  HeadlessDispatcher.cs             <- pumps the Uno dispatcher without a real window
  DesignProtocol.cs

WinUIXamlDesigner.ProGPUHost/       (alternate GPU-based renderer host)
XamlStudio.Toolkit.ProGPU/          (ported rendering toolkit for ProGPUHost)
```

Why the split: WinUI/Uno controls can't run inside the WPF host process
(different UI framework, and Uno installs its own `SynchronizationContext`).
`UnoHost` is a standalone child process that preloads the target project's
assemblies, starts a headless dispatcher, connects back over loopback TCP
*before* calling `Application.Start` (ordering matters — Uno's sync context
needs the dispatcher already pumping), renders to PNG, and streams
state/JSON back. The in-proc side never touches a WinUI type directly.

**Relevance to ASP.NET Core**: this is the template to reuse if we want a
live-preview/debug-launch feature — e.g. spawning `dotnet run` (or the built
exe) as a child process, capturing its Kestrel bind URL from stdout, and
driving a browser preview pane or hitting the app's endpoints for
diagnostics — rather than only shelling out fire-and-forget the way
`IISExpressProcessStartInfo.cs` does today.

## 5. Existing "ASP.NET" / Kestrel / web-project references

- `AspNetCore` / `Kestrel`: **zero hits** anywhere in OpenDevelop's own
  source, docs, or tests.
- `ASP.NET` (case-insensitive) hits are all legacy/unrelated to Core:
  `src/Setup/Files.wxs` (WiX file list for AspNet.Mvc T4 item templates),
  `src/Setup/Chocolatey/sharpdevelop.nuspec:11` ("also supports ASP.NET
  MVC"), `src/AddIns/BackendBindings/AspNet.Mvc/Project/AspNet.Mvc.addin`
  (addin name/description), `WebProjectService.cs:56`
  (`ASPNET_REG_PATH = @"SOFTWARE\MICROSOFT\ASP.NET"`), assorted
  `AssemblyInfo.cs` descriptions, and an incidental EULA file
  (`AspNet.Mvc/Libraries/eula.rtf`).
- `"web project"` hits: `PackageManagement/Project/Src/EnvDTE/References.cs:96`
  and its VB mirror (generic EnvDTE-shim comments), and
  `WebProjectService.cs:48`.

Bottom line: no ASP.NET Core awareness exists today. The legacy `AspNet.Mvc`
addin is useful only as a UX-layering precedent; its runtime-detection code
must be replaced entirely.

## 6. A much better analog exists: MonoDevelop.AspNetCore

The sibling checkout at
`externals/monodevelop/main/src/addins/MonoDevelop.AspNetCore/` already
solves this for a sister MSBuild-based IDE, and should be the primary
porting source instead of designing the launcher from scratch:

```
MonoDevelop.AspNetCore/                  <- core: run config + execution
  AspNetCoreRunConfiguration.cs          <- IRunConfiguration for ASP.NET Core
  AspNetCoreRunConfigurationEditor.cs    <- options-panel UI for it
  AspNetCoreExecutionCommand.cs/Handler.cs/Target.cs
                                         <- builds `dotnet run`/exe command line,
                                            runs it, exposes ExecutionTarget
  LaunchProfile.cs / LaunchProfileData.cs / LaunchProfileProvider.cs
  LaunchProfileDataExtensions.cs         <- full launchSettings.json model +
                                            parser/provider, incl. env vars,
                                            applicationUrl, launchBrowser
  AspNetCoreProjectExtension.cs          <- ProjectExtension hook (MonoDevelop's
                                            equivalent of layering onto the
                                            generic C# project, same idea as
                                            point 3 above)
  AspNetCoreCertificateManager.cs +
  DotNetCoreDevCertsTool.cs              <- dev-cert (HTTPS) detection/creation
  AspNetCoreProjectTemplateWizard.cs
MonoDevelop.AspNetCore.Templating/       <- project/file template conditions
  AspNetCoreSdkInstalledCondition.cs     <- gates templates on SDK presence
MonoDevelop.AspNetCore.Commands/         <- Publish (profiles, folder publish)
MonoDevelop.AspNetCore.Scaffolding/      <- "Add > Scaffolded Item" wizard
MonoDevelop.AspNetCore.Dialogs/
MonoDevelop.AspNetCore.DevCertInstaller/ <- standalone helper exe for dev-certs
Templates/                              <- Startup/Middleware/Controller/RazorPage
                                            code templates (T4-ish .cs generators)
```

This directly replaces the weakest part of the plan below (the
`launchSettings.json`-aware launcher, item 2's third bullet): `LaunchProfile*.cs`
is a ready-made, already-battle-tested parser/model for
`Properties/launchSettings.json`, and `AspNetCoreExecutionCommand/Handler/Target`
is a ready-made `dotnet run` process launcher with Kestrel URL/env handling —
both directly portable to OpenDevelop's `IProject`/run-config abstractions with
mostly mechanical changes (MonoDevelop's `IRunConfiguration`/`ExecutionCommand`
APIs are conceptually close to SharpDevelop's `IProject`/process-start
plumbing, though the exact interfaces differ and need mapping). The
Templating/Scaffolding/Commands/DevCertInstaller addins are optional
stretch scope — worth porting only after the core run/launch path works.

## Proposed plan

1. **Confirm baseline**: verify an SDK-style ASP.NET Core `.csproj` already
   opens/builds/runs correctly today via the existing `CSharpProjectBinding`
   + MSBuild integration (likely yes, since it's just SDK-style `.csproj`).
   If gaps exist (e.g. `Properties/launchSettings.json` not respected,
   multi-target `web` SDK quirks), file them first — they block everything
   else.
2. **New addin `src/AddIns/BackendBindings/AspNetCore/`** (parallel to
   `AspNet.Mvc`, not a fork of it):
   - `.addin` manifest: no new `ProjectBinding` (reuse C#'s); add
     `/SharpDevelop/Workbench/FileFilter` / editor associations only if
     needed for `.cshtml`/Razor (or depend on existing Razor support if
     `AspNet.Mvc`'s highlighting can be shared/generalized).
   - Project templates (`*.xpt`) for "ASP.NET Core Web API",
     "ASP.NET Core Empty", "Razor Pages", "Blazor Server" etc., modeled on
     `FSharpBinding`/`WixBinding`'s `.xpt` + `ProjectDescriptor` pattern.
   - A `launchSettings.json`-aware run/debug launcher: **port** (not
     rewrite) `LaunchProfile*.cs` and `AspNetCoreExecutionCommand/Handler/
     Target.cs` from `externals/monodevelop/.../MonoDevelop.AspNetCore/`
     (point 6) onto OpenDevelop's `IProject`/process-start plumbing —
     `Properties/launchSettings.json` parsing, `dotnet run`/exe launch,
     Kestrel URL/env capture, and browser-launch are all already solved
     there. This replaces `WebProjectService.cs`'s registry+IIS-Express
     logic with a process-based equivalent.
   - Optional live preview pane: adapt the WinUIXamlDesigner out-of-process
     pattern (point 4) — spawn the app as a child process, talk to it (HTTP
     rather than TCP/JSON-RPC) to check readiness/health before showing a
     browser-embedded preview, mirroring the "connect before Application.Start"
     ordering lesson learned there.
3. **Testing**: add integration tests under
   `tests/OpenDevelop.IntegrationTests/` following the existing
   `AddInTests.cs` conventions (open/build/run an ASP.NET Core sample
   project, assert Kestrel URL detection and browser-launch action).
4. **Docs**: update this file with the concrete addin name/GUID and manifest
   snippet once the addin skeleton lands.

## Current state (launch path implemented, 2026-08-15)

- `externals/monodevelop` added as a git submodule (shallow, depth 1, pinned
  to `main` @ `ba01d2d6`) so the MonoDevelop.AspNetCore source referenced
  throughout this doc is available locally for porting/diffing.
- The addin is split into two projects, both wired into `OpenDevelop.Mvp.slnx`
  under `/src/AddIns/BackendBindings/AspNetCore/`:
  - **`AspNetCore.csproj`** (cross-platform core) — plain `Microsoft.NET.Sdk`,
    `net10.0`, no WPF/WinForms, no reference to
    `ICSharpCode.SharpDevelop`/`ICSharpCode.Core` (both require the Windows
    TFM). Holds the launchSettings.json model/logic only. `ILaunchProfile.cs`,
    `ILaunchProfile.cs` and `LaunchProfile.cs` are linked directly from the
    submodule via `<Compile Include>` (not copied). `LaunchProfileData` and its
    extensions are local `System.Text.Json` ports: they preserve arbitrary
    unknown JSON objects, arrays, scalar values, and nulls during edits without
    taking a third-party JSON dependency. The MonoDevelop-dependent
    provider/execution classes are not linked: their OpenDevelop ports are now
    `AspNetCoreLaunchProfileProvider` and `AspNetCoreLaunchCommand`. Together
    they load and save `Properties/launchSettings.json`, preserve global and
    unknown profile settings, create a non-persistent default profile when the
    file is missing, select `Project`/`Executable` profiles, resolve browser
    URLs, build an argv-safe `dotnet run --no-build --project ...` command, and
    merge profile environment variables plus `ASPNETCORE_URLS`.
  - **`Binding/AspNetCore.AddIn.csproj`** (Windows-hosted binding layer) —
    `LibreWPF.Sdk`, `net10.0-windows`, `UseWPF` true,
    matching `CSharpBinding`'s convention (LibreWPF's SDK is what makes
    Windows-TFM projects buildable on macOS in this repo — plain
    `Microsoft.NET.Sdk` fails there with `NETSDK1100`). References the core
    project plus `ICSharpCode.SharpDevelop`/`ICSharpCode.Core`, and owns
    `AspNetCore.addin` (imports `ICSharpCode.AspNetCore.AddIn.dll`) plus the
    `CopyAddInManifest` target. The manifest registers `AspNetCoreProject`, a
    condition that recognizes `Microsoft.NET.Sdk.Web` and ASP.NET Core package
    references, and layers `AspNetCoreProjectBehavior` onto the normal C#
    binding. The behavior participates in OpenDevelop's normal build-before-run
    and debugger pipeline, supplies the profile-derived process start info, and
    ports MonoDevelop's browser behavior by polling the launch URL until Kestrel
    responds before opening the system browser. Set the evaluated MSBuild
    property `AspNetCoreLaunchProfile` to select a non-default profile.
- The conditioned **ASP.NET Core Launch** project-options panel lists runnable
    profiles and edits the selected profile's `applicationUrl`, `launchUrl`, and
    `launchBrowser` values. Saving writes `launchSettings.json` without dropping
    unknown/global settings and persists `AspNetCoreLaunchProfile`; advanced
  environment/custom values remain directly editable in the JSON file.
- The launch panel now includes explicit HTTPS development-certificate status
  and trust controls. `AspNetCoreDevCertificate` is the cross-platform port of
  MonoDevelop's `DotNetCoreDevCertsTool`, updated for the current .NET SDK: it
  uses `dotnet dev-certs https --check-trust-machine-readable`, maps its JSON
  response to `Trusted`/`Untrusted`/`Missing`/`Error`, and invokes
  `dotnet dev-certs https --trust` only after the user confirms. This replaces
  MonoDevelop's obsolete macOS-only helper/external privilege path. Merely
  opening or running a project never changes the certificate store. Linux
  trust still depends on the distribution/browser prerequisites documented by
  the .NET SDK.
- Folder publishing is now available from an ASP.NET Core project's context
  menu as **Publish to Folder...**. The command saves open files, uses an
  argument-safe `dotnet publish` invocation, streams output into the Build
  output pad, reports non-zero exits, and opens the output directory after a
  successful Release publish. The IDE-neutral `AspNetCorePublishProfile` and
  `AspNetCorePublishCommand` also load Visual Studio/MonoDevelop `.pubxml`
  files from `Properties/PublishProfiles`, accepting both `PublishDir` and
  `PublishUrl` plus configuration, target framework, RID, and self-contained
  settings. Its code-only WPF dialog lists saved profiles, shows their
  effective target/RID/self-contained settings, and supports a one-time custom
  output folder. Folder selection goes through `SD.FileService`, whose
  OpenDevelop implementation uses WPF `Microsoft.Win32.OpenFolderDialog`; the
  addin contains no WinForms UI or WinForms dependency. For a saved profile,
  the dialog offers an explicit **Save changes to the selected .pubxml
  profile** choice. The core writer updates configuration/output/framework/
  RID/self-contained/delete flags while preserving unrecognized MSBuild XML;
  one-time custom publishes never create or modify a profile.
- Project templates deliberately use OpenDevelop's existing modern
  `Microsoft.TemplateEngine.IDE` 10.0.302 pipeline rather than adding legacy
  `.xpt` files or copying MonoDevelop templates. The installed .NET SDK already
  contributes the current `web`, `webapi`, `webapiaot`, `mvc`, `webapp`,
  `blazor`, and related templates. The shared WPF New Project window now has
  live search across name, short name, description, and engine tags, so queries
  such as `ASP.NET`, `webapi`, `Razor`, or `Blazor` expose those SDK-owned
  templates. A Base test discovers and instantiates the SDK `webapi` template
  through `TemplateDiscoveryService` and verifies the generated Web SDK
  project, preventing a regression back to a parallel proprietary template
  path.
- **Add Scaffolded Item...** likewise targets the current .NET 10 scaffolding
  path, not MonoDevelop's old in-process wizard or the older
  `dotnet-aspnet-codegenerator` wrapper. It checks for the
  `Microsoft.dotnet-scaffold` global tool and launches its interactive
  `dotnet scaffold` workflow in a real platform terminal rooted at the current
  project, after saving open files. If the tool is absent, the IDE performs no
  installation implicitly and displays the exact official install command.
  Platform command construction is argument-safe for Windows, macOS, and
  Linux, including spaces and apostrophes in project paths.
- `.razor` files now have an ASP.NET Core-owned AvalonEdit syntax definition
  combining HTML, Razor comments/directives/transitions, and embedded C# code.
  It is an embedded addin resource registered for the `.razor` extension, so it
  neither depends on the legacy `AspNet.Mvc` addin nor brings `System.Web.Razor`
  into the modern binding. The checked-in sample contains a compiled Razor
  component, and the full-IDE integration test opens it and verifies that the
  `ASP.NET Core Razor` mode was selected. This is deliberately lexical support;
  completion, diagnostics, navigation, and refactoring require the semantic
  service described below.
- Standalone Blazor WebAssembly projects now use their SDK-defined development
  server launch path explicitly. After the normal restore/build pipeline has
  produced `obj/project.assets.json` and the application DLL, the binding reads
  NuGet's resolved `libraries` and `packageFolders`, locates the matching
  `Microsoft.AspNetCore.Components.WebAssembly.DevServer` package's
  `tools/blazor-devserver.dll`, and launches
  `dotnet blazor-devserver.dll --applicationpath <app.dll>`. No package cache
  root or package version is hard-coded, so custom `RestorePackagesPath` and
  centrally updated package versions continue to work. Projects without that
  resolved package—including Blazor Server and ordinary ASP.NET Core—continue
  to use `dotnet run`. Launch-profile `inspectUri` is now exposed by the model
  and preserved on edits.
- **`Tests/AspNetCore.Tests.csproj`** is an xUnit v3/MTP test executable, also
  included in `OpenDevelop.Mvp.slnx`. It covers profile selection, quoted
  command arguments, environment/URL propagation, browser URL composition,
  missing-file defaults, profile updates, and preservation of global/unknown
  JSON settings. Its integration-category test creates a real
  `Microsoft.NET.Sdk.Web` project, starts it with the generated command, waits
  for Kestrel `/ready`, and kills the complete child process tree.
- **`src/Samples/AspNetCoreSample`** is a checked-in `Microsoft.NET.Sdk.Web`
  sample with `/` and `/health` endpoints and a launch profile targeting the
  latter. It is included in `OpenDevelop.Mvp.slnx` and is the stable fixture for
  the full-IDE test.
- Debug builds expose `od.aspnetcore.status/start/stop` DevFlow actions. They
  deliberately call the loaded project's `CreateStartInfo()`, so the test
  proves AddInTree selected `AspNetCoreProjectBehavior` rather than merely
  exercising the core library directly. Release builds exclude these actions.
- `AspNetCoreAddIn_OpensBuildsAndRunsKestrelSample` opens the checked-in project
  in OpenDevelop, verifies the addin is loaded, builds it through the IDE,
  inspects the resolved command/environment, starts Kestrel, requests
  `/health`, and stops the process tree in `finally`.
- Verified on macOS:
  - `dotnet build .../AspNetCore.csproj -c Debug --no-restore` succeeds;
  - `dotnet build .../Binding/AspNetCore.AddIn.csproj -c Debug --no-restore
    --no-dependencies` succeeds and deploys the addin;
  - `dotnet run --project .../Tests/AspNetCore.Tests.csproj --no-build
  --no-restore` passes 10/10 tests, including a read-only real dev-certificate
  check, real Kestrel startup, and a real folder publish.
  - `dotnet build src/Samples/AspNetCoreSample/AspNetCoreSample.csproj -c
    Debug --no-restore` succeeds with 0 warnings and 0 errors.
  - `dotnet run --project tests/OpenDevelop.IntegrationTests/... --no-build --
    -method OpenDevelop.IntegrationTests.AddInTests.AspNetCoreAddIn_OpensBuildsAndRunsKestrelSample
    -parallel none` passes against a freshly rebuilt OpenDevelop app.

## Remaining parity work

The core run path is no longer a skeleton, but "complete MonoDevelop parity"
still includes independently shippable features that should not be hidden by
that label:

1. Add non-interactive, structured scaffolder parameter pages when the modern
   `dotnet scaffold` tool publishes a stable machine-readable discovery
   contract; retain its interactive CLI as the compatibility baseline.
2. Add semantic Razor support using Microsoft's Roslyn/Razor co-host model.
   This is not a registration of an arbitrary standalone `rzls` executable:
   the maintained Razor sources moved from the archived `dotnet/razor`
   repository into `dotnet/roslyn`, and current editor integrations load Razor
   as a Roslyn language-server component alongside the Razor source generator.
   OpenDevelop's generic LSP transport can carry standard requests, but Razor's
   generated-C#/HTML projections and delegated requests need a Razor-aware
   client adapter. Keep this as an isolated `.razor` service and do not replace
   `CSharpVBLanguageService`, which remains the authoritative in-process Roslyn
   backend for ordinary `.cs` files.

### Razor semantic-service integration boundary

The first semantic slice should ship only when all of these are true:

1. A pinned, redistributable Roslyn language-server build and matching Razor
   component are produced from the same release line; the installed .NET SDK
   is not sufficient because it contains the Razor compiler/source generator,
   but not `Microsoft.CodeAnalysis.LanguageServer.dll` or a Razor language
   server executable.
2. The server is launched with `dotnet exec` from prebuilt artifacts. Build or
   restore output must never share the stdio LSP stream (the same invariant as
   the WPF XAML server registration in `LspServerRegistry`).
3. A Razor-specific client layer handles generated-document synchronization
   and HTML/C# delegation. Registering `.razor` directly against a plain C# LSP
   server would silently produce incorrect coordinates and incomplete results.
4. Capability detection is explicit and failure falls back to the lexical
   mode above. No global tool or server is installed automatically.
5. Integration coverage proves completion and diagnostics in both a C# code
   block and markup, plus navigation from a component attribute into C#.

`csharp-ls` remains a useful MIT-licensed C# server, but its optional `.cshtml`
support is not a complete Blazor `.razor` implementation and it would duplicate
OpenDevelop's existing C# Roslyn backend. It is therefore not the selected
architecture.

3. Complete IDE-owned Blazor WebAssembly breakpoint debugging. The development
   server and its `inspectUri`/`_framework/debug/ws-proxy` endpoint are now
   launched correctly, but OpenDevelop's bundled SharpDbg adapter is a managed
   process DAP adapter, not a Chromium/Blazor DAP client. A compatible browser
   debug adapter must launch Chromium with an isolated remote-debugging profile,
   substitute `browserInspectUri` into the profile template, and connect DAP to
   the Blazor CDP proxy. Until that adapter is integrated, the browser's Blazor
   developer-tools workflow can use the running proxy, while OpenDevelop F5
   cannot yet bind client-side WebAssembly breakpoints.
