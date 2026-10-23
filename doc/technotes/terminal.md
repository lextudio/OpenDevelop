# Integrated terminal

Status: working prototype (issue #20, "Terminal Toolbox"), `src/AddIns/Misc/Terminal`. See "Prototype status" below.

Goal: parity with the VS Code integrated terminal for everyday use — several terminals, split, a
shell picker — in OpenDevelop's classic toolbar-driven pad, and nothing beyond that. Not a goal: persistent sessions across restarts,
terminal profiles marketplace, shell-integration decorations, GPU rendering, remote/SSH terminals.

## What existed before this work (the survey the design started from)

- No terminal pad. `ICSharpCode.SharpDevelop.addin` defines `OutputPad` (read-only compiler output) and the
  Debugger/PackageManagement/Scripting console pads are line-oriented REPLs on a text editor: nothing
  interactive in the sense of a PTY.
- Project Browser had "Open in Terminal" (`ICSharpCode.SharpDevelop.ProjectBrowser.addin`), which launched
  an *external* terminal app. It now opens the integrated terminal through `ITerminalHost` when this
  add-in is loaded and keeps the external app as the fallback.
- `src/Libraries/AvalonDock/source/AvalonDockCodeApp/ViewModels/TerminalViewModel.cs` is a demo that
  pipes stdin/stdout of a `Process` into a `TextBox`. No PTY, no escape sequences: unusable for
  `vim`, `less`, `dotnet watch`, colours, or line editing. Do not build on it.

## Layers

The UI is the classic OpenDevelop pad: one toolbar on top, one terminal view below. No tab strip and
no side list; the toolbar's session dropdown is the only session switcher.

```text
TerminalPad (AvalonDock tool pad, Bottom)
 ├─ Toolbar          [Session ▾] [New] [shell ▾] [Split] [Kill] [Clear] [Copy] [Paste] [Settings]
 └─ Group            panes side by side (Grid + GridSplitters); one group per New, Split adds a pane
      └─ Pane        TerminalView + TerminalSession
           TerminalView      draws an immutable TerminalSnapshot, turns keys/mouse/wheel into PTY input
           TerminalSession   one shell:
             ├─ libghostty-vt terminal   (LibGhostty.Net) VT state, scrollback, key/mouse encoders
             └─ Porta.Pty pseudo-terminal   the shell's PTY (Unix helper; ConPTY on Windows)
```

The VT state and the PTY are separate libraries behind `TerminalSession`; nothing outside that class
touches native handles.

### Build vs. reuse

Decision: do not write a VT emulator, and do not write a PTY layer.

- VT state: `LibGhostty.Net` (NuGet, third-party wrapper of Ghostty's `libghostty-vt` C ABI), low-level
  `GhosttyNativeLibrary` API (`IntPtr` handles): `TerminalNew/Write/Resize`, render state (row iterator,
  row cells, style/colour), key/mouse/paste encoders, title, alternate-screen query, write-back callback.
- PTY: `Porta.Pty` (`PtyProvider.SpawnAsync` → `ReaderStream`/`WriterStream`, `Resize`, `Kill`,
  `ProcessExited`). NOT LibGhostty.Net's own PTY half: its 1.0.2 `Resize` writes a garbage winsize on
  macOS arm64 (`stty size` after `Resize(96, 7)` printed `59120 27655`; zsh then thinks the terminal is
  tens of thousands of columns wide and its prompt-fill blanks the screen). Porta.Pty returned `7 96`.

Both are hidden behind `TerminalSession`, so either can be swapped (managed fallback: `XTerm.NET`).
Rendering is ours: `TerminalView`, a WPF `FrameworkElement` that draws the cells of a snapshot.

Rejected: embedding xterm.js (web runtime), `alacritty_terminal`/`wezterm-term` (Rust, no C ABI),
`VirtualTerminal` (WPF backend is Windows-only), godot-xterm as used by SharpIDE (Godot-only).

#### Initial spike (console app, before the add-in)

A console app (`LibGhostty.Net` 1.0.2, net10.0, `-r osx-arm64`) started `/bin/zsh -c ...` in a 60x8
PTY, fed the bytes into a Ghostty terminal and read the grid back through the render state:

- Text, SGR foreground (`\e[31m`, `\e[1;32m` resolved to palette RGB), PTY size (`stty size` = `8 60`),
  and exit code all correct. PTY and native `libghostty-vt.dylib` load fine on osx-arm64.
- `$TERM` was empty: the host must set `TERM=xterm-256color` itself (already listed under Environment).
- Not yet exercised: key/mouse encoders, `vim`/alt screen, scrollback viewport, resize while running,
  the write-back callback (`TerminalOptionWritePty`, needed for device-attribute replies), wide
  characters, rendering speed.

Known gaps in the package (1.0.2):
- No `win-arm64` native assets (win-x64, linux-x64/arm64, osx-x64/arm64 only). OpenDevelop ships
  `OpenDevelopARM64.exe`, so Windows ARM64 needs the managed fallback or an upstream build.
- Building without a RID on macOS fails (`MSB3030`: it copies `build/native/runtimes/x64/OpenConsole.exe`
  which is not in the package). Set a `RuntimeIdentifier`, or exclude the Windows assets, in the add-in
  project; check how our `dist.ps1` payloads handle per-RID native assets.
- Third-party, young, and tracks an upstream whose C API was "early testing quality" when announced.
  Pin the version and keep the fallback.

## Prototype status (macOS arm64, 2026-10-22)

Files: `src/AddIns/Misc/Terminal/Project/` — `Terminal.addin` (pad, toolbar, autostart), `TerminalPad`
(AddInTree shim, same shape as Debugger's `ConsolePad`), `TerminalPaneModel` (`ToolPaneModel`: toolbar +
groups of panes), `TerminalSession` (Porta.Pty + libghostty-vt: snapshots, key encoder), `TerminalView`
(drawing, key input), `TerminalKeys` (WPF `Key` → libghostty key), `TerminalSessionComboBox` and
`TerminalNewShellButton` (toolbar `Custom` items), `TerminalShells` (detection, default shell in
`SD.PropertyService` key `Terminal.DefaultShell`), `TerminalSettings` (font size, scrollback lines) and
`TerminalOptions` (Tools > Options > Terminal page, also the toolbar's settings button), `TerminalCommands`, `TerminalDevFlowActions`. Icons `Console`, `Add`, `Delete`,
`SplitWindowHorizontal` were copied into `ICSharpCode.Core.Presentation/Resources/VS2026/`.

Model: a *pane* is one shell + one `TerminalView`; a *group* is the panes shown side by side. New
terminal = new group of one; Split = another pane in the active group (same shell); Kill removes the
active pane. The session combo lists every pane, `(split)` marks panes that share a group.

Toolbar today: session combo, New, shell drop-down (installed shells; "Default Shell" submenu), Split,
Kill, Clear, Copy, Paste, Settings (opens the Options dialog; the Terminal page is under the
IDE options). View menu: Terminal (Ctrl+`), New Terminal (Ctrl+Shift+`), Next/Previous Terminal and Rename Terminal....
Project Browser "Open in Terminal" opens the integrated terminal in that folder through
`ITerminalHost` (Base interface, registered by the add-in; external terminal app if the add-in is not
loaded). DevFlow: `od.terminal.new|split|
activate|send|key|mouse|wheel|click|scroll|select|copy|paste|shells|settings|text|list|kill`; host side
`od.project-browser.open-terminal`. Integration test: `TerminalPadTests` (seven
journeys, pass; run with the xUnit v3 runner from `tests/OpenDevelop.IntegrationTests/TESTING.md`).

Verified live (the journey above, plus the UI tree):
- zsh/sh login shells start at the laid-out size; text, SGR colours, `$TERM`, `stty size`
  (`7 96` after the view resized the PTY), window title (OSC 0/2) and `ls` output are correct.
- Keys go through `TerminalView.HandleKey` and libghostty's encoder (what `OnKeyDown` calls): Up, F5,
  Shift+Tab, Alt+X (ESC prefix added by us, libghostty leaves Option composing on macOS), Up under
  DECCKM (`ESC O A`), Ctrl+C interrupting `sleep` (exit 130); plain text keys are left to text input.
- `vim -u NONE`: alternate screen shown and restored on `:q`.
- Wide characters: `AB你好C` stays `AB你好C` (the right half of a wide character is a spacer cell with no
  text; the view draws a wide character on its own so its two cells stay aligned).
- Scrollback: `seq 1 200` is kept (`scrollback.total > rows`), scrolling back moves the viewport, any key
  returns to the bottom. Selection (by cell range) returns the text without trailing blanks; Copy puts
  exactly the selection on the clipboard (nothing is copied without one).
- Paste: plain text is sent with CR newlines; after the program enables DECSET 2004 (tracked by watching
  the output, libghostty has no mode query) it is wrapped in `ESC[200~ ... ESC[201~`.
- Menu and shell list: View > New Terminal adds a terminal; `/etc/shells` and `$SHELL` are offered, the
  stored default decides what a plain New Terminal starts; Project Browser "Open in Terminal" on a project
  node opens the integrated terminal in the project folder (`echo $PWD` shows it).
- Mouse reporting: after a program enables it (DECSET 1000 + SGR 1006) press/release/wheel arrive as
  `ESC[<0;6;3M`, `...m`, `ESC[<64;6;3M` for the cell (1-based); before that the event goes nowhere. While a
  program tracks the mouse the view neither selects nor scrolls (hold Shift to select, as in xterm).
- Alternate screen: the wheel sends arrow keys (xterm "alternate scroll"; `less` moves 3 lines per
  notch). Double click selects a word, triple click the line.
- Copy: a long line the terminal wrapped is copied as one line (libghostty marks continuation rows), with
  no newline and no trimming at the wrap.
- Settings: a bigger font means fewer columns in the same pad and the shell is told (`stty size`);
  scrollback 0 keeps no history, 2000 keeps it. libghostty limits scrollback in *bytes* (rounded up to
  pages), so the line count is converted at about 16 bytes per cell and is approximate.
- Split: two panes in one group, each with its share of the width (376.5 DIP each), independent
  shells; the combo lists them and activating one routes input; killing one leaves its sibling.

Not verified / not done: actual pixels (no screenshot taken), WPF's own routing of real input: the mouse
handlers (`OnMouseLeftButtonDown/Move/Up`, wheel) and `OnKeyDown`/`OnTextInput` are only reached by real
input, the journeys drive the same methods through DevFlow (`HandleKey`, `HandleLeftDown/Up`,
`HandleWheel`, `ReportMouse`, `SelectRange`, `Paste`); the options page UI and the shell drop-down popup
(only their data is tested), the draggable splitter between split panes (a `GridSplitter`, never
dragged), the View menu shortcuts (commands are invoked through `od.menu.invoke`), vertical split, Windows (including its shell detection), Linux, win-arm64.

Pitfalls found (keep):
- Start the PTY near its final size. The view has no size before its first layout; the fit maths gave
  2x1 and a shell started that small was mangled by the later resize reflow. `TerminalView.IsLaidOut`
  guards it and a new pane starts from the active pane's size.
- "Open in Terminal" on a *project* node did nothing before: the node's path is the .csproj file, so
  `Directory.Exists` failed and the command returned silently (file and folder nodes were fine). It now
  uses the project's directory (`ProjectBrowserControllerBase.OpenTerminal`).
- libghostty's `MaxScrollback` is bytes, not lines. Passing a line count made every terminal keep the same
  few hundred lines whatever the setting was (found when a 40-line setting still kept 503).
- A big `dotnet run` of the integration project can look hung: the first test that opens a solution runs
  msbuild for a while, and a fixture start can stall once ("DevFlow agent did not respond within 2
  minutes"); one retry passed. Run it in the background and wait for the summary.
- libghostty's palette setter is not named in the managed binding (only the foreground/background options
  are): the C header's option 14 (`GHOSTTY_TERMINAL_OPT_COLOR_PALETTE`, 256 RGB triples) was found to work
  by setting entry 1 and reading back what SGR 31 resolved to. Options 11 and 12 take one RGB triple.
- Porta.Pty loads `libporta_pty` from the add-in DLL's directory only, but NuGet leaves it under
  `runtimes/<rid>/native/`; `TerminalSession` installs a `NativeLibrary.SetDllImportResolver`.
- Porta.Pty's build target derives `OpenConsole.exe` from the first `conpty.dll` it sees, which is
  LibGhostty.Net's (no OpenConsole beside it): `MSB3030` on any non-Windows build. The csproj sets
  `PortaPtyStageConPtyHost=false`; Windows ConPTY packaging is still to do (phase 4).
- LibGhostty.Net has no win-arm64 native assets.
- The host shell must be rebuilt (`ICSharpCode.Core.Presentation` embeds the icons) before new icons
  resolve; the add-in alone is not enough. Use one SDK (Homebrew) for both builds.
- xUnit's `Assert.DoesNotContain(string, string)` compares by culture: with ICU an ESC character is
  "empty", so every string contains it. Pass `StringComparison.Ordinal` when asserting on terminal text.
- A custom toolbar item (`type="Custom"`) is constructed while the toolbar is built, i.e. inside the
  model's constructor: the model must publish `Instance` first. LibreWPF does not walk `BaseType` for
  implicit styles, so a `ComboBox` subclass pins the themed style on `Loaded`.

## Behaviour as built

Everything below is reachable from the toolbar, the View menu or keys; the same commands back the
DevFlow actions. Icons are `Icons.16x16.<Icon>` from the VS 2026 image library
(`Resources/VS2026/<Icon>/<Icon>_16x.xaml`; the library has no terminal glyph, `Console` is the closest
and is also the pad icon).

| Toolbar item | Behaviour | Icon |
|---|---|---|
| Session dropdown | lists every terminal ("1: <OSC title or shell>", `(split)` for panes sharing a group); selecting one activates its pane | (combo) |
| New | new terminal with the default shell in the solution folder (else home) | `Add` |
| Shell arrow beside New | menu of installed shells (macOS/Linux: `$SHELL` and `/etc/shells`; Windows: pwsh, powershell, `ComSpec`, Git bash, wsl) that starts one, and a checkable "Default Shell" submenu | (text) |
| Split | another shell (same shell as the active one) beside the active pane, in the same group, with a draggable divider | `SplitWindowHorizontal` |
| Kill | kills the active pane's shell and removes the pane (a new terminal starts if none is left) | `Delete` |
| Clear | clears screen and scrollback and redraws the prompt | `ClearWindowContent` |
| Copy / Paste | selection to clipboard / paste (bracketed when the program asked for it) | `Copy`, `Paste` |
| Settings | opens the Options dialog (Terminal page: default shell, font size, scrollback lines) | `Settings` |

View menu: Terminal (Ctrl+`), New Terminal (Ctrl+Shift+`), Next Terminal and Previous Terminal (they wrap
around the session list) and Rename Terminal... (an input box; an empty name restores the shell's own
title). A pane whose shell ended shows "[Process exited with code N]" and any key other than a bare
modifier closes it. Cmd/Ctrl+Shift+C and V copy and paste in the terminal. Scrollback defaults to 10 000 lines (approximate, see pitfalls) and the PTY follows the
view's size.

Colours follow the IDE theme: the default background and foreground are the theme's
`ToolWindowBackground` and `Foreground` brushes, and the 16 ANSI colours are VS Code's dark or light set
(dark when the background's luminance is below one half); the rest of the 256-colour palette is the
standard xterm cube and grey ramp. A new terminal starts with the current colours, and a theme change is
noticed by each view within about a second (a timer compares the colours, the add-in cannot subscribe to
the host's theme event) and recolours open terminals, text already on screen included.

Design intent that is **not built yet**: find in terminal, clickable file:line links, vertical split,
shell-integration cwd tracking.

Shutdown needs no sweep: with `sleep 4322 &` running in a terminal, killing the pane, and also
terminating (SIGTERM) or killing (SIGKILL) the whole IDE, left no child behind on macOS, because the
kernel hangs up the PTY. A program that deliberately ignores SIGHUP (`nohup`) would survive; that is
not handled. Not checked on Windows.

## Integration points

- Add-in: `src/AddIns/Misc/Terminal/` with its own `.addin` registering the pad
  (`<Pad id="TerminalPad" defaultPosition="Bottom, Hidden" .../>`), the toolbar, the View menu items and
  the Options page. It stays out of `Base`; the shell reaches it through `ITerminalHost`.
- Preferences: `Terminal` options panel (default shell, font size, scrollback lines).
- Environment: the IDE's environment, plus `TERM=xterm-256color`, `COLORTERM=truecolor`,
  `TERM_PROGRAM=OpenDevelop`, and `LANG=en_US.UTF-8` if unset on non-Windows.

## Testing

Integration only (`tests/OpenDevelop.IntegrationTests/TerminalPadTests.cs`, seven journeys, DevFlow
actions instead of OS input): shells, size and alternate screen, keys, split; wide text, scrollback,
selection, wrapped copy, bracketed paste; View menu, Project Browser "Open in Terminal", shell list;
mouse reporting, alternate-screen wheel, word and line selection; settings; cycling, renaming and the
end of a shell; IDE theme colours. There are no unit tests of
the engine on its own.

## Phasing

1. Spike: PTY + VT round trip. Done.
2. Pad with toolbar, sessions, split, selection, mouse, settings, Project Browser switch-over, DevFlow
   actions and journeys. Done on macOS arm64.
3. Still open: the not-built list above, then Windows validation (ConPTY packaging, ARM64 fallback)
   and Linux.

## Open questions

- Windows ARM64: managed fallback vs. upstream native build.
- Frame rate of a full-screen redraw (`vim`, `top`): it has not been measured.
