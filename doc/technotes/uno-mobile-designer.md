# Uno designer: iOS Simulator and Android Emulator previews

Status: project charter and design (2026-11-03), updated 2026-11-04. Implemented so far: the shared device layer, the shared
iOS/Android host-process classes in `Designer.Remote`, the Uno 6.7.30 baseline, and an opt-in Skia-based iOS head of the Uno design host that
opens a document and renders a frame in a simulator (see section 4a). Not started: the Android head, the `UnoDesignClient`/IDE wiring
(picker, progress card), project assemblies on a device, and packaging. Related: `winui-designer.md` (the Uno/WinUI runtimes and the out-of-process decision),
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

**Not yet started:** the `UnoDesignClient` connection abstraction (it is built on a private
`Connection : DesignerHostProcessClient` and reads capabilities from the handshake), the view-content picker/overlay, preview-target
extraction, and everything Android. Android is also blocked on a healthy emulator and on installing the Android workload the same
way as iOS.

## 5. Risks and open questions

1. **Workloads and build weight.** Mobile TFMs need the `ios`/`android` workloads and Xcode/Android SDK. Opt-in TFMs keep the add-in
   buildable without them; CI needs a macOS lane with both. Xcode 27 currently trails the installed iOS workload (see the
   workspace `AGENTS.md`): the MAUI iOS host builds with `-p:ValidateXcodeVersion=false`; Uno will need the same.
2. **Frame capture on mobile.** Resolved for iOS: the Skia-based head renders `RenderTargetBitmap` frames in a simulator (section 4a;
   Skia is the only renderer Uno 7 is expected to offer on mobile). Still unverified for Android (pixel scale, first-frame timing,
   `UnoPlatformHostBuilder` on Android).
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
