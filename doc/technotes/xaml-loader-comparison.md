# XamlDom vs Microsoft XAML loader

The WPF designer edits through **XamlDom** (`externals/vscode-wpf/external/WpfDesigner/WpfDesign.XamlDom`),
the SharpDevelop-derived document model. Microsoft open-sourced the WPF XAML stack
(`System.Xaml`, `XamlReader`, `XamlWriter`). This note records a measured comparison of the two, and what
it means for replacing parts of XamlDom. The harness is `tests/OpenDevelop.XamlDom.Tests`; run it with
`dotnet test --project tests/OpenDevelop.XamlDom.Tests/OpenDevelop.XamlDom.Tests.csproj`.

## Method

45 XAML snippets (`XamlCorpus.cs`) cover resources of every primitive and freezable kind, markup
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

Both axes keep a list of known differences with the reason. A case that starts matching fails its test
until it is removed from the list, so the lists only shrink.

## Results

| | cases | match | differ | XamlDom cannot load |
|---|---|---|---|---|
| Load fidelity | 45 | 37 | 7 | 1 |
| Write-back, XamlDom | 44 (the other one does not load) | 43 | 1 | - |
| Write-back, `XamlWriter` | 45 | **0** | 45 | - |

The "cannot load" cases are listed once, in `XamlCorpus.NotLoadableByXamlDom`; the load comparison checks the
list against what XamlDom really does, and the round-trip test skips exactly those.

`XamlWriter` serialized every snippet but reproduced none of them: formatting, comments, namespace prefixes,
attribute order, implicit content and default-valued properties are all rewritten. It cannot be used to save
an edited document.

Remaining load differences, the 7 that load plus the 1 that does not (XamlDom is wrong, Microsoft's loader is the reference):

| Difference | Effect |
|---|---|
| An `{x:Static ...}` resource key is kept as markup text instead of being evaluated | System resource keys never match; the resource picker shows markup text |
| A `DataTemplate` with only `DataType` gets no implicit `DataTemplateKey` | The template is stored under a different key than the one WPF derives from `DataType` |
| Inline text between `Run`/`Italic` is trimmed; whitespace-only runs are dropped | Design view text differs from runtime |
| `DataTrigger.Binding` is null | A trigger's data condition is lost in the design model |
| `MenuItem.Role` is computed against the wrong parent | Items are attached to their parent after the subtree is built |
| `x:Uid` is not applied to the instance (2 snippets) | Minor |
| `mc:AlternateContent` is not supported | Rare |

Write-back difference: the `Storyboard` content of `BeginStoryboard` is rewritten as an explicit
`<BeginStoryboard.Storyboard>` property element, so an unedited document would change on save.

Not XamlDom's fault: `ComboBox` cannot be constructed under LibreWPF (both loaders hang), and `x:Shared` is
rejected by Microsoft's loader outside compiled dictionaries.

## Conclusion

Keep XamlDom's document model: nothing in `System.Xaml` replaces its lossless editing, which is the reason a
designer is built on it. Its **XAML semantics** (type resolution, built-in types, key evaluation, implicit keys,
text handling) are where it falls short of Microsoft's loader, and those are the parts to take from
`System.Xaml` (`XamlSchemaContext`, `XamlLanguage`, value conversion) one difference at a time, each closed by
deleting its line from `KnownDifferences`. Suggested order: `x:Static` keys, implicit
`DataTemplate` keys, inline text, `DataTrigger.Binding`.

Fixed during this comparison:

- A `ResourceDictionary` nested in `<ResourceDictionary.MergedDictionaries>` that had any entry made the whole file
  fail to load (`The reference node is not a child of this node`). `ParseObjectContent` treated every
  collection-typed element as the collection instance of its parent's property, so the nested dictionary's
  children were added to the parent's `MergedDictionaries`; it now does so only when the element's type fits
  that property. The same change keeps the original stack of load errors (`ReportException` used `throw x;`).
- `<x:Double>`, `<x:String>` and the other XAML language primitives were dropped by XamlDom entirely (commit
  `7760bcc` in WpfDesigner), which made the resource picker offer nothing for `FontSize="{StaticResource Small}"`.

Not measured: error positions and recovery (line/column of load errors, behaviour on invalid XAML), and load
speed. XamlDom keeps source positions from the XML document; `System.Xaml`'s exceptions carry line numbers
too, so a later pass could compare both.
