# LeXtudio.MewUI.Xaml (.mxaml)

## Status

Approved direction (2026-08-23). Supersedes the "C# syntax tree is the authoritative
document" decision for the DESIGNER layer only — see the reversal note at the bottom of
[`mewui-designer.md`](mewui-designer.md). The Aprillz.MewUI runtime stays C#-first: it gains
no XAML reader, and generated applications never load XML at runtime.

## Why

The direct-C# designer (Roslyn transforms over a strict InitializeComponent grammar) works,
but every real usage exposed friction rooted in the same cause: **the document is program
text**.

- Containment lives in `parent.Children(child, ...)` calls; omitting ONE call silently
  drops a subtree (measured: a missing `generalGroup.Children(generalStack)` erased three
  controls while parsing reported zero errors).
- Literal kinds must be guessed from value shapes (`Text = "123"` vs `Width = 123`) — a
  whole bug class (M-2) that a typed property table eliminates.
- Hand-written grammar variants are silently rejected; there is no way to surface WHERE the
  document deviates.
- Every edit regenerates the whole method body, so diffs are noisy and formatting churns.

A dedicated XML dialect fixes all four structurally: the tree IS the document, the property
table IS the type system, and deviations are positioned diagnostics.

## Pipeline

```
MainWindow.mxaml                MainWindow.mxaml.cs (user-owned, never rewritten)
      │ designer edits                 ▲ never touched
      ▼                                │
LeXtudio.MewUI.Xaml ──generate──► obj/…/MainWindow.MewUI.g.cs
(MxamlDocument: parse/validate/          auto-included in compilation; also refreshed by the
 transform/canonical serialize)          designer right after each flush so F5 works without
                                         an explicit build)
```

There is NO Designer.cs on disk — WPF-style generated-code separation. The solution explorer
shows only `.mxaml`.

The out-of-process MewUI host switches from Roslyn transforms to MXAML transforms — making
it structurally identical to the GTK host (both are "document-model designers"). The DDP
surface, HostClient, preview rendering, property adapters, and DevFlow actions are unchanged;
only the wire payload becomes MXAML text.

## File format (.mxaml)

```xml
<?xml version="1.0" encoding="utf-8"?>
<MewUI xmlns="http://schemas.lextudio.com/mewui/2026"
       Class="MewUIFixture.Windows.MainWindow">
  <Window Name="MainWindow" Title="QuickNotes">
    <StackPanel Name="rootPanel" Spacing="8">
      <Label Name="heading" Text="QuickNotes"/>
      <StackPanel Name="toolRow" Spacing="6" Orientation="Horizontal">
        <Button Name="newButton" Content="New" Click="NewButton_Click"/>
      </StackPanel>
    </StackPanel>
  </Window>
</MewUI>
```

Rules:

1. Root element `<MewUI>` with required `Class` (namespace-qualified C# identifier) — names
   the partial class the generated code merges into.
2. Exactly one `<Window .../>` element child (the design surface root).
3. Control elements are named by their MewUI type (`StackPanel`, `Button`, ... — the same
   catalogue as the toolbox). Each carries a required `Name` (valid C# identifier, unique in
   the document).
4. Attributes are properties; values are always STRINGS in the XML — the property registry
   decides how the generator emits them (`"123"` vs `123` vs `true` vs `Orientation.Horizontal`).
   This kills the M-2 literal-kind guessing permanently.
5. Events are attributes whose name matches a control event (`Click`, `CheckedChanged`, ...)
   and whose value is a method identifier in the user-owned partial.
6. Children nest as elements; containers only (registry-checked, with line/column in the
   diagnostic).
7. Comments/PIs are dropped on save (canonical form). Whitespace inside property values is
   significant and preserved verbatim.

## Library API

```csharp
var doc = MxamlDocument.Parse(text);                    // throws MxamlException w/ line info
doc.Diagnostics                                         // parse + semantic diagnostics
doc.SetProperty("heading", "Text", "Configured");       // validated against the registry
doc.Add("toolRow", "TextBox");                          // unique naming built in
doc.Remove("textBox1"); doc.Rename("textBox1", "searchBox");
doc.Rename("heading", "bad name");                      // -> diagnostic, document untouched
string csharp = MewUICSharpGenerator.Generate(doc);     // strict InitializeComponent emitter
string xaml   = doc.ToXaml();                           // canonical serialization
```

All mutations validate FIRST and leave the document untouched on failure; accumulated
diagnostics carry line/column from `IXmlLineInfo`.

## Property registry

`MewUIControlCatalog` mirrors the verified Aprillz.MewUI 0.12 API surface:

| Kind | Example | Generated form |
|---|---|---|
| String | `Label.Text`, `Button.Content` | `.Text = "v";` |
| Double | `StackPanel.Spacing`, `Window.Width` | `.Spacing = 8;` |
| Boolean | `CheckBox.IsChecked`, `DockPanel.LastChildFill` | `.IsChecked = true;` |
| Int32 | `ComboBox.SelectedIndex` | `.SelectedIndex = 2;` |
| Enum | `StackPanel.Orientation` | `.Orientation = Orientation.Horizontal;` |

Unknown properties on known controls default to String (permissive, Info-level diagnostic).
Unknown control types are retained in the tree (round-trip safe) with a Warning diagnostic.

## C# generation contract

Unchanged from the strict grammar documented in [`mewui-designer.md`](mewui-designer.md):
ordered sections (fields → constructions → property assignments → relationship calls →
`Content = root`), no `this.` prefix, no initializers, generated field name = stable component
identity. Events emit `name.Handler += Handler;` next to the property block.

## Rollout

| Phase | Scope |
|---|---|
| 1 (this change) | Library + generator + unit suite + slnx registration; designer keeps working off C# until cut over |
| 2 | MewUIDesigner.Host switches to MXAML payloads; `.mxaml` secondary binding replaces the `.cs` one; MSBuild generation targets ship (`*.MewUI.g.cs` into `obj/`, no Designer.cs anywhere); migration action converts legacy `*.Designer.cs` then deletes it |
| 3 | Hardlink dedup pass + shared-framework trim for the new Host folders (addin-sdk.md Phase 2) |
