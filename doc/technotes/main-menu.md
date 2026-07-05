# Main Menu Investigation

## The Problem

The WPF main menu shows top-level items (File, Edit, View, etc.) but clicking
them never opens a submenu — either the submenu popup doesn't appear at all, or
it appears empty (the SubmenuOpened handler that replaces the dummy
`ItemsSource` with real sub-items never fires).

## Architecture

Menu items are built by `MenuService.CreateMenuItems()` in
`src/Main/ICSharpCode.Core.Presentation/Menu/MenuService.cs`.

Each top-level `type="Menu"` item is created as a `CoreMenuItem` (which extends
`MenuItem`) with `ItemsSource = new object[1]` (a single dummy placeholder). A
`SubmenuOpened` handler is wired up to lazily expand sub-items:

```csharp
var item = new CoreMenuItem(codon, descriptor.Parameter, descriptor.Conditions) {
    ItemsSource = new object[1],
    SetEnabled = true
};
var subItems = CreateUnexpandedMenuItems(context, descriptor.SubItems);
item.SubmenuOpened += (sender, args) => {
    item.ItemsSource = ExpandMenuBuilders(subItems, true);
    args.Handled = true;
};
```

This is the same lazy-expansion pattern used by `ContextMenu` creation
(`CreateContextMenu` at line 149) — but context menus work fine in the app.

## Menu Population Site

`WpfWorkbench.Initialize()` in
`src/Main/SharpDevelop/Workbench/WpfWorkbench.cs`:

```csharp
mainMenu.Items.Clear();
foreach (var item in MenuService.CreateMenuItems(this, this, mainMenuPath, ...)) {
    mainMenu.Items.Add(item);
}
```

## Startup Timing

From `WorkbenchStartup.cs` (`src/Main/SharpDevelop/Workbench/WorkbenchStartup.cs`):

```
line 55:  InitializeWorkbench(new WpfWorkbench(), new AvalonDockLayout());
              line 67:  workbench.Initialize();        // menu items added here
              line 68:  workbench.SetMemento(...);
              line 69:  workbench.WorkbenchLayout = ...;
line 156: app.Run(SD.Workbench.MainWindow);   // window shown here (Show() called)
```

Key fact: **`Initialize()` runs BEFORE the window is shown**. At that point the
`Menu` is a live XAML object but has no `PresentationSource` yet. The window
isn't connected to the visual tree until `app.Run()` calls `Show()`.

## Original Bug (ItemsSource)

The original code used:

```csharp
mainMenu.ItemsSource = MenuService.CreateMenuItems(...);
```

This left every top-level `CoreMenuItem` with `Parent = null` because WPF
doesn't parent items that serve as **both data items and their own containers**
(the `ItemContainerGenerator` does NOT wrap data items in a container if the
data item already IS a `MenuItem` — but it also doesn't set the data item's
`Parent`).

Without a valid `Parent`, `MenuItem.IsParentMenuOrMenuItem(item)` returns
`false`, so `CoerceIsSubmenuOpen` coerces `IsSubmenuOpen` back to `false`.

## Attempted Fix (Items.Add)

Changed to:

```csharp
foreach (var item in MenuService.CreateMenuItems(...)) {
    mainMenu.Items.Add(item);
}
```

With `Items.Add()`, `item.Parent = Menu` (correctly set by WPF when adding a
UIElement directly as a logical child). This fixes the `Parent` check in
`CoerceIsSubmenuOpen`.

## Still Broken: IsLoaded

Even with `Parent = Menu`, `IsSubmenuOpen = true` is still coerced to `false`.
The log shows:

```
mainMenu.IsLoaded=False   mainMenu.IsVisible=True   mainMenu.Items.Count=11
File: Header=_File  Parent=Menu  IsLoaded=False  HasItems=True  Items.Count=1
After IsSubmenuOpen=False
```

The `MenuItem.CoerceIsSubmenuOpen` in WPF source checks three conditions:

```csharp
private static object CoerceIsSubmenuOpen(DependencyObject d, object value) {
    var mi = (MenuItem)d;
    if ((bool)value) {
        if (!mi.IsLoaded || !mi.IsEnabled || !IsParentMenuOrMenuItem(mi))
            return false;
        ...
    }
    return value;
}
```

- `mi.IsEnabled` → true (confirmed)
- `IsParentMenuOrMenuItem(mi)` → true (Parent=Menu passed)
- `mi.IsLoaded` → **false** ← this is the blocker

## Why IsLoaded Is False

At `ApplicationIdle` priority — the lowest WPF dispatcher priority — the Menu
ITSELF reports `IsLoaded=False` even though `IsVisible=True` and the window is
on screen.

Possible explanations (not yet confirmed):

1. **Menu items added before window is shown**: Items were `Items.Add()`ed at
   `Initialize()` time (before `app.Run()`). WPF may not correctly connect
   them to the logical tree when the window is later shown if they were added
   "too early" — the `Menu` might finalize its visual tree generation during
   `OnApplyTemplate` (which triggers during first layout), and items that exist
   before that point need to be re-connected somehow.

2. **IsLoaded requires legacy logical tree connection**: In WPF 3.x/4.x,
   `IsLoaded` is set by `ContentOperations.SetParent` / `AddLogicalChild` when
   the element is connected to the `PresentationSource`. If the Menu's
   `ItemContainerGenerator` hasn't finished its work on the already-present
   items, the Menu itself may not fire `Loaded`.

3. **Something in the initialization sequence interferes**: The combination of
   `mainMenu.Items.Clear()` + 11 `Items.Add()` calls + subsequent
   `dockPanel.Children.Insert(...)` for toolbars may cause the layout engine
   to defer or skip the Menu load.

4. **Behaviour is specific to this WPF runtime version**: .NET 8 / modern
   `PresentationFramework` may have subtle differences around item generation.

Note: The `Initialize()` method also calls
`MenuService.UpdateStatus(mainMenu.ItemsSource)`, but since we now use
`Items.Add()`, `mainMenu.ItemsSource` is `null` — so `UpdateStatus` does
nothing. This is a secondary bug (the items never get `UpdateStatus`/`UpdateText`
called after the window is visible).

## SubmenuOpened Event Never Fires

Even if the user physically clicks on a top-level menu item, the SubmenuOpened
handler doesn't fire. This is because the event is a routed event that's
triggered by `IsSubmenuOpen` becoming `true`, and `CoerceIsSubmenuOpen`
prevents that from happening.

## Eager Populate Hypothesis (Untested)

An alternative approach: instead of the lazy `SubmenuOpened` pattern, eagerly
populate sub-items during `Initialize()`:

```csharp
var expandedSubItems = ExpandMenuBuilders(subItems, true);
item.ItemsSource = expandedSubItems;
// No SubmenuOpened handler needed
```

This would bypass the need for `IsSubmenuOpen` entirely. The menu would show
all sub-items immediately. However, the `SubmenuOpened` handler also serves to
allow menu builders (like the file MRU list) to populate dynamically, so this
would lose dynamic menu item support for those cases.

## Secondary Bug: UpdateStatus Not Called

`WpfWorkbench.UpdateMenu()` at line 289:

```csharp
void UpdateMenu() {
    MenuService.UpdateStatus(mainMenu.ItemsSource);
    ...
}
```

Since `mainMenu.ItemsSource` is now `null` (we use `Items.Add()`), `UpdateStatus`
is a no-op. Menu items never have their status (visibility, enabled state)
updated after creation. Fix: either iterate `mainMenu.Items` directly, or pass
the item collection.

## Items.Count = 1 vs ItemsSource

Each top-level CoreMenuItem has `Items.Count = 1` because of the dummy
`ItemsSource = new object[1]`. The actual sub-items are stored in the closure
variable `subItems` and will only be expanded when/if SubmenuOpened fires. The
dummy entry is never visually realized because setting `ItemsSource` does not
cause item generation until the item is loaded — but it satisfies the
`HasItems` property check.

## Confirmed Working

- All 79 menu item `class=` references in `.addin` files resolve to existing
  `.cs` source files.
- All condition evaluators referenced by the main menu (`SolutionOpen`,
  `WindowActive`, `ActiveWindowState`, `OpenWindowState`, etc.) are registered.
- `ExpandMenuBuilders` creates correct sub-item lists (File=17, Edit=15,
  View=14, etc.).
- `MenuService.CreateMenuItems()` correctly creates 11 top-level items.
- App builds with `dotnet build OpenDevelop.Mvp.slnx` (0 errors).

## Debugging Setup

Diagnostic logging writes to `/tmp/opencode_menu.log` (file-based to avoid
log4net capture issues). The app is started with:

```
dotnet run --project src/Main/SharpDevelop/SharpDevelop.csproj --no-build
```

DevFlow is used to observe the running app's UIA tree at `localhost:9223`.

The diagnostic code is in:
- `MenuService.cs` — `CreateMenuItems()`, `CreateMenuItemFromDescriptor()`,
  `ExpandMenuBuilders()` (adds file-log writes to trace item creation)
- `WpfWorkbench.cs` — `Initialize()` (adds file-log writes at Background and
  ApplicationIdle priorities to check menu state)
