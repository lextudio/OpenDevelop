# XamlDom vs Microsoft XAML loader

The WPF designer edits through **XamlDom** (`externals/vscode-wpf/external/WpfDesigner/WpfDesign.XamlDom`),
the SharpDevelop-derived document model. Microsoft open-sourced the WPF XAML stack
(`System.Xaml`, `XamlReader`, `XamlWriter`). This note records a measured comparison of the two, and what
it means for replacing parts of XamlDom. The harness is `tests/OpenDevelop.XamlDom.Tests`; run it with
`dotnet test --project tests/OpenDevelop.XamlDom.Tests/OpenDevelop.XamlDom.Tests.csproj`.

## Method

47 XAML snippets (`XamlCorpus.cs`) cover resources of every primitive and freezable kind, markup
extensions, styles, templates, triggers, animations, bindings, transforms, geometry, input bindings, menus,
text and whitespace, and attached properties. Two axes are measured:

1. **Load fidelity** (`XamlLoaderComparisonTests`). Each snippet is loaded with XamlDom
   (`XamlParser.Parse(...).RootInstance`) and with `XamlReader.Parse`; both object graphs are dumped by
   `XamlGraphDump` (types, explicitly set dependency properties, resource dictionaries, logical children,
   `ItemsControl.Items`, template trees, triggers, bindings with all parameters, animations and key frames,
   transforms, geometry) and compared as text. The reference load must succeed, so two loaders failing the same
   way never counts as a match. `XamlGraphDumpTests` guards the dump itself against silently dropping a kind
   of content.
2. **Write-back fidelity** (`XamlRoundTripTests`). XamlDom parses and saves without an edit; the result must
   equal the source. A designer changes only what the user edited, so this is the property it cannot do without.
   Microsoft's `XamlWriter` is the contrast: it serializes the live object graph.

What the dump leaves out: each loader's own bookkeeping attached properties (base URI, namespace maps, name
scopes, `mc:Ignorable`, `d:` values, the text editor's internal state), layout, and anything that is not a local
value, a resource, a logical child or one of the items above. Two loaders can still differ there unnoticed.

The expected differences live in three lists, each with the reason, and each enforced in both directions: a case
that starts matching (or loading) fails its test until it is removed, so the lists only shrink.

| List | Where | Meaning |
|---|---|---|
| `NotLoadableByXamlDom` | `XamlCorpus` | XamlDom cannot load the case at all |
| `KnownDifferences` | `XamlLoaderComparisonTests` | XamlDom loads it but builds a different graph |
| `KnownRoundTripDifferences` | `XamlRoundTripTests` | XamlDom does not save the source back unchanged |

## Results

| | cases | match | differ | XamlDom cannot load |
|---|---|---|---|---|
| Load fidelity | 47 | 40 | 6 | 1 |
| Write-back, XamlDom | 46 (the other one does not load) | 45 | 1 | - |
| Write-back, `XamlWriter` | 47 | **0** | 47 | - |

The "cannot load" cases are listed once, in `XamlCorpus.NotLoadableByXamlDom`; the load comparison checks the
list against what XamlDom really does, and the round-trip test skips exactly those.

`XamlWriter` serialized every snippet but reproduced none of them: formatting, comments, namespace prefixes,
attribute order, implicit content and default-valued properties are all rewritten. It cannot be used to save
an edited document.

Remaining load differences, the 6 that load plus the 1 that does not (XamlDom is wrong, Microsoft's loader is the reference).
All results were confirmed on Windows against real WPF (net10.0-windows, arm64); the same 96 tests pass there:

| Difference | Effect |
|---|---|
| A `DataTemplate` with only `DataType` gets no implicit `DataTemplateKey` | The template is stored under a different key than the one WPF derives from `DataType` |
| Inline text between `Run`/`Italic` is trimmed; whitespace-only runs are dropped | Design view text differs from runtime |
| `DataTrigger.Binding` is null | A trigger's data condition is lost in the design model. Cause not yet investigated |
| `MenuItem.Role` differs (`TopLevelHeader` in the reference, `SubmenuHeader` in XamlDom) | The item's role depends on its parent. Probable cause, not verified: the item is given its parent after its subtree is built |
| `x:Uid` is not applied to the instance (2 snippets) | Minor. Cause not yet investigated |
| `mc:AlternateContent` is not supported (`Cannot find type mc:AlternateContent`) | Rare |

Write-back difference: the `Storyboard` content of `BeginStoryboard` is written back as an explicit
`<BeginStoryboard.Storyboard>` property element, so an unedited document would change on save. Cause not yet
investigated.

Not XamlDom's fault:

- The suspected LibreWPF quirk for `Lookup by system key` was **refuted** on Windows: real WPF also resolves to the system
  brush, so `ReferenceQuirksOffWindows` was removed and XamlDom was fixed (see below). No other result differed between
  LibreWPF and real WPF.
- `ComboBox` cannot be constructed under LibreWPF (both loaders hang), and `x:Shared` is
rejected by Microsoft's loader outside compiled dictionaries.

## Conclusion

Keep XamlDom's document model: nothing in `System.Xaml` replaces its lossless editing, which is the reason a
designer is built on it. Its **XAML semantics** (type resolution, built-in types, key evaluation, implicit keys,
text handling) are where it falls short of Microsoft's loader, and those are the parts to take from
`System.Xaml` (`XamlSchemaContext`, `XamlLanguage`, value conversion) one difference at a time, each closed by
deleting its line from the list. Suggested order: implicit `DataTemplate` keys, inline text, `DataTrigger.Binding`.

Open items, each with the test that tracks it (the case name in `XamlCorpus`):

| Item | Case | List |
|---|---|---|
| Implicit `DataTemplateKey` for a `DataType`-only template | `ControlTemplate and DataTemplate` | `KnownDifferences` |
| Trimmed inline text between `Run`/`Italic` | `Content property and text` | `KnownDifferences` |
| `DataTrigger.Binding` is null | `Triggers` | `KnownDifferences` |
| `MenuItem.Role` | `ToolTip, ContextMenu and Menu` | `KnownDifferences` |
| `x:Uid` not applied | `x:Name x:Uid x:Key forms`, `Uri and Key as attribute order` | `KnownDifferences` |
| `mc:AlternateContent` | `Designer attributes` | `NotLoadableByXamlDom` |
| `BeginStoryboard` content written back as a property element | `Animations and storyboards` | `KnownRoundTripDifferences` |

Fixed during this comparison:

The fixes are commits in the nested `WpfDesigner` repository (`externals/vscode-wpf/external/WpfDesigner`, branch
`opendevelop`), picked up through the `vscode-wpf` submodule pointer: `7760bcc` (XAML language primitives), `27dcc3f`
(merged dictionaries) and `e4bb51a` (`x:Key` markup).

- A `{x:Static ...}` or `{x:Type ...}` `x:Key` was stored as its markup text, so the dictionary key was a string; it is now
  evaluated (`CollectionSupport.GetDictionaryKey`), and the `{}` escape is removed. System resource keys and type keys are
  the same objects as in WPF.
- A `ResourceDictionary` nested in `<ResourceDictionary.MergedDictionaries>` that had any entry made the whole file
  fail to load (`The reference node is not a child of this node`). `ParseObjectContent` treated every
  collection-typed element as the collection instance of its parent's property, so the nested dictionary's
  children were added to the parent's `MergedDictionaries`; it now does so only when the element's type fits
  that property. The same change keeps the original stack of load errors (`ReportException` used `throw x;`).
- `{StaticResource}` with a `SystemResourceKey` (e.g. `{x:Static SystemColors.ControlBrushKey}`) searched the element tree and
  found a local brush defined under that key. WPF's `StaticResourceExtension.ProvideValue` returns the system resource for
  such keys without any lookup, so local overrides only affect `{DynamicResource}`; `StaticResourceWrapper` now does the same
  (not yet committed in WpfDesigner).
- `<x:Double>`, `<x:String>` and the other XAML language primitives were dropped by XamlDom entirely (commit
  `7760bcc` in WpfDesigner), which made the resource picker offer nothing for `FontSize="{StaticResource Small}"`.

Not measured: error positions and recovery (line/column of load errors, behaviour on invalid XAML), and load
speed. XamlDom keeps source positions from the XML document; `System.Xaml`'s exceptions carry line numbers
too, so a later pass could compare both.
