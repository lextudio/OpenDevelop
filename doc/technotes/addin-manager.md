# AddIn Manager

UnoDevelop uses SharpDevelop's `ICSharpCode.Core` AddIn system — a modular plugin architecture
based on XML addin descriptors. UI elements, services, pads, menu items, toolbars, commands, and
option panels are all declared declaratively in `.addin` files and composed at runtime via the
`AddInTree`.

---

## How It Works

### AddIn XML Format

Each `.addin` file sits in `src/Main/SharpDevelop/AddIns/` and follows this structure:

```xml
<AddIn name="My AddIn" author="..." description="...">
  <Manifest>
    <Identity name="My.AddIn" version="1.0"/>
  </Manifest>

  <Runtime>
    <Import assembly=":AssemblyName">
      <ConditionEvaluator name="MyCondition" class="Namespace.MyConditionEvaluator" />
      <Doozer name="MyDoozer" class="Namespace.MyDoozer" />
    </Import>
  </Runtime>

  <Path name="/Some/Extension/Point">
    <MenuItem id="MyItem" label="My Item" class="Namespace.MyCommand" />
  </Path>
</AddIn>
```

| Section | Purpose |
|---|---|
| `<Manifest>` | Identity and version. Used for dependency resolution at load time. |
| `<Runtime>` | Declares assemblies to load and registers custom `ConditionEvaluator`/`Doozer` types. |
| `<Path>` | Extension point. Items declared here are merged into the `AddInTree` under the given path. |

### Assembly References

The `assembly` attribute in `<Import>` follows these conventions:

| Syntax | Meaning |
|---|---|
| `:AssemblyName` | Load from any assembly named `AssemblyName` already in the app domain. Usually `:UnoDevelop` for all code in `SharpDevelop.csproj`. |
| `AssemblyName.dll` | Load from the file `AssemblyName.dll` in the addin directory. |

### The Path System

The `AddInTree` is a hierarchical tree of extension points identified by slash-delimited paths:

```
/SharpDevelop
  /Services
  /Workbench
    /Pads
    /MainMenu
    /ToolBar/Standard
    /DisplayBindings
    /ViewMenu
  /Dialogs
    /OptionsDialog
  /Pads
    /ProjectBrowser
      /ToolBar/Standard
      /ContextMenu/SolutionNode
      /ContextMenu/ProjectNode
      /...
    /TestsPad
      /Toolbar/Standard
```

Multiple `.addin` files can contribute to the same path — items are merged at load time with
topological ordering.

### Built-in Doozers

A **doozer** is a factory that converts an XML codon into a runtime object. Built-in doozers
registered in `ICSharpCode.Core`:

| Doozer Name | Codon XML | Produces |
|---|---|---|
| `MenuItem` | `<MenuItem id="..." label="..." class="..." />` | `MenuItemDescriptor` |
| `ToolbarItem` | `<ToolbarItem id="..." icon="..." tooltip="..." class="..." />` | `ToolbarItemDescriptor` |
| `Pad` | `<Pad id="..." class="..." title="..." ... />` | `PadDescriptor` |
| `Service` | `<Service id="InterfaceType" class="ImplType" />` | Service instance (lazy) |
| `Include` | `<Include path="..." />` or `<Include item="..." />` | Inlines items from another path |
| `Class` | `<Class id="..." class="..." />` | Class instance |
| `Static` | `<Static id="..." class="..." />` | Static method call |
| `String` | `<String id="..." text="..." />` | String value |
| `Icon` | `<Icon id="..." resource="..." />` | Icon |
| `FileFilter` | `<FileFilter id="..." ... />` | File filter |
| `OptionPanel` | `<OptionPanel id="..." label="..." class="..." />` | `IOptionPanelDescriptor` |

UnoDevelop also registers several custom doozers in `ServiceBootstrapper.Initialize()`:

| Doozer Name | Class | Purpose |
|---|---|---|
| `Pad` | `ICSharpCode.SharpDevelop.PadDoozer` | Wraps pad instances in `UnoPadContent` |
| `PadMenu` | `ICSharpCode.SharpDevelop.PadMenuDoozer` | Creates `PadMenuDescriptor` for the View menu |
| `OptionPanel` | `UnoDevelop.OptionPanels.OptionPanelDoozer` | Creates `DefaultOptionPanelDescriptor` for the Options dialog |

### Condition Evaluators

Conditions gate whether items are shown, disabled, or excluded at runtime.

```xml
<Condition name="SolutionOpen" action="Disable">
  <MenuItem id="..." class="..." />
</Condition>
```

| Attribute | Meaning |
|---|---|
| `name` | Matches a registered `ConditionEvaluator` name. |
| `action` | `Disable` (grey out), `Exclude` (remove), or `Nothing` (default). |
| Additional attrs | Arbitrary key-value pairs passed to the evaluator. |

Built-in evaluators from `ICSharpCode.Core`:

| Name | Evaluator |
|---|---|
| `Compare` | `CompareConditionEvaluator` |
| `Ownerstate` | `OwnerStateConditionEvaluator` |

UnoDevelop-specific evaluators (declared in `.addin` `<Runtime>` blocks):

| Name | Class | Checks |
|---|---|---|
| `SolutionOpen` | `UnoDevelop.Conditions.SolutionOpenConditionEvaluator` | A solution is loaded |
| `ExecutionActive` | `UnoDevelop.Services.ExecutionActiveConditionEvaluator` | A process is running |
| `Debugging` | `UnoDevelop.Services.DebuggingConditionEvaluator` | Debugger is attached |
| `Paused` | `UnoDevelop.Services.PausedConditionEvaluator` | Debugger is in break mode |
| `TestsRunning` | `UnoDevelop.Services.TestsRunningConditionEvaluator` | Test runner is active |

SharpDevelop evaluators (from `ICSharpCode.SharpDevelop.addin`):

| Name | Checks |
|---|---|
| `ActiveContentExtension` | Editor file extension matches |
| `ActiveViewContentUntitled` | Active tab is untitled |
| `ActiveWindowState` | Window is active/inactive |
| `DebuggerSupports` | Debugger supports a feature |
| `IsProcessRunning` | `isdebugging="True"` / `"False"` |
| `OpenWindowState` | Window is open/closed |
| `WindowActive` | Specific window is active |
| `ProjectActive` | Specific project language is active |
| `IsTextSelected` | Text is selected in editor |

### `type` Attribute on MenuItem / ToolbarItem

Controls the visual appearance:

| `type` | MenuItem | ToolbarItem |
|---|---|---|
| (default) / `Command` / `Item` | Clickable `MenuFlyoutItem` | Clickable `Button` |
| `Separator` | `MenuFlyoutSeparator` | Separator bar |
| `Menu` | Submenu (`MenuFlyoutSubItem`) | — |
| `CheckBox` | — | Toggle button |
| `ComboBox` | — | Combo box |
| `DropDownButton` | — | Drop-down button |
| `Builder` | Handled by `IUnoAddInMenuBarBuilder` | Handled by `IUnoAddInToolbarBuilder` |

---

## Registration & Loading Flow

### Order of Initialization

```
App..ctor()
  └─ ServiceBootstrapper.Initialize()
       ├─ Create SharpDevelopServiceContainer
       ├─ Register core services (IPropertyService, ILoggingService, etc.)
       ├─ Register Uno services (IProjectService, IStatusBarService, etc.)
       ├─ Add custom doozers (Pad, PadMenu, OptionPanel)
       ├─ Register IAddInTree service
       ├─ Set ServiceSingleton.ServiceProvider = container
       ├─ Register remaining services (IDisplayBindingService, IEditorControlService, etc.)
       └─ LoadBuiltInAddIns()
            ├─ Collect .addin files from output/ and source/ AddIns directories
            ├─ addInTree.Load(files) — parse XML, register paths
            └─ addInTree.BuildItems("/SharpDevelop/Services") — activate ServiceDoozer
```

### How Path Items Are Built

```csharp
// Static API facade
AddInTree.BuildItems<MenuItemDescriptor>("/UnoDevelop/MainMenu/File", owner);

// Internal flow:
addInTree.GetTreeNode("/UnoDevelop/MainMenu/File")
  → walks rootNode.ChildNodes["UnoDevelop"]→["MainMenu"]→["File"]
  → returns AddInTreeNode

node.BuildChildItems<MenuItemDescriptor>(parameter)
  → for each codon in node, finds registered doozer by name
  → calls doozer.BuildItem(args)
  → doozer returns MenuItemDescriptor (or subclass)
```

### Service Registration via AddIns

`/SharpDevelop/Services` is a special path. After loading all addins, the bootstrapper calls
`BuildItems<object>("/SharpDevelop/Services", container)` which triggers `ServiceDoozer`. Each
`<Service>` codon registers a lazy factory in the `IServiceContainer`.

```xml
<Path name="/SharpDevelop/Services">
  <Service id="UnoDevelop.Services.IUnoSolutionExplorerController"
           class="UnoDevelop.Services.UnoSolutionExplorerController" />
</Path>
```

This allows external addins to contribute services without modifying the bootstrapper.

---

## Adding New Items

### Adding a Menu Item

1. Open the relevant `.addin` file (e.g. `UnoDevelop.Shell.addin` for main menu items).
2. Add a `<MenuItem>` to the appropriate path:

   ```xml
   <Path name="/UnoDevelop/MainMenu/File">
     <MenuItem id="MyCommand" label="My Command"
               class="UnoDevelop.Commands.MyCommandShellCommand" />
   </Path>
   ```

3. Implement the command class in the `UnoDevelop.Commands` namespace implementing `ICommand`.

### Adding a Toolbar Button

```xml
<Path name="/UnoDevelop/Workbench/ToolBar/Standard">
  <ToolbarItem id="MyButton" tooltip="My Tooltip"
               icon="MyIcon_16x"
               class="UnoDevelop.Commands.MyCommandShellCommand" />
</Path>
```

Icons are resolved from `ms-appx:///Icons/{name}.svg`.

### Adding a Pad

```xml
<Path name="/SharpDevelop/Workbench/Pads">
  <Pad id="MyPad"
       class="UnoDevelop.Workbench.MyPad"
       title="My Pad"
       category="Main"
       defaultPosition="Left" />
</Path>
```

The pad class must be a `Microsoft.UI.Xaml.FrameworkElement`. It's wrapped in `UnoPadContent`
by `PadDoozer`.

### Adding an Option Panel

```xml
<Path name="/SharpDevelop/Dialogs/OptionsDialog">
  <OptionPanel id="MyCategory" label="My Category">
    <OptionPanel id="MyPanel" label="My Panel"
                 class="UnoDevelop.OptionPanels.MyPanelOptions" />
  </OptionPanel>
</Path>
```

The panel class inherits from `OptionPanel` (which inherits `UserControl`) and implements
`IOptionPanel`.

### Adding a New Service

**Via code** (preferred for core services):

```csharp
// In ServiceBootstrapper.Initialize():
container.AddService(typeof(IMyService), new MyService());
```

**Via addin** (for addin-provided services):

```xml
<Path name="/SharpDevelop/Services">
  <Service id="Namespace.IMyService"
           class="Namespace.MyService" />
</Path>
```

### Adding Condition Evaluators

Register in the addin's `<Runtime>` block:

```xml
<Runtime>
  <Import assembly=":UnoDevelop">
    <ConditionEvaluator name="MyCondition"
                        class="UnoDevelop.Services.MyConditionEvaluator" />
  </Import>
</Runtime>
```

Implement `IConditionEvaluator`:

```csharp
internal sealed class MyConditionEvaluator : IConditionEvaluator
{
    public bool IsValid(object parameter, Condition condition)
    {
        // condition.Properties contains extra XML attributes
        return SomeRuntimeState;
    }
}
```

Then use it in any `.addin` path:

```xml
<Condition name="MyCondition" action="Disable">
  <MenuItem id="..." class="..." />
</Condition>
```

---

## Built-in AddIn Files

| File | Registers |
|---|---|
| `UnoDevelop.Shell.addin` | Main menu items (File, Edit, Build, Debug, Analysis, Tools, Window, Help), main toolbar, Options dialog paths |
| `UnoDevelop.Pads.addin` | Pads (Solution Explorer, Properties, Locals, Call Stack, Watch, Immediate, Threads, Modules, Tests, Errors, Output), tests pad toolbar, View menu |
| `UnoDevelop.Explorer.addin` | Solution Explorer controller service, display bindings, context menus (Solution/Project/Folder/File/Reference nodes), project browser toolbar, Explorer submenu, `/SharpDevelop/Workbench/MainMenu` |
| `ICSharpCode.SharpDevelop.addin` (external) | ~30 services, ~30 condition evaluators, ~15 doozers, main menu structure, toolbars, property panels |

---

## Key Paths Reference

### Menu Paths

| Path | Consumed By |
|---|---|
| `/UnoDevelop/MainMenu/File` | `MainPage.xaml.cs:PopulateAddInMenu(FileMenu, ...)` |
| `/UnoDevelop/MainMenu/Edit` | `PopulateAddInMenu(EditMenu, ...)` |
| `/UnoDevelop/MainMenu/Build` | `PopulateAddInMenu(BuildMenu, ...)` |
| `/UnoDevelop/MainMenu/Debug` | `PopulateAddInMenu(DebugMenu, ...)` |
| `/UnoDevelop/MainMenu/Search` | `PopulateAddInMenu(SearchMenu, ...)` |
| `/UnoDevelop/MainMenu/Analysis` | `PopulateAddInMenu(AnalysisMenu, ...)` |
| `/UnoDevelop/MainMenu/Tools` | `PopulateAddInMenu(ToolsMenu, ...)` |
| `/UnoDevelop/MainMenu/Window` | `PopulateAddInMenu(WindowMenu, ...)` |
| `/UnoDevelop/MainMenu/Help` | `PopulateAddInMenu(HelpMenu, ...)` |
| `/SharpDevelop/Workbench/MainMenu` | `UnoAddInMenuBarBuilder.PopulateMenuBar()` (alternative full menu) |
| `/SharpDevelop/Workbench/ViewMenu` | View menu builder (pad visibility toggles) |

### Toolbar Paths

| Path | Consumed By |
|---|---|
| `/UnoDevelop/Workbench/ToolBar/Standard` | `UnoAddInToolbarBuilder.PopulateToolbar(MainToolbar, ...)` |
| `/SharpDevelop/Pads/ProjectBrowser/ToolBar/Standard` | Solution explorer toolbar |
| `/SharpDevelop/Pads/TestsPad/Toolbar/Standard` | Tests pad toolbar |

### Context Menu Paths

| Path | Consumed By |
|---|---|
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/SolutionNode` | Solution root context menu |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/ProjectNode` | Project context menu |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/FolderNode` | Folder context menu |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/FileNode` | File context menu |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/ReferenceNode` | Reference context menu |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/PackageReferenceNode` | Package reference context menu |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/Common/Add` | Shared "Add" submenu items |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/Common/Open` | Shared "Open" submenu items |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/Common/Edit` | Shared "Edit" submenu items |
| `/SharpDevelop/Pads/ProjectBrowser/ContextMenu/Common/Membership` | Shared project membership items |

### Other Paths

| Path | Consumed By |
|---|---|
| `/SharpDevelop/Services` | `ServiceDozer` for lazy service registration |
| `/SharpDevelop/Dialogs/OptionsDialog` | `OptionsDialog` flat panel tree |
| `/SharpDevelop/Workbench/Pads` | Pad loading in `MainPage.xaml.cs` |
| `/SharpDevelop/Workbench/DisplayBindings` | `DisplayBindingService` |

---

## AddIn Tree Architecture

```
AddInTreeImpl
  ├─ addIns: List<AddIn>              (parsed .addin files)
  ├─ rootNode: AddInTreeNode          (tree of registered extensions)
  ├─ doozers: ConcurrentDictionary    (name → IDoozer)
  └─ conditionEvaluators: ConcurrentDictionary  (name → IConditionEvaluator)

AddInTreeNode
  ├─ ChildNodes: Dictionary<string, AddInTreeNode>
  └─ Codons: List<Codon>             (items registered at this path level)
```

When `BuildItems<T>(path, parameter)` is called:
1. `GetTreeNode(path)` walks `ChildNodes` to find the node
2. `node.BuildChildItems<T>(parameter)` iterates `Codons`, resolves the doozer, filters by
   conditions, and builds each item
3. Results are returned as `List<T>`

## Internal Architecture

The addin system is layered:

- **`AddInTree`** (static class in `ICSharpCode.Core`) — convenience facade that delegates to `IAddInTree`
- **`AddInTreeImpl`** — concrete implementation registered as the `IAddInTree` service
- **`AddInTreeNode`** — tree node holding child nodes and codon lists
- **`Codon`** — parsed representation of a single XML element within a `<Path>`
- **`AddIn`** — parsed representation of an entire `.addin` file
- **`IDoozer`** / **`IConditionEvaluator`** — extension points for custom factories and conditions
- **`Condition`** — parsed `<Condition>` element with name, action, and properties
- **`MenuItemDescriptor`** / **`ToolbarItemDescriptor`** — data objects produced by doozers, consumed by Uno bar builders
- **`PadDescriptor`** — data object produced by `PadDoozer`, consumed by the workbench
- **`DefaultOptionPanelDescriptor`** — produced by `OptionPanelDoozer`, consumed by `OptionsDialog`
