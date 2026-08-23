# MewUI designer fixture

This fixture is the reference project layout for source-backed MewUI windows:

```text
MewUIFixture/
├── MewUIFixture.csproj
├── Program.cs
└── Windows/
    ├── MainWindow.cs
    ├── MainWindow.Designer.cs
    ├── SettingsWindow.cs
    └── SettingsWindow.Designer.cs
```

Each window is an independently constructible `partial class : Window`:

- `WindowName.cs` is user-owned. It contains the constructor and event handlers.
- `WindowName.Designer.cs` is designer-owned. It contains control fields and
  `InitializeComponent()`.
- Both files use the same namespace and partial class name.
- The constructor calls `InitializeComponent()` exactly once.
- Generated code uses standalone construction, property, event, relationship, and final
  `Content` assignments. It never uses `this.`, nested assignments, or nested object creation.
- Open `WindowName.cs` in UnoDevelop to activate the visual designer. Generated
  changes are saved only to its adjacent `WindowName.Designer.cs` file.

`MainWindow` exercises designer edits and event preservation. `SettingsWindow`
proves that another window in the same project is discovered and designed as a
separate unit.
