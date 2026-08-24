# LeXtudio.MewUI.Xaml (.mxaml)

## Status

Approved direction (2026-08-23). Supersedes the "C# syntax tree is the authoritative
document" decision for the DESIGNER layer only. The Aprillz.MewUI runtime stays C#-first:
it gains no XAML reader, and generated applications never load XML at runtime.

## Why

The direct-C# designer (Roslyn transforms over a strict InitializeComponent grammar) works,
but every real usage exposed friction rooted in the same cause: **the document is program
text**.

- Containment lives in `parent.Children(child, ...)` calls; omitting ONE call silently
  drops a subtree (measured: a missing `generalGroup.Children(generalStack)` erased three
  controls while parsing reported zero errors).
- Literal kinds must be guessed from value shapes (`Text = "123"` vs `Width = 123`) — a
  whole bug class that a typed property table eliminates.
- Hand-written grammar variants are silently rejected; there is no way to surface WHERE the
  document deviates.
- Every edit regenerates the whole method body, so diffs are noisy and formatting churns.

A dedicated XML dialect fixes all four structurally: the tree IS the document, the property
table IS the type system, and deviations are positioned diagnostics.

## Pipeline

There is NO Designer.cs on disk — WPF-style generated-code separation:

```
MainWindow.mxaml                MainWindow.cs (user-owned, never rewritten)
      │ designer edits                 ▲ never touched
      ▼                                │
LeXtudio.MewUI.Xaml ──generate──► obj/…/MainWindow.MewUI.g.cs (auto-included in compilation;
(MxamlDocument: parse/validate/          also refreshed by the designer right after each
 transform/canonical serialize)          flush so F5 works without an explicit build)
```

## File format (.mxaml)

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window xmlns="http://schemas.lextudio.com/mewui/2026"
        Class="MewUIFixture.Windows.MainWindow">
  <StackPanel Name="rootPanel" Spacing="8">
    <Label Name="heading" Text="QuickNotes"/>
  </StackPanel>
</MewUI>
```

Rules:

1. Root element `<Window>` with required `Class` attribute naming the partial class.
2. Control elements named by their MewUI type; each carries a required `Name`.
3. Attributes are properties; values are always STRINGS in the XML — the property registry
   decides how the generator emits them.
4. Events are attributes matching a control event name; value = method identifier.
5. Children nest as elements; containers only (registry-checked).
6. Canonical form: 4-space indent, comments dropped.

## Property registry

| Kind | Example | Generated form |
|---|---|---|
| String | `Label.Text` | `.Text = "v";` |
| Double | `StackPanel.Spacing` | `.Spacing = 8;` |
| Boolean | `CheckBox.IsChecked` | `.IsChecked = true;` |
| Int32 | `ComboBox.SelectedIndex` | `.SelectedIndex = 2;` |
| Enum | `StackPanel.Orientation` | `.Orientation = Orientation.Horizontal;` |

Unknown properties on known types default to Unsupported (generator emits a comment).

## Rollout

| Phase | Scope |
|---|---|
| 1 ✅ | Library + generator + unit suite (13/13) |
| 2 ⏳ | Host switches to MXAML payloads; MSBuild targets; fixture migration |
| 3 | Hardlink dedup + shared-framework trim for new Host folders |
