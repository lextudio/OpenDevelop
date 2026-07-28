# XML Editor AddIn

**Status (2026-07-28): one shared source set; UnoDevelop links 78 of 98 files from here.**

`src/AddIns/DisplayBindings/XmlEditor/` existed as two full, independently-maintained copies - this
one, and a hand-copied duplicate in UnoDevelop's own tree. Measured before touching anything:

| Category | Count |
|---|---|
| Byte-for-byte identical | **78** |
| Diverged (>20 diff lines) | 7 |
| Diverged (<=20 diff lines) | 7 |
| WPF-only, absent from UnoDevelop | 6 |
| UnoDevelop-only (`_WpfToPort/` + `Stubs.cs`) | 9 |

The 78 identical ones are now linked from here via `$(SharpDevelopSourceRoot)` in UnoDevelop's
`XmlEditor.csproj` instead of being a second copy - the schema/completion/parsing/folding/XPath core
(`XmlSchemaCompletion*`, `XmlCompletionItem*`, `XmlParser`, `XmlElementPath*`, `XmlFold*`,
`XPathQuery`, `RegisteredXmlSchemas`, `QualifiedName*`, and the whole `*Command.cs` set) plus the
tree-node model (`XmlElementTreeNode`/`XmlTextTreeNode`/`XmlCommentTreeNode`/`XmlCharacterDataTreeNode`).

Two things are deliberately **not** shared, and the distinction matters:

- **The 6 WPF-only UI classes** (`XmlTreeView`, `XmlTreeViewControl`, `XmlTreeViewContainerControl`,
  `SelectXmlSchemaWindow.xaml.cs`, `XmlSchemasPanel.xaml.cs`, `XPathQueryControl`) are WPF
  `UserControl`s with no Uno equivalent. UnoDevelop keeps the originals parked in its own
  `_WpfToPort/` (excluded from compilation) and satisfies the references with a local `Src/Stubs.cs`.
  These are explicitly listed in the link's `Exclude` - compiling them into the Uno build would not
  work. Note the consequence, which is a real feature gap and not a dedup artifact: every tree-view
  command (`AddAttributeCommand`, `AddChildElementCommand`, `ExpandAllCommand`, ... - all linked and
  compiling fine) is inert in UnoDevelop, because `Stubs.cs`'s `XmlTreeViewContainerControl` methods
  are empty. The XML tree-view pad itself is unported.
- **The 14 diverged files** are kept local to UnoDevelop and excluded from the link. Most were
  reworked for Uno (`AddXmlNodeDialog` at 274 diff lines is effectively a rewrite; `XPathNodeTextMarker`,
  `XPathQueryPad`, `XmlFoldingManager`, `FoldingManagerAdapter`, `XmlView`, `XmlDisplayBinding`,
  `XmlFormattingStrategy`, `XmlEditorOptionsPanel.xaml.cs` follow the same pattern). Reconciling
  those is a per-file question about which host's behavior should win, not mechanical dedup, and was
  not attempted here.

Arithmetic worth keeping as a check when this is revisited: 98 upstream files − 20 excluded (14
diverged + 6 WPF-only) = 78 linked, which matches the byte-identical count exactly. If a future pass
finds those numbers no longer reconcile, a file has silently diverged or been added on one side only.

Verified: `XmlEditor.csproj` builds standalone, UnoDevelop's full `UnoDevelop.slnx` builds, and
`UnoDevelop.Core.Tests` (204) + `UnoDevelop.IntegrationTests` (70) pass.

## Where the wholesale-copy dedup effort ends

XmlEditor was the last AddIn in this codebase that was a byte-identical wholesale copy. Measured for
comparison in the same pass, with zero identical files each:

- `Misc/AndroidSdkManager`, `Misc/AndroidDeviceManager` - every shared-name file diffs at roughly
  twice its own line count (e.g. `AvdManagerService.cs`: 424 diff lines over 227 lines), i.e. nearly
  every line differs; both also carry a UnoDevelop-only `*ViewContent.cs`.
- `Misc/TextTemplating` - same shape across all 6 shared-name files, plus a UnoDevelop-only
  `TextTemplatingStartup.cs`.
- `DisplayBindings/IconEditor` - different directory layout entirely (WPF `*.xaml.cs` flat here vs
  `Project/Src/` there): two separate UI implementations, not a copy.

These are re-implementations of the same features on a different UI stack, not duplicates. Unifying
them would mean choosing a canonical behavior per feature and re-verifying it on both hosts - the
kind of work that needs the real call sites and requirement differences measured first (see
`nuget.md` for what happens when that step is skipped).

## Plan: porting the tree-view UI to Uno/WinUI

Status: in progress. This is new UI development, not a dedup pass - each file below gets a genuine
Uno/WinUI implementation, not a mechanical translation.

Scope: the 6 WPF-only files, plus `AddXmlNodeDialog.cs` and `XPathNodeTextMarker.cs` which are
diverged-and-local already but depend on the tree UI:

| File (in `_WpfToPort/`) | Lines | Role | Port approach |
| --- | --- | --- | --- |
| `XmlTreeViewControl.cs` | 580 | WPF TreeView + `ObservableCollection<XmlTreeNode> Nodes` | WinUI `TreeView` bound to the same `Nodes` collection; reuse the already-shared node model (`XmlElementTreeNode` etc.) unchanged - only the view binding changes |
| `XmlTreeViewContainerControl.cs` | 653 | Hosts the tree + implements the 12 tree-edit commands (AddAttribute, RemoveAttribute, AddChildElement, AppendChildComment, AppendChildTextNode, InsertElementBefore/After, InsertCommentBefore/After, InsertTextNodeBefore/After, ExpandAll, CollapseAll) | Port command bodies as real logic operating on the shared node model; these replace the corresponding empty methods in `Src/Stubs.cs` |
| `XmlTreeView.cs` | 182 | Pad-level wrapper registering the tree view as a `PadContent`/DisplayBinding | Follow the same pad-registration pattern already used by the ported Solution Explorer pad |
| `SelectXmlSchemaWindow.xaml.cs` | 100 | Modal schema-picker dialog | New WinUI `ContentDialog` |
| `XmlSchemasPanel.xaml.cs` | 243 | Options panel listing registered schemas | New WinUI options-panel page, registered in `XmlEditor.addin`'s `<OptionPanel id="XmlSchemasPanel">` |
| `XPathQueryControl.cs` | 497 | Backs `XPathQueryPad` - run an XPath query against the open document | New WinUI pad content; reuse the already-shared `XPathQuery`/query-execution logic, only the input box + results list view is new |
| `AddXmlNodeDialog.cs` | 267 | Dialog for adding a new XML node (already diverged/local, not WPF-only) | Check whether it already targets WinUI; if so just wire it to the new container control's commands, no rewrite needed |
| `XPathNodeTextMarker.cs` | 80 | Diverged/local already | Check if it needs updates to match the new tree control's node selection API |

Order of work: `XmlTreeViewControl` and `XmlTreeViewContainerControl` first (everything else in this
list depends on having a real, working tree), then `XmlTreeView` (pad registration), then the two
dialogs/panels, then `XPathQueryControl` last (least coupled to the tree).

Once each `_WpfToPort/` file has a real replacement, delete the corresponding empty method(s) from
`Src/Stubs.cs` and remove the file from `_WpfToPort/` and the csproj's `<Compile Remove>` exclusion -
mirroring how GitAddIn's fake `ProcessRunner` stub was deleted once a real implementation existed,
rather than keeping stub and implementation side by side.

Verification for each stage: `XmlEditor.csproj` standalone build, full `UnoDevelop.slnx` build,
`UnoDevelop.Core.Tests` (204), and a single serial (not concurrent) `UnoDevelop.IntegrationTests` run
(70) - concurrent IntegrationTests runs produce false failures via DevFlow port conflicts, see the
NuGet section's methodology notes for why builds/tests must be verified one at a time.
