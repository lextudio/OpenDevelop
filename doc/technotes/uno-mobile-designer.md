# Uno designer: iOS Simulator and Android Emulator previews

Status: project charter and design (2026-11-03), updated 2026-11-04. Implemented so far: the shared device layer, the shared
iOS/Android host-process classes in `Designer.Remote`, the Uno 6.7.30 baseline, an opt-in Skia-based iOS head of the Uno design host, and the
IDE side for iOS and Android: the Uno designer offers the shared device picker and progress card for a project that targets iOS or Android, and
previews on a simulator or an emulator end to end (see section 4a). Not started: project assemblies on a device, orientation/device chrome/Cancel
for the Uno designer, debugging on a device (feasibility in section 4b), and packaging. Related: `winui-designer.md` (the Uno/WinUI runtimes and the out-of-process decision),
`designer-common.md` ("Machine-wide devices and the Device Manager", DDP), and the MAUI Designer's design
(`MAUI-Designer/opendevelop-addin/docs/design.md`), which this project follows and reuses.

## 1. Goal

Let a developer open a page of an Uno Platform project and preview and edit it **inside the real iOS or Android runtime**, not
only in the desktop Skia host: native controls and fonts, safe areas, density, conditional XAML namespaces (`ios:`, `android:`),
with the same device picker, Device Manager, progress card and preflight the MAUI designer already has.

Non-goals for the first delivery: touch/gesture input forwarding into the device, hot reload into a running app (the existing
Uno hot reload service is separate), and non-Apple/Android targets (WebAssembly, Windows, Linux are covered by the desktop host).

## 2. What exists (verified in the tree)

- **Desktop host.** `WinUIXamlDesigner.UnoHost` is an Uno.Sdk 6.7.30 (baseline raised from 6.5.31 on 2026-11-04) `net10.0-desktop` Skia app. It installs a headless
  dispatcher (`HeadlessDispatcher`), renders an element that belongs to no window with `RenderTargetBitmap`, encodes the frame
  through SkiaSharp, and speaks DDP through `DesignerChildHost.Run(...)`. It preloads the project's assemblies from `--appbin`.
- **Client.** `WinUIXamlDesigner.UnoDesignHost/UnoDesignClient` (`RecoverableDesignerDocumentHostClient`, DDP method mapping)
  on top of `DesignerHostProcessClient` (`Designer.Remote`: spawn, token handshake, log pump, timeouts, shutdown). Children are
  pooled per `CompatibilityKey` (runtime config, deps file, host dll, app bin path, architecture and dotnet host path; `UnoDesignClient.cs`) and shared by documents.
- **IDE side.** `WinUIXamlDesignerViewContent` (2.4k lines) has no preview-target concept: no device picker, no device frame,
  no session overlay.
- **Shared device layer** (done, in OpenDevelop): `Device`/`DeviceService`/providers, `DevicePreferences`, `DevicePickerBar`,
  `DeviceManagerDialog`, `DesignerSession` + `SessionOverlay`, and the canvas's optional device body (`DesignerDeviceChrome.cs` and `DesignSurface.DeviceChrome` in `src/AddIns/DisplayBindings/DesignerCanvas`).
- **What the MAUI add-in still owns** (and Uno must not copy): the simulator/emulator launch connections, the preview-target
  model and preflight, the device-frame builder and the connection orchestration (generation counter, retry, cancel).

## 3. Design

### 3.1 One host source, three heads

The DDP child logic (`DesignHost`, `DesignRpc`, XAML repair/normalisation helpers) is platform-neutral Uno/WinUI code; only the
process entry and the dispatcher differ. Compile it for three targets:

| Head | TFM | Window | Frame source |
|---|---|---|---|
| Desktop (today) | `net10.0-desktop` | none (headless dispatcher) | off-screen `RenderTargetBitmap` |
| iOS | `net10.0-ios` (simulator RID) | a real `Window` created at start, hosting the design root | `RenderTargetBitmap` of the root |
| Android | `net10.0-android` | a real `Window`/`Activity` | `RenderTargetBitmap` of the root |

Mobile heads cannot be headless: Uno on iOS/Android needs an application window. The host creates one and puts the design root
in it; the app is not interactive, only a render target. **Decision: multi-target `WinUIXamlDesigner.UnoHost`** (Uno's single-project
model) with the mobile TFMs **opt-in** (`UnoDesignHostMobile`, on by default only when the iOS/Android workloads are present), so
a machine without those workloads still builds the add-in and previews on desktop. Platform code sits behind `#if`/partial
classes next to `Program.cs`.

### 3.2 Getting a child into a device

A device child cannot be spawned directly: it is installed into the simulator/emulator and told where to call back. This is
already solved twice in the MAUI client (`IosSimulatorConnection`, the Android connection): `simctl bootstatus/install/launch`
with `--port N --token T` plus a watcher script for the app's lifetime; `adb install`, `am start --es port --es token` and
`adb reverse tcp:N tcp:N`, removing the mapping on exit. **These move to `Designer.Remote`** as designer-neutral
`IosSimulatorHostProcess` and `AndroidEmulatorHostProcess` (subclasses of `DesignerHostProcessClient`, parameterised by bundle
path and bundle id / package and activity). MAUI is changed to use them; Uno uses them from day one.

### 3.3 Client and pooling

`UnoDesignClient` already separates the Uno-specific method mapping from the process lifecycle. Give it a connection factory
so a device connection can replace the desktop one. The pool key gains the device id (a child per (device, project) shared by
documents, like today), so two documents on the same simulator do not boot two apps.

### 3.4 IDE side: reuse, don't copy

`WinUIXamlDesignerViewContent` gains the same row and overlay the MAUI view has: `DevicePickerBar` (with "Manage devices..."),
orientation toggle, `SessionOverlay` over the canvas, device chrome. Everything that is not MAUI-specific in the MAUI add-in
moves to OpenDevelop **before** Uno needs it (see phase 0), so the two designers share one implementation:

- the preview-target model (platform + device + orientation, key format, remembered per project) and its policy;
- preflight (tooling problem, device still exists, host bundle present);
- the device-frame builder (`DeviceChromeProvider`);
- connection orchestration: connection generation, retry, cancel, host-exit recovery, session phase reporting, written as a
  small coordinator that takes "start a client" as a delegate, so each designer supplies only its own client.

The Uno project's targets come from its `TargetFrameworks`/`UnoFeatures` (Uno.Sdk single project): iOS and Android previews are
offered only when the project targets them, exactly as MAUI does.

### 3.5 Project assemblies on a device (the hard part)

The desktop host loads the project's compiled assemblies (`--appbin`) so custom controls and user types render. A device child
runs a different runtime (`net10.0-ios`/`-android`) and needs assemblies built for that TFM.

- **Phase 1 delivers framework-only previews**: XAML with Uno/WinUI controls and the host's own resources render. How an element of an
  unknown project type is shown (a diagnostic on that element, or a stand-in) is **not yet defined for the device host**: the
  desktop host's repair and substitution helpers (`XamlDocumentRepair`, `CompiledXamlControlSubstituter`) are the starting
  point, and the phase-1 spike must decide it. The limit is stated in the preflight/status either way.
- **Phase 3 adds project types**: build the project for the device TFM (can be minutes; cached by project version), then make
  the assemblies reachable: the iOS simulator shares the Mac filesystem, so the child loads them from the build output path;
  an Android emulator needs `adb push` into the app's files directory. Loading reuses the desktop host's preload logic.

## 4. Phasing

| Phase | Content | Exit criterion |
|---|---|---|
| 0 | **Shared extraction, MAUI behaviour unchanged**: host processes (3.2), preview-target model + preflight + remembered target, device-frame builder, connection coordinator move to OpenDevelop; MAUI consumes them | MAUI unit tests and its macOS/iOS/Android/Device-Manager journeys unchanged |
| 1 | **Uno iOS**: iOS head spike first (feasibility: a window, a render, DDP over the simulator loopback), then `UnoDeviceConnection`, picker/overlay in the view content, framework-only previews | A journey opens an Uno page on a simulator, gets a frame, edits a property, switches back to desktop |
| 2 | **Uno Android**: Android head, adb path, emulator | Same journey on an emulator |
| 3 | **Project assemblies** on both platforms (3.5) | A page using a project control renders it on a device |
| 4 | Parity: orientation and real device metrics/safe areas, conditional `ios:`/`android:` namespaces, theme, device frame | Matrix below is green |
| 5 | CI lanes, packaging of the mobile heads (release and CI builds must set `UnoDesignHostMobile=true` explicitly, or the packaged add-in has no device child), docs | Release checklist |

Phase 1 starts with the spike because it answers the biggest unknown (does a real-window Uno iOS head render
`RenderTargetBitmap` frames reliably and start quickly enough) before the rest is built on it.

## 4a. Progress and spike results (2026-11-04)

**Phase 0, host processes: partly done.** iOS is complete and verified; Android is pending (the shared Android class exists and is unit-tested, but MAUI still uses its own copy). The rest of phase 0 (preview-target model, preflight, device-frame builder, connection coordinator) is not started. `IosSimulatorHostProcess` and `AndroidEmulatorHostProcess` are in `Designer.Remote`
(subclasses of `DesignerHostProcessClient`; `DesignerHostProcessClient` gained `OnDisposed`, `RaiseOutputLine`, a settable
`Handshake`, and release-once cleanup that also runs when a start fails or the host is terminated). The MAUI iOS client uses the iOS
class (its iOS and macOS journeys pass); the MAUI Android client still has its own copy until the Android journey can be verified
again. Reviews of this code found real defects worth remembering for any future device host: the watcher's `grep` matched its own
command line (use the `[-]-port` bracket form), `ps` output must not be truncated (`-ww`), an EXIT trap does not run when the
script is killed (remove the `adb reverse` bridge in `OnDisposed` too), and device-side cleanup must not depend on `Dispose` alone.

**Phase 1 spike (iOS): the head is feasible.** A scratch Uno 6.5.31 (native iOS renderer) `net10.0-ios` app (single project, `SkiaRenderer`) was built,
installed into an iOS 26.x simulator, launched, created a `Window` and rendered its content with `RenderTargetBitmap` about 1.5 s
after launch: `1206x2622` pixels (a full-screen frame at 3x) from `RenderAsync`/`GetPixelsAsync`. This answers risk 2 below for iOS:
frame capture works in a real window, so a mobile head can use the same `RenderTargetBitmap` path as the desktop host.

What the spike also showed (each cost time, so they are recorded):

- **Workloads.** Building `net10.0-ios` needs the iOS workload for the SDK that builds it. The system `dotnet` (10.0.200 and
  11.0.100-rc.1) had no `ios` workload; installing it into the user-level dotnet (`~/.dotnet`, SDK 10.0.4xx) works. The newest
  workload (27.1) needs Xcode 27.1; with Xcode 27.0 use the 26.5 manifests (a rollback file such as
  `{"microsoft.net.sdk.ios": "26.5.10315/10.0.100"}` with `--from-rollback-file`) and pass `-p:ValidateXcodeVersion=false`, as the MAUI
  host builds do. `SupportedOSPlatformVersion` must be at least 15.0.
- **Launch screen.** Without a launch storyboard the Apple SDK writes `UILaunchStoryboardName=LaunchScreen` into the app and the app
  dies at start ("Could not find a storyboard named 'LaunchScreen'"). An empty `UILaunchScreen` dict alone did not stop that here;
  a minimal `LaunchScreen.storyboard` (as `InterfaceDefinition`) did. (The MAUI iOS host works with just the empty dict; the
  difference is Uno.Sdk's single-project defaults.) An empty launch screen is also what keeps iOS out of legacy 320x480 mode.
- **Noise.** `Uno.UI.Runtime.Skia.AppleUIKit` logs a `TypeLoadException` on registration at start-up; rendering still works.

**Direction change: Skia-only, Uno 6.7 baseline (2026-11-04).** Uno 7 is expected to be Skia-only; the local `Uno.WinUI` 7.0.0-dev packages
have no `net10.0-ios`/`-android` lib flavours (6.5 has them), which supports that. The spike above used the native iOS renderer, so it is not
the shape to ship. The real iOS head is Skia-based from the start, and the Uno baseline is now 6.7.30 (central pin
`Microsoft.Extensions.DependencyInjection.Abstractions` raised to 10.0.12, because Uno 6.7 brings `Microsoft.Extensions.Logging.Console`
10.0.12). What that changes in practice: with `SkiaRenderer`, `Application` is not a `UIApplicationDelegate`, so `UIApplication.Main(typeof(App))`
fails ("No class named App is loaded", found the hard way when a multi-targeted build silently switched flavours); the entry point is
`UnoPlatformHostBuilder.Create().App(() => new App()).UseAppleUIKit().Build().Run()`, and the managed `Main` args are empty, so
`--port`/`--token` come from `NSProcessInfo.ProcessInfo.Arguments`. The same host-builder pattern will be used for Android.

**Phase 1, iOS head: working end to end (commits `9cdd64144a`, `67254aed11`).** `WinUIXamlDesigner.UnoHost` now multi-targets
`net10.0-desktop` (always) and `net10.0-ios` (opt-in, `-p:UnoDesignHostMobile=true`; needs the iOS workload). `DesignHost` talks to a swappable
`IHostDispatcher` (desktop: the unchanged headless pump; mobile: the window's `DispatcherQueue`) and the mobile head parks the design root in
a real window through the existing `HostVisualRoot`/`HostCommitLayout`/`HostAwaitFrame` hooks. Driven by a scratch program with the shared
`IosSimulatorHostProcess`: installed into an iOS simulator, DDP connected in 8.6 s, `session/open` accepted, a 1170x155 frame at dpi 3 rendered,
clean disposal. Desktop regression check: `WinUIXamlDesigner.UnoHost.Tests` 27 passed, 3 skipped on 6.7.30. Also found on the way: a built app's
`Info.plist` is binary, which the shared bundle-id reader could not read (fixed with a `plutil` conversion).
Caveats: the Windows-only `WinUIXamlDesigner.MicrosoftHost` source-links `DesignHost.cs` and now also links `HostDispatcher.cs`; it could not be
built on the macOS machine used here, so it needs a Windows build before release. Packaging has no mobile path yet (the deploy target is
desktop-only). The iOS head has no automated test yet (the driver was a scratch program).

**Phase 1, IDE side for iOS: working (commits `f85aca3be9`, `ef6002db9b`).** `UnoDesignClient.AdoptDeviceHost` wraps a host that
`IosSimulatorHostProcess` already started (not pooled, no automatic crash recovery; `HostExited` tells the view). `ProjectTargets`
(`Designer.Shell`) reads the project's target frameworks, so the picker only appears for a project that targets iOS. In
`UnoDesignRuntimeHost` (new partial `UnoDesignRuntimeHost.Devices.cs`) the canvas is wrapped in `DevicePickerBar` + `SessionOverlay`
(`IWinUIXamlChrome`; the view and DevFlow reach the device state through `IWinUIXamlDevicePreview`). Switching disposes the current client, runs a
preflight (iOS head bundle present, provider available, device exists), boots/installs/launches/connects through the shared host process, and
the session reaches Ready on the first frame; the device is remembered per project; a device that is hidden or deleted while selected falls
back to the computer. The iOS head ships as an app bundle beside the desktop head (`UnoHost.iOS`, copied by a mac-only deploy target). A
connection generation guards overlapping switches (found in review: the remembered-simulator restore raced the constructor's first desktop
connection and could leave the adopted host leaked). Verified by the integration journey
`AddInTests.WinUIDesigner_UnoOnIosSimulator_RendersOnTheDeviceAndSwitchesBack` against the real IDE and a real simulator (device preview with a
390-wide frame, then back to the 1280-wide desktop preview); it needs `-p:BuildIntegrationFixtures=false` when run locally because the fixtures
otherwise build in the same MSBuild process as the new Uno.Sdk. Known limits: framework controls only on the device, the Android entry in the
picker reads "not available yet", no orientation toggle, no device chrome, no Cancel button, and the remembered-device restore path has no test.

**Phase 2, Android: working.** The same host source builds as `net10.0-android` (opt-in with the other mobile heads). Skia-based Uno on Android
runs as `Microsoft.UI.Xaml.NativeApplication` + `ApplicationActivity`; the IDE starts the activity with `am start --es port N --es token T` (via
`AndroidEmulatorHostProcess`) and the activity turns the extras into the `--port/--token` arguments the other heads get on their command line. Things
that cost time and are worth remembering: `ApplicationActivity` throws "You need to use a Theme.AppCompat theme" without an AppCompat theme
(`Platforms/Android/Resources/values/Styles.xml`); the all-ABI debug APK is about 130 MB and was slow to install, so the head is built for one ABI
(`android-arm64` on an arm64 machine; 74 MB, installs in about 20 s); `EmbedAssembliesIntoApk` is needed for a plain `adb install`; and a small
emulator (1.5 GB AVD) takes tens of seconds to start the Mono app and to render the first frame, which exceeded the host-process defaults (30 s
connect, 30 s request) and made a healthy host look dead, so device hosts now carry their own connect and request timeouts. The IDE side
(`UnoDesignRuntimeHost.Devices.cs`) offers the AVDs of the Android Device Manager provider for a project that targets Android (macOS/Linux; the host
process is a Unix script), starts an emulator that is not running and waits for `sys.boot_completed`, installs the APK deployed beside the desktop
head (`UnoHost.Android`), and uses the same session phases, overlay, remembered device and generation guard as iOS. Verified by the integration
journey `AddInTests.WinUIDesigner_UnoOnAndroidEmulator_RendersOnTheDeviceAndSwitchesBack` (real IDE, real emulator: Ready with a 412-wide frame, then back
to the 1280-wide desktop preview), skipped unless an emulator is running and the APK is deployed. Known limits are the iOS ones (framework controls
only, no orientation/chrome/Cancel) plus: the preview target for Android defaults to 412x915 rather than the AVD's own screen size.

**Machine note (2026-11-04).** On the 16 GB development Mac the memory pressure (swap about 7 of 8 GB) came mostly from two booted iOS simulators
(about 3.5 GB across 356 processes), Spotlight's `mds_stores` (about 1.1 GB) and the emulator; inside the 1.5 GB AVD the Google apps of the
`google_apis` image (Wellbeing, Gboard, Messages, quick search) use about 1 GB that a design host does not need. A healthy emulator for the journeys
wants either more AVD RAM or those apps disabled.

**Not yet started:** project assemblies on a device, orientation and device chrome for the Uno designer, preview-target/preflight extraction from
MAUI (the Uno code duplicates a little of it), device debugging (see below), and packaging (the deploy targets are a developer-machine convenience,
not a release path).

## 4b. Debugging on a device (feasibility, 2026-11-04; nothing implemented)

Asked for alongside the Android work: run and debug the user's Uno app on an iOS Simulator or an Android emulator from the IDE. This is a
separate feature from the designer preview; this section only records what exists and what it would take, so the next session does not
re-derive it. What was observed on this machine is marked "observed"; claims about Mono runtime flags, debug properties, launch environment
variables and PDB handling are from general knowledge of Mono/.NET mobile and are **unverified here**.

**What OpenDevelop has.** `Debugger.AddIn` talks the Debug Adapter Protocol to SharpDbg (`externals/sharpdbg`, `ICorDebug` via ClrDebug), which
debugs **CoreCLR** processes on the same machine (`doc/technotes/debugging.md`). A Uno app on a device does not run on CoreCLR: it runs on the
Mono runtime (observed: the Android APK built by this tree contains `libmonosgen-2.0.so` and `libmonodroid.so`, and the iOS app bundle contains
`libmono-component-debugger.dylib` and `MonoTouchDebugConfiguration.txt`; its crash stack traces earlier in this work showed `libmonosgen-2.0.dylib`
frames; CoreCLR packs for Android are also installed but are not what these builds use), and Mono is debugged with the **soft debugger** (SDB), a TCP protocol between the IDE and a debugger agent inside the runtime. So `ICorDebug`/SharpDbg cannot
attach to it, and the device debugger is a second backend, not a configuration of the first one.

**What is already in the tree.** The `externals/monodevelop` submodule carries `Mono.Debugging.Soft` (the SDB client library, under
`main/external/debugger-libs`), `MonoDevelop.Debugger.Soft` and `MonoDevelop.Debugger.VSCodeDebugProtocol`. The IDE's debugger service is
DAP-shaped (`Debugger.AddIn/Service/Dap`), so the natural shape is a small **DAP adapter over `Mono.Debugging.Soft`**, launched the way SharpDbg is,
instead of teaching the IDE a new debugger API. Whether the submodule's projects build and run under LibreWPF on this stack is unknown.

**What a device debug session needs (each is a work item):**

1. *Build and deploy the user's app for the device TFM.* Needs the iOS/Android workloads and the project's real `net10.0-ios`/`-android` build (the
   same cost as the "project assemblies on a device" item); the IDE's build system has no mobile path today.
2. *Launch with the debugger agent on.* Android: the app is started with debugger properties (the `debug.mono.*` system properties or the
   Android tooling's debug-connect mechanism) and the agent port is reached with `adb forward tcp:N tcp:N` (the mirror of the `adb reverse` the design
   host uses). iOS simulator: the process runs on the Mac, so the agent listens on loopback and the runtime flags are passed through the launch
   environment (`simctl launch` with `SIMCTL_CHILD_`-prefixed variables). The shared `IosSimulatorHostProcess`/`AndroidEmulatorHostProcess` already own
   install, launch and lifetime, so a debug launch is an extension of them (extra arguments/environment, a forwarded port), not new infrastructure.
3. *Debug symbols and source mapping* (unverified). Portable PDBs must reach the debugger, with Mono's debug settings enabled in the app; on
   Android the assemblies are embedded in the APK (observed: `EmbedAssembliesIntoApk`), so the PDBs would have to be located next to the build output.
4. *The adapter and its IDE hook-up:* breakpoints, stepping, stack, variables, exceptions over SDB; a Run/Debug target picker for "this simulator / this
   emulator" (the shared device picker and Device Manager already list them); stop/restart lifecycle; the DevFlow test hooks the debugger tests use.
5. *Hot reload* is a separate, existing feature (`UnoHotReloadService`) and is not part of this item.

**Order I would take, and the risks.** (a) A spike outside the IDE: build the Uno app for the Android emulator, launch it with the agent, and drive
`Mono.Debugging.Soft` from a console to hit one breakpoint - this answers whether the submodule library works here at all. (b) The DAP adapter. (c) The
IDE launch/attach path and picker. (d) iOS after Android (the same adapter, a different launch). Biggest risks: the soft debugger's compatibility
with the .NET 10 Mono runtimes of the installed workloads, the cost of a real device-TFM build in the IDE, and the 1.5 GB emulator, which struggles
with the app alone. This is weeks of work, not a follow-up commit; it should be scoped as its own project with its own charter.

## 5. Risks and open questions

1. **Workloads and build weight.** Mobile TFMs need the `ios`/`android` workloads and Xcode/Android SDK. Opt-in TFMs keep the add-in
   buildable without them; CI needs a macOS lane with both. Xcode 27 currently trails the installed iOS workload (see the
   workspace `AGENTS.md`): the MAUI iOS host builds with `-p:ValidateXcodeVersion=false`; Uno will need the same.
2. **Frame capture on mobile.** Resolved for iOS and Android: the Skia-based heads render `RenderTargetBitmap` frames in a simulator and an
   emulator (section 4a; Skia is the only renderer Uno 7 is expected to offer on mobile).
3. **Startup time.** A cold simulator or emulator takes tens of seconds to over a minute; the progress card and Cancel already
   cover the UX. The Android test AVD has been seen to starve (2 GB), so Android journeys need a healthy emulator.
4. **Conditional XAML.** Uno XAML uses `ios:`/`android:` prefixes resolved at build time; the desktop host strips them
   (`ConditionalXmlnsStripper`). A device host should evaluate them for its own platform instead; this is a phase-4 item and
   needs a design of its own.
5. **Platform limits.** iOS previews need macOS; the provider already reports that as "unavailable" instead of hiding the platform.
6. **Cross-repository migration.** The MAUI add-in builds against an OpenDevelop checkout (`OPENDEVELOP_ROOT`), so each phase-0
   extraction lands in OpenDevelop first and MAUI changes only after it; a MAUI commit that needs an OpenDevelop type must say which
   commit it requires (as the device-layer commits already do), and the add-ins' `LibreWPF.Sdk` pins must stay in step.
7. **Licensing and packaging.** The mobile heads ship inside the add-in; check Uno's redistribution terms before packaging.

## 6. Acceptance matrix (target for phase 4)

| Capability | Desktop host | iOS | Android |
|---|---|---|---|
| Open, render, edit property, undo | done | phase 1 | phase 2 |
| Device picker, Device Manager, progress card, Cancel/Retry | n/a | phase 1 | phase 2 |
| Project custom controls | done | phase 3 | phase 3 |
| Device metrics, orientation, frame | n/a | phase 4 | phase 4 |
| Conditional namespaces per platform | stripped | phase 4 | phase 4 |
| Theme (Light/Dark) | done | phase 4 | phase 4 |

## 7. Decisions taken (change them here if they are wrong)

- Multi-target the existing `UnoHost` project with opt-in mobile TFMs, rather than separate head projects.
- Phase 1 is framework-only; project assemblies come in phase 3.
- Extract the shared pieces from MAUI to OpenDevelop first (phase 0), then build Uno on them; MAUI is the first consumer and
  must stay green throughout.
