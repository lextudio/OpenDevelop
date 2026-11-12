# Keyboard shortcut overrides (design)

Status (2026-11-12): **design; phase 1 is implemented**, phases 2 to 4 are not. Implemented: the command key (`CommandKey`), `KeyBindings.xml` (`KeyBindingsFile`), the resolution, `KeyBindingService`,
the four installers moved onto it, and the DevFlow actions `od.keybindings.bindings|invoke|overrides|set|reset`. **Not yet:** the `CommandCatalog`, the command-wide default for an item that has no `shortcut`
of its own (so for now an override applies to every site of a command, but a default is still taken from the item's own attribute), and the Settings page. The sections below say what each part does or will do.
Facts below were read from the code and from the `.addin` manifests on that date.
The counts come from a script over the **57 product `.addin` files**: the 79 tracked ones minus the 15 under `samples/` and the 7 test fixtures
(`*.Tests/TestResources`, `tests/`). The samples are left out on purpose: they carry their own menus (for example `Control|Z` on `Undo` in `SdaBase.addin`)
that the product does not. Anything marked *proposal* or *open* is not verified. Tracks
[issue #26](https://github.com/lextudio/OpenDevelop/issues/26) "User be able to define shortcuts for tasks" (build, rebuild, clean, debug, ...).

Related notes: [`main-menu.md`](main-menu.md) (how menus are built, lazily), [`debugging.md`](debugging.md) (the commands the issue names).

## Goal

A user can, from the Settings dialog, give a command a keyboard shortcut, change the one it has, remove it, and restore the default; and the
result applies at once and is still there after a restart. The issue's own example, "Clean", has **no** default shortcut today, so assigning one
to a command that has none is the first-class case, not an afterthought.

Not goals of the first version: two-step chords (`Ctrl+K, Ctrl+C`), several gestures for one command, keymap presets (VS, Eclipse, ...),
per-project or per-solution shortcuts, shortcuts for the editor's built-in editing actions (see "Phases").

## What exists today (verified)

- A shortcut is the `shortcut` attribute of an `.addin` item, for example `shortcut = "Control|F7"`. It is parsed by
  `MenuService.ParseShortcut` (`ICSharpCode.Core.Presentation/Menu/MenuService.cs:284`; `|` becomes `+`, then WPF's `KeyGestureConverter`).
- It is installed in exactly **four** places, each of which reads the attribute itself and adds an `InputBinding` to a UIElement it was given:
  `MenuCommand.cs:41`, `MenuCheckBox.cs:49`, `ToolBarButton.cs:54`, and the pad builder `Commands/MenuItemBuilders.cs:466` (a pad's `Shortcut`,
  bound on `SD.Workbench.MainWindow`). Two items with the same gesture on one owner are merged by an `AmbiguousCommandWrapper`; nothing tells the user.
- The owner is the main window for the main menu (`WpfWorkbench.cs:196`, built with `immediatelyExpandMenuBuildersForShortcuts: true`, because sub-menus are
  otherwise expanded lazily when opened and their shortcuts would not exist yet), or the control that opens a context menu.
- There is **no** user override and **no** keyboard settings page. Shortcuts for the defaults that exist work (verified 2026-11-12 with a real key press: F8 builds).
- The Settings dialog is `Tools > Options` (`OptionsCommand`, `Gui/Dialogs/TreeViewOptionsDialog.xaml`): a tree on the left (200 px), the selected
  `OptionPanel` on the right, **OK and Cancel only (no Apply)**, default size 552 x 414, minimum 480 x 260. A panel is registered in
  `ICSharpCode.SharpDevelop.addin` as `<OptionPanel id label class>` and derives from `OptionPanel` (`LoadOptions`/`LoadOptionsAsync`/`SaveOptions`),
  for example `IdeThemeOptions` under the `UIOptions` node.
- SharpDevelop had no configuration UI for this either: its Options tree has no keyboard page and its history has no such feature (checked in this repository and in the project's wiki), so the shortcuts were changed by editing `.addin` files.

### The data that decides the design

A *commandable item* is a `MenuItem` or `ToolbarItem` element that has a `class` or a `command` attribute and whose `type` is not `Menu`, `Separator` or `Builder` (a Builder produces a
dynamic list and has no identity; counting the builders too gives 570 items, 403 distinct commands and 90 shared ones). A *shortcut item* is any `MenuItem`, `ToolbarItem` or `Pad` with a `shortcut`. Everything is counted on the **parsed XML**, so an element inside an XML comment does not count: `WpfDesign.addin` has an
`OutlineViewPad` with `Control|Alt|I` in a commented-out block, which a text search finds and the product does not have. The one pad with a shortcut is `PropertyPad` (`F4`, `ICSharpCode.SharpDevelop.addin`).

| | count |
|---|---|
| menu / toolbar / context-menu items that run a command (a `class` or a `command`) | 551 |
| distinct commands among them | **385** |
| commands that appear in more than one item (main menu, toolbar button, context menu, ...) | 89 |
| distinct commands that have a default shortcut | 63 |
| commands whose items disagree about the default shortcut | **0** |
| commands with a shortcut on some of their items but not on others (a toolbar button without it) | 22 |
| items with a `shortcut` | 76 (75 menu items, 1 pad) |
| commandable items by kind and site: context-menu 256, main-menu 167, toolbar 100, other menu paths 28 (the Tools list, pad menus) | |

So the user thinks in **commands** ("Save", "Rename"), and the manifests already agree with that: one command, one gesture. `Rename` is a
context-menu item in ten project-browser context-menu paths, eight of which carry the same `F2`; `Save` is in the main menu, the editor's context menu and a
toolbar. Overriding per menu *item* would make the user repeat the change in each of the ten items (the eight that carry `F2`, and still the other two) and leave the toolbar button out of date.

## Decisions (proposal)

### 1. A shortcut belongs to a command, identified by a command key

| command is... | key | examples |
|---|---|---|
| a command class | `class:<full class name>` | `class:ICSharpCode.SharpDevelop.Project.Commands.Build` |
| a WPF routed command named in `command=` | `routed:<name>` | `routed:Undo` |
| a pad | `pad:<pad id>` | the `shortcut` of a `<Pad id=...>` codon under `/SharpDevelop/Workbench/Pads` (one in the product today) |
| an item with neither (a `link=` item that opens a URL) | `item:<AddIn identity>/<id>` | Help > Topics, `Alt+F1`, the only such item with a shortcut. The tree path of an item is not known where its shortcut is installed, so the AddIn and the id stand in for it |

The class name is stable, human-readable, and the same for every item that runs the command, which is what the data above requires. An item
that is a `Builder` (a dynamic list) has no identity and cannot have a shortcut, as today. A key that no longer exists after an upgrade
(a renamed class) is kept in the file and shown in the page as "unknown command" with a Remove button, never silently dropped.

**The effective gesture applies to every site of the command**: the override if there is one (an explicit "none" removes the default),
otherwise the default from the manifest. Where a command's items differ today (22 commands have the attribute on only some items), the
service uses the one gesture it finds, so the toolbar button's tooltip and the binding agree. This changes nothing for the defaults. *(Phase 1 implements the override part: an override reaches every site of the command. Taking the default of a command from its other items needs the `CommandCatalog`.)*

Routed commands are different in kind. In the product they are `Undo`, `Redo`, `Cut`, `Copy`, `Paste`, `SelectAll` (main-menu and toolbar items with `command=` and **no**
`shortcut` attribute: their keys are WPF's built-in `InputGestures` on the command, `Ctrl+Z` in any text box) and `Print` and `Help` (which do carry a `shortcut`). An `InputBinding`
on the window can add a gesture to such a command but cannot remove the built-in one. **First version: shown with the gesture they have, read-only, with the reason in the row**,
because an override that leaves the old key working would be a lie.

### 2. Where overrides are stored

A dedicated file in the configuration directory, `KeyBindings.xml`, not a bag of properties, so it can be read, hand-edited, exported, and
carried to another machine as one file. Only the **differences** from the manifests are stored, so a new default in a later version reaches
everyone who did not change that command.

```xml
<KeyBindings version="1">
  <Command key="class:ICSharpCode.SharpDevelop.Project.Commands.Clean" gesture="Ctrl+Shift+F8" />
  <Command key="class:ICSharpCode.SharpDevelop.Project.Commands.Build" gesture="Ctrl+B" />
  <Command key="class:ICSharpCode.SharpDevelop.Commands.SaveFile" gesture="" />   <!-- removed: no shortcut -->
</KeyBindings>
```

An empty `gesture` is the explicit "no shortcut". `KeyGestureConverter` cannot parse an empty string, so the loader tests for it first and never hands it to the converter; a
`gesture` that does not parse is reported and the entry ignored, not fatal. The file lives in the user configuration directory (`PropertyService.ConfigDirectory`, which is what
`-configdir:` sets; on macOS `~/Library/Application Support/ICSharpCode/SharpDevelop5`).

Gestures are stored in WPF's invariant `KeyGesture` text (`Ctrl+Shift+F8`), which is culture-independent; the page shows the culture's display string
(`MenuService.GetDisplayStringForShortcut`). `version` lets a later format be read.

### 3. How an override reaches the running application

The four installers above each read the attribute and add an `InputBinding`. That cannot be changed live, because nothing remembers what it added.
*Proposal*: one **`KeyBindingService`** (next to `MenuService` in `ICSharpCode.Core.Presentation`):

- `Register(owner, commandKey, command, defaultGesture, displayTargets)`: the four installers call this instead of `InputBindings.Add`. The service
  adds the binding, remembers `(owner, binding, the menu item or toolbar button that shows the gesture)` with weak references, and returns the
  effective gesture text for the item.
- `Changed`: raised when overrides change. The service removes the bindings it added, adds the new ones, and updates `InputGestureText` and tooltips of the
  items it still holds. No restart; the Settings dialog's OK calls it.
- A gesture used twice on one owner is **refused by the page** (below) instead of being merged by `AmbiguousCommandWrapper`; the wrapper stays for
  manifests that already collide, as today.
- Nothing else about building menus changes, including the lazy sub-menu expansion: sites are registered when the item is built, and the main menu
  is already built eagerly for shortcuts.

The page cannot depend on what has been built (sub-menus and context menus are created when opened), so it lists commands from a **`CommandCatalog`**
that walks the AddIn tree's *codons* (path, id, label, `class`/`command`/`link`, `shortcut`, `type`, and the `<Pad>` codons under `/SharpDevelop/Workbench/Pads`) without instantiating anything. The breadcrumb
("File > New > File...") is the chain of labels down the path. Labels are `${res:...}` strings, run through the `StringParser`, mnemonic underscore removed.

### 4. Scope and conflicts

A binding lives on an owner, and WPF gives the focused control's bindings first. So "the same gesture twice" is only a conflict inside one **scope**:

| scope | where | examples |
|---|---|---|
| Workbench | main menu, toolbars, pad shortcuts (main window) | Build `F8`, Start `F5` |
| a control scope | a context menu's owner | Text editor, Project browser, XML tree, Resource editor |

Rules for the page:

1. A gesture already used by **another command in the same scope** is a conflict. A gesture used by a Workbench command and by a control-scope command
   is shown as *shadowing* (the control wins while it has focus), allowed with a visible note. The same command in several scopes is not a conflict with itself
   (`F2` Rename in eight context-menu paths).
2. A conflict is never saved silently. The assign action shows who owns the gesture and offers **Reassign** (removes it from the other command,
   recorded as an explicit "none" override if that one had a default) or **Cancel**.
3. Refuse gestures that cannot work: a bare letter, digit or `Shift`+letter (they type text) outside a text-free scope, a lone modifier, and the platform's reserved keys
   (macOS `Cmd+Q`, `Cmd+H`, `Cmd+Tab`, Windows `Alt+F4`, ...). The list is data, not code.

*Open*: on macOS the manifests say `Control`. Whether `Ctrl` in a manifest should mean `Cmd` there, and how `Ctrl` and `Cmd` are told apart in the capture box,
needs a look at how LibreWPF reports `ModifierKeys.Windows` before the page is built.

## The Settings page (proposal)

**Where**: a new page `Keyboard` under the first node of the tree (`UIOptions`, next to *IDE Theme*, *Load/Save*, *Updates*), registered in
`ICSharpCode.SharpDevelop.addin` as `<OptionPanel id="Keyboard" label="Keyboard" class="ICSharpCode.SharpDevelop.OptionPanels.KeyboardShortcutsOptions"/>`,
implemented like `IdeThemeOptions` (a XAML `OptionPanel`). It is where people expect it: Visual Studio has Options > Environment > Keyboard, Rider has Settings > Keymap, and VS Code has its Keyboard Shortcuts editor.

**Size**: the right-hand area at the default dialog size is only about 350 px wide, too narrow for a command list and an editor. Proposal: raise the
default to about 760 x 540 (minimum unchanged), remembered between sessions; the page must still be usable at the 480 px minimum
(the list drops the "Default" column and the details wrap).

```
+- Settings -----------------------------------------------------------------------------+
| SharpDevelop Options             | Keyboard                                            |
|  > IDE                           | +--------------------------------------------------+|
|     Culture                      | | [ Search commands or press a shortcut ] [Show v] ||
|     Style                        | | All | Customized | Unassigned | Conflicts        ||
|     IDE Theme                    | +--------------------------------------------------+|
|  * Keyboard   <-----             | | Command                 Shortcut        Scope    ||
|     Load/Save                    | | v Build                                          ||
|     Updates                      | |     Build Solution     F8              Workbench ||
|     ...                          | |     Rebuild Solution   Alt+F8          Workbench ||
|                                  | |  *  Clean Solution     Ctrl+Shift+F8   Workbench ||
|                                  | |     Clean Project      (none)          Workbench ||
|                                  | | v Debug                                          ||
|                                  | |     Start Debugging    F5              Workbench ||
|                                  | +--------------------------------------------------+|
|                                  | Shortcut for "Clean Solution"   Build > Clean ...   |
|                                  | [ Ctrl+Shift+F8      ]  [Assign] [Remove] [Reset]   |
|                                  | ! Ctrl+Shift+F8 is also "Foo" (Text editor).        |
|                                  |   [Reassign]  [Cancel]                              |
|                                  | [ Reset all... ] [ Import... ] [ Export... ]        |
|                                  |                                 [  OK  ] [Cancel]   |
+----------------------------------------------------------------------------------------+
```

**The list** (a grid, one row per command from the `CommandCatalog`, grouped by the top-level menu, sorted in menu order):
- *Command*: the display name, with the breadcrumb as a second, dimmer line. A command that is in several menus appears **once**, under its first main-menu location;
  its other locations are listed in the details.
- *Shortcut*: the effective gesture in the culture's display form, empty text "(none)". A dot marks a customized row (differs from the default); a warning mark
  marks a conflict. *Default* (dim) is shown when there is room. *Scope* tells Workbench from a control scope.
- A routed command is shown with a lock and the reason in its tooltip.

**Finding**: the search box filters on command name, breadcrumb and gesture text. A toggle makes it a **gesture search**: the next key chord typed into the box filters to the commands
that use it ("who owns Ctrl+B?"), which is also how a conflict is found before assigning. The filter chips cover the other questions: *Customized*, *Unassigned* (the
issue's "Clean" starts here), *Conflicts*.

**Assigning**: select a row; the details area shows the command, where it appears, and a capture box. A click (or double-click on the row) puts focus in the capture box, which shows
"Press a shortcut..." and records the next chord, including `Esc` and the function keys, because capturing is a mode, not a typed string; leaving the box ends it. **Assign** is enabled when
there is a valid, changed gesture; **Remove** sets "none" (explicit, so the default does not return); **Reset** removes the override and restores the default; **Reset all...** asks
first. Invalid gestures say why in the same place the conflict message appears; the conflict message names the other command with its breadcrumb and scope.

**Applying**: the dialog has no Apply, so the page keeps a **working copy** and nothing changes until OK; Cancel discards it. OK writes `KeyBindings.xml` and calls
`KeyBindingService`, so the menus and the toolbars show the new keys immediately.

**Import / Export** read and write `KeyBindings.xml`; an import shows what it would change and the conflicts it would create before applying.

**Accessibility and text**: every control has an automation name; the whole page works from the keyboard (the capture box is the one place that swallows keys, and its state is announced);
all strings go through `${res:...}` with English, Simplified and Traditional Chinese like the rest of the UI.

## Phases

1. **Core** (implemented, except the `CommandCatalog`): the command key, `KeyBindings.xml` (read, write, round-trip, a newer `version` and unknown content kept, an unreadable file kept as `.corrupt`), the resolution
   `override ?? default`, `KeyBindingService`, and the four installers moved onto it. No UI. Observable through DevFlow and a hand-edited file. How the service works: for every owner it keeps the sites registered on it and
   DERIVES the bindings it adds from them, so a change removes what it added and adds the new ones; a site is replaced when the same item (its codon) registers again, which is what a context menu does every time it
   opens, so nothing piles up (the old code added a binding, or wrapped one, on every open); two sites on one gesture are combined as before (the first that can execute runs); bindings other code added are not touched; an
   override that does not parse is reported and ignored, so the default applies; an item without a shortcut registers too, so an override can give it one (the issue's "Clean"). Still to do in this phase: the
   `CommandCatalog` and the command-wide default.
2. **The page**: list, search, details and capture, conflict handling, OK/Cancel semantics, the dialog's default size.
3. **Import / export, reset all, the unknown-command row**, and the gesture search.
4. **More scopes**: the editor's own commands (AvalonEdit's built-in editing actions and the add-in's `CustomCommands`), the terminal, designers. They bind inside
   controls, not through `.addin` items, so they need their own registration (a small `IKeyBindable` description) before they can appear in the same list.

## Tests

- **Unit** (`tests/OpenDevelop.Base.Tests`, no UI): command-key derivation for class, routed and pad items; `CommandCatalog` over an in-memory AddIn tree (the pattern of `AddInSuspensionTests`);
  resolution (`override`, explicit none, default, two sites of one command); `KeyBindings.xml` round trip including the unknown-key row and a future `version`; gesture parsing and display for
  the culture; conflict detection per scope, including `F2` Rename in eight context-menu paths being no conflict and Workbench vs control being shadowing; the rejected gestures.
- **Service**: `Register` / `Changed` against real `UIElement`s: the old gesture stops working, the new one runs the command, the `InputGestureText` changes, a collected owner does not leak.
- **Integration** (one method at a time): DevFlow actions `od.keybindings.set` / `reset` / `overrides` / `bindings` / `invoke <gesture>`, where `invoke` finds the `InputBinding` for a gesture on the owner and executes its command
  **by the path the key takes**, because a real key press needs the key window and a locked screen drops it; then the Settings page through the UI tree (select a row, set a gesture through the
  capture box's action, OK) and the effect on the menu text. A restart journey as for suspension: set, relaunch with the same configuration directory, the override is still applied.
- Not testable without a real key: the capture box's raw key handling; the manual check is one real chord on a machine with an unlocked screen.

## Risks and open questions

1. macOS: what `Control` in a manifest means, and the capture box's modifiers (above). Needs a look before Phase 2.
2. Routed commands stay read-only in the first version; making them overridable means removing the built-in gestures per control, which is a larger change.
3. A manifest that gives the same command different gestures on different items (none today) would be resolved by the service to one; the catalog should log it.
4. Commands built by a `Builder` (dynamic lists such as recent files or window lists) cannot be keyed; they stay as they are.
5. Several gestures per command and chords are the obvious next requests; the file format (a `<Command>` element) can take more attributes or child elements under a new `version`.
6. Whether the page should offer scheme presets is a product decision, not a technical one.
