# Auto-update: plan for a visible "Check for Updates" feature

## Status (2026-08-07): UI implemented

All three gaps this technote originally identified are now closed, using the shell notification
banner built for exactly this purpose (see `doc/technotes/ilspy.md`, "Follow-on infrastructure: a
shell-wide notification banner"):

- **Help-menu command**: `ICSharpCode.SharpDevelop.Commands.CheckForUpdates`
  (`src/Main/Base/Project/Src/Commands/AboutCommands.cs`), registered in
  `ICSharpCode.SharpDevelop.addin` right before `About`. Always force-checks
  (`UpdateService.CheckForUpdatesAsync`, not the `...IfEnabledAsync` weekly-cadence path) and
  reports through `INotificationHost` - "Checking...", then either "A new version is available"
  with a Download action (opens the release URL via `Process.Start`) or "You have the latest
  version."
- **Notification surface**: the silent startup check in `WorkbenchStartup.cs` now calls
  `SD.StatusBar.SetMessage(...)` when (and only when) an update is actually found - never on
  failure or "no update", so it stays non-intrusive. The manual command above uses the banner
  instead, since that path is user-initiated and can afford a more visible surface.
- **Options panel**: `ICSharpCode.SharpDevelop.OptionPanels.UpdatesOptions`
  (`src/Main/SharpDevelop/OptionPanels/UpdatesOptions.xaml{,.cs}`), registered under
  `/SharpDevelop/Dialogs/OptionsDialog/UIOptions` next to `LoadSave`/`IdeTheme`. One checkbox bound
  to `UpdateSettings.AutomaticUpdateCheckEnabled`, one "Check Now" button reusing
  `UpdateService.CheckForUpdatesAsync` directly (its own inline result text, not the shared banner,
  since it's already inside a dialog).

Verified live (2026-08-07), not just by build: added two small DevFlow actions for this
(`od.menu.invoke` - instantiate any `ICommand`/`AbstractMenuCommand` by fully-qualified class name
and call `Execute(null)`, since no existing action can click an arbitrary menu item by id;
`od.notification.status` - read the live `NotificationBannerViewModel`'s state), since neither
existed before. Launched a real (non-interactive-user) instance and drove it headlessly:

- `od.menu.invoke "ICSharpCode.SharpDevelop.Commands.CheckForUpdates"` → `success:true`.
- `od.notification.status` immediately after → `isVisible:true`,
  `message:"You have the latest version."` - a real `GetLatestVersionAsync()` call against
  `api.github.com/repos/lextudio/OpenDevelop/releases/latest` completed and routed through
  `INotificationHost` correctly.
- `/api/v1/ui/tree` confirms this isn't just view-model state: a real
  `System.Windows.Controls.TextBlock` with `Text:"You have the latest version."` is visible at
  real layout bounds (996×14 at y=76, just below the toolbar) - the XAML binding chain
  (`WpfWorkbench.xaml`'s `notificationBar` → `NotificationBannerViewModel`) works end to end, not
  only in isolation.
- `od.menu.invoke "ICSharpCode.SharpDevelop.OptionPanels.UpdatesOptions"` → constructs cleanly
  (fails only on the expected "not an ICommand" cast, not on BAML/XAML load), confirming the
  options panel's XAML compiles and its constructor (which reads
  `UpdateSettings().AutomaticUpdateCheckEnabled`) runs without throwing.

The already-running interactive `/Applications/OpenDevelop.app` instance (a real user session, not
a test artifact) was left untouched throughout - the DevFlow agent's port-9299-in-use fallback
(logs "Port 9299 is already in use; listening on 57804 instead") made this automatic, not something
that had to be arranged.

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
