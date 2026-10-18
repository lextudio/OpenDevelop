# Auto-update: plan for a visible "Check for Updates" feature

## Status (2026-08-07): UI implemented

All three gaps this technote originally identified are now closed:

- **Help-menu command**: `ICSharpCode.SharpDevelop.Commands.CheckForUpdates`
  (`src/Main/Base/Project/Src/Commands/AboutCommands.cs`), registered in
  `ICSharpCode.SharpDevelop.addin` right before `About`. Always force-checks
  (`UpdateService.CheckForUpdatesAsync`, not the `...IfEnabledAsync` weekly-cadence path) and
  reports through the status bar - "Checking...", then either "A new version is available" with
  a Download button (opens the release URL via `Process.Start`) or "You have the latest version."
- **Notification surface**: both the silent startup check and manual command use
  `SD.StatusBar.SetMessage(...)`. When an update is found, the status bar includes a Download
  button; startup remains silent for failure or "no update".
- **Options panel**: `ICSharpCode.SharpDevelop.OptionPanels.UpdatesOptions`
  (`src/Main/SharpDevelop/OptionPanels/UpdatesOptions.xaml{,.cs}`), registered under
  `/SharpDevelop/Dialogs/OptionsDialog/UIOptions` next to `LoadSave`/`IdeTheme`. One checkbox bound
  to `UpdateSettings.AutomaticUpdateCheckEnabled`, one "Check Now" button reusing
  `UpdateService.CheckForUpdatesAsync` directly (its own inline result text, not the shared banner,
  since it's already inside a dialog).

The earlier live verification for this feature targeted a now-removed update banner. The current
manual path is intentionally status-bar-only. `AddInTests.CheckForUpdatesCommand_RoutesResultToStatusBar`
invokes the Help command and verifies its rendered status-bar result; the update-check service test
continues to cover network/parse outcomes separately.

## Status quo (as of the original plan, superseded by the above)

The **backend is already implemented** and silently wired into startup, but there is
no UI, so it's invisible to users:

- [`src/Main/Base/Project/Src/Updates/UpdateService.cs`](../../src/Main/Base/Project/Src/Updates/UpdateService.cs)
  — queries `https://api.github.com/repos/lextudio/OpenDevelop/releases/latest`
  directly (no feed file, no Octokit), parses `tag_name`, compares against
  `AppUpdateService.CurrentVersion`. Modeled 1:1 on ILSpy's
  `externals/ilspy/ILSpy/Updates/UpdateService.cs` (same method names:
  `GetLatestVersionAsync` / `CheckForUpdatesIfEnabledAsync` / `CheckForUpdatesAsync`).
- [`AppUpdateService.cs`](../../src/Main/Base/Project/Src/Updates/AppUpdateService.cs)
  — `CurrentVersion` from `RevisionClass`, `UpdateStrategy` enum (only
  `NotifyOfUpdates` for now, matching ILSpy).
- [`UpdateSettings.cs`](../../src/Main/Base/Project/Src/Updates/UpdateSettings.cs)
  — `AutomaticUpdateCheckEnabled` (default `true`), `LastSuccessfulUpdateCheck`,
  backed by `ICSharpCode.Core.PropertyService` (ILSpy uses an `ISettingsSection`
  instead — different plumbing, same two fields).
- [`WorkbenchStartup.cs:179-191`](../../src/Main/SharpDevelop/Workbench/WorkbenchStartup.cs)
  — fires `CheckForUpdatesIfEnabledAsync` in the background on startup (weekly
  cadence), but only logs the result via `LoggingService` — nothing reaches the UI.
- `OpenDevelopDevFlowActions.cs` exposes the same check over the DevFlow HTTP API
  for test automation only.

So the gap is exactly what was noticed: **no menu command, no settings toggle UI,
no visible notification.** ILSpy already solved all three; the plan below ports its
UI layer (not its backend, which OpenDevelop's version already covers) rather than
inventing a new design. Roma is not used as a reference — its `Updates/*.cs` are
themselves just a copy of the ILSpy files with the same GitHub-API swap OpenDevelop
already made independently.

## Reference: ILSpy's UI layer

- [`Commands/CheckForUpdatesCommand.cs`](../../externals/ilspy/ILSpy/Commands/CheckForUpdatesCommand.cs)
  — Help-menu command, sends a `CheckIfUpdateAvailableEventArgs(notify: true)` on
  a message bus.
- [`ViewModels/UpdatePanelViewModel.cs`](../../externals/ilspy/ILSpy/ViewModels/UpdatePanelViewModel.cs)
  — subscribes to that message, calls `UpdateService.CheckForUpdatesAsync`/`...IfEnabledAsync`,
  exposes `IsPanelVisible`, `UpdateAvailableDownloadUrl`, `ButtonText`/`Message`
  (switch between "an update is available, download" and "check again"), and a
  `DownloadOrCheckUpdateCommand`.
- A small panel bound to that view model is docked into ILSpy's main window and
  shows/hides itself via `IsPanelVisible`.

OpenDevelop has no MVVM message-bus/TomsToolbox stack wired into its shell, and no
existing "info bar" pad (confirmed — the only transient-notification primitive is
`SD.StatusBar.SetMessage(string, bool highlighted, IImage icon)`), so the panel
itself can't be linked in verbatim. The `.cs` files that are pure logic
(`UpdateService`/`AppUpdateService`/`UpdateSettings`) don't need linking either —
OpenDevelop already has its own equivalents. What's left to build is UI glue: a
command, a settings panel, and *some* way to surface "update available".

## Plan

### 1. Help-menu command

Add `ICSharpCode.SharpDevelop.Commands.CheckForUpdates : AbstractMenuCommand` next
to `AboutSharpDevelop` in
[`src/Main/Base/Project/Src/Commands/AboutCommands.cs`](../../src/Main/Base/Project/Src/Commands/AboutCommands.cs)
(the WPF one actually wired into the addin XML — there's a second, apparently dead,
`AboutSharpDevelop` in `HelpCommands.cs` using the old WinForms `CommonAboutDialog`;
worth confirming which csproj actually compiles that file before touching this area).

`Run()` calls `UpdateService.CheckForUpdatesAsync(new UpdateSettings())` (always
force-checks, since it's a user-initiated action) and reports the result — see
notification mechanism below.

Register it in
[`ICSharpCode.SharpDevelop.addin`](../../src/Main/Base/Project/ICSharpCode.SharpDevelop.addin)
right before the existing `About` entry (~line 1899), same shape as:

```xml
<MenuItem id = "CheckForUpdates"
          label = "${res:XML.MainMenu.HelpMenu.CheckForUpdates}"
          class = "ICSharpCode.SharpDevelop.Commands.CheckForUpdates" />
<MenuItem id = "Separator2" type = "Separator" />
<MenuItem id = "About" ... />
```

### 2. Notification surface

No dockable "info bar" pad exists yet, and building one is more than this feature
needs. Two viable, low-effort options, in order of preference:

1. **Status bar message** for both the silent startup check and the manual
   command: `SD.StatusBar.SetMessage(downloadUrl != null ? "..." : "...", highlighted: true)`.
   Cheap, consistent with existing patterns, but easy to miss and can't carry a
   clickable download link.
2. **`MessageService`-based dialog**, but only for the *manual* "Check for
   Updates" command (never for the silent startup check — that must stay
   non-intrusive, matching the existing weekly/opt-out policy in
   `UpdateSettings`). On startup, if an update is found, fall back to the status
   bar message from (1) rather than popping a dialog unprompted.

Recommendation: do both — silent startup check always uses the status bar;
the manual Help-menu command uses a `MessageService.ShowMessage`/`AskQuestion`
style dialog with a "Download" button that opens `downloadUrl` via
`SD.FileService.OpenFile`/`Process.Start`-based link opener already used
elsewhere in this codebase (check how ReadMe/Web links in the Help menu open, or
`GlobalUtils.OpenLink` in ILSpy, for the existing helper). A full ILSpy-style
dismissible panel is worth doing later if this feature turns out to want more
prominence, but isn't needed for a first cut.

### 3. Options panel

Add an "Updates" panel next to `LoadSaveOptions`/`IdeThemeOptions`
(`src/Main/SharpDevelop/OptionPanels/`), XAML + code-behind `: OptionPanel`
pattern, one checkbox bound to `UpdateSettings.AutomaticUpdateCheckEnabled`, plus
a "Check now" button that reuses the same manual-check path as the Help-menu
command. Register under `/SharpDevelop/Dialogs/OptionsDialog/UIOptions` in the
addin XML, alongside `LoadSave`/`IdeTheme`.

### Order of work

1. Options panel (simplest, no notification-UI design questions).
2. Help-menu command + status-bar/dialog notification.
3. (Optional follow-up) richer notification panel if status bar / dialog proves
   insufficient in practice.
