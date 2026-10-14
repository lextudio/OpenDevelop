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

**MewUI 0.22.0 (2026-10-14).** The fixture (`tests/fixtures/MewUIFixture`) now pins Aprillz.MewUI.Core
0.22.0, the designer host's version, and the registry targets it (writable public properties, checked
by reflection):

- Every FrameworkElement control (not Window, nor TabItem, a plain MewObject with its own
  IsEnabled/HeaderText) accepts MinWidth/MinHeight/MaxWidth/MaxHeight, Opacity, IsEnabled, IsVisible,
  IsHitTestVisible and HorizontalAlignment/VerticalAlignment (Left|Center|Right|Stretch,
  Top|Center|Bottom|Stretch). Slider and ProgressBar get Minimum/Maximum/Value (Slider also
  SmallChange/LargeChange/ChangeOnWheel, ProgressBar IsIndeterminate); ComboBox
  ZebraStriping/ChangeOnWheel/ItemHeight; ListBox SelectedIndex/ZebraStriping/ItemHeight.
- Button, CheckBox and RadioButton are content controls in 0.22 (their `Content` is an Element; CheckBox
  and RadioButton lost `Text`). Their caption - `Content` on all three, and the `Text` attribute existing
  documents use on CheckBox/RadioButton - is the `ContentText` kind, generated as the runtime's
  `.Content("text")` extension (`accessKey` defaults to true). The preview already renders it as a label.
- Window's Width/Height/MinWidth/MinHeight/MaxWidth/MaxHeight are read-only (in 0.12 too, so any sized
  Window previously generated code that did not compile): they generate one
  `WindowSize = WindowSize.Resizable(width: …, height: …, minWidth: …)` with named arguments for the
  attributes present; Width and Height are both required, otherwise a comment is generated.
- **Thickness and Color kinds.** `Margin` (every FrameworkElement), `Padding` (all but Image and
  Separator), and on controls other than panels/Image/Separator `Background`/`Foreground`/`BorderBrush`
  (Color) and `CornerRadius`/`BorderThickness` (Double); Window takes the same visuals and `Padding`.
  A thickness is one, two (horizontal, vertical) or four numbers, comma or space separated, generated as
  `new Thickness(...)`; a colour is `#RRGGBB`/`#AARRGGBB`, generated as `Color.FromArgb(0xAARRGGBBu)`, or
  one of the runtime's 145 colour names (`Color.CornflowerBlue`). Anything else is an Error diagnostic,
  and the preview parses the same forms.

Verified by building the fixture against 0.22.0 and by compiling a probe document that uses every new
kind (window size, captions, items, range and common properties, thickness and colour values) against
0.22.0.

### Items (2026-10-14)

`ComboBox` and `ListBox` take a plain string list as `<Item>` children (they are not containers, so
no other child element is allowed):

```xml
<ComboBox Name="themeCombo" SelectedIndex="0">
    <Item>Light</Item>
    <Item>Dark</Item>
</ComboBox>
```

The generator emits the runtime's `params string[]` extension, `themeCombo.Items("Light", "Dark");`, before the
control's other properties (so an authored `SelectedIndex` is in range), and
the designer's preview sets the same strings as `ItemsSource = ItemsView.Create(...)`. An `<Item>` with
attributes or child elements is an error. Fixed alongside: Window-level properties were emitted once per
root attribute, and a Double one used the wrong attribute's value (`Width` took `Title`'s text).

## Rollout

| Phase | Scope |
|---|---|
| 1 ✅ | Library + generator + unit suite (13/13) |
| 2 ⏳ | Host switches to MXAML payloads; MSBuild targets; fixture migration |
| 3 | Hardlink dedup + shared-framework trim for new Host folders |
