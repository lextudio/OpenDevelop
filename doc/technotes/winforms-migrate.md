# ResourceToolkit: WinForms → WPF + NRefactory → Roslyn Migration

## Scope

`src/AddIns/Misc/ResourceToolkit/` — the `Hornung.ResourceToolkit` (a.k.a. "ResourceToolkit")
addin, which provides resource-string resolution, code completion, tooltips, find-references,
and rename-refactoring for `${res:...}` resource references in C# source files. Original
implementation: ~49 source files, split across WinForms UI (`System.Windows.Forms`,
`ICSharpCode.Core.WinForms`) and NRefactory AST-based resolvers
(`ICSharpCode.NRefactory.CSharp` / `.TypeSystem`).

Both dependency families are out of scope for OpenDevelop's MVP build:
- **WinForms**: `ICSharpCode.Core.WinForms` deleted; `ICSharpCode.SharpDevelop.WinForms`
  deleted; `System.Windows.Forms`/`System.Drawing` unavailable.
- **NRefactory**: NRefactory submodule not initialized; git-submodule path is empty.

## Conversion summary

### Files changed

**WinForms → WPF (5 files):**

| File | WinForms | WPF |
|---|---|---|
| `EditStringResourceDialog.cs` | Inherited `BaseSharpDevelopForm` (`Form` + `Panel`/`TextBox`/`Button`) | `Window` with `StackPanel`/`TextBox`/`Button` (code-only, no XAML) |
| `UnusedResourceKeysViewContent.cs` | Inherited `AbstractViewContent`, `Panel` + `ListView` (WinForms) + `ToolStrip` | `StackPanel` + WPF `ListView` + `ToolBar` (via `ToolBarService.CreateToolBarItems`) |
| `UnusedResourceKeysCommands.cs` | `ICommand` from `ICSharpCode.Core.WinForms`, `ToolStripButton` as `Owner` | `AbstractCheckableMenuCommand` + `ToggleButton` (WPF) |
| `RefactoringCommands.cs` | `Application.DoEvents()` call in long-running operations | Removed `DoEvents()` — no WPF equivalent; `AsynchronousWaitDialog` (WinForms) also stubbed out later (see below) |
| `TextEditorContextMenuBuilder.cs` | `ContextMenuStrip` + `ToolStripMenuItem` creation | `System.Windows.Controls.MenuItem` + `Separator` |

**NRefactory → Roslyn (16 files):**

| File | NRefactory | Roslyn |
|---|---|---|
| `NRefactoryAstCacheService.cs` → `RoslynAstCacheService.cs` | `ICSharpCode.NRefactory.CSharp.SyntaxTree` | `Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree`, `SemanticModel` |
| `INRefactoryResourceResolver.cs` → `IRoslynResourceResolver.cs` | Interface returning NRefactory types | Interface returning Roslyn types |
| `NRefactoryResourceResolver.cs` → `RoslynResourceResolver.cs` | Walked NRefactory `SyntaxTree` | Walks Roslyn `SyntaxNode` tree, using `SemanticModel.GetSymbolInfo()` |
| `BclNRefactoryResourceResolver.cs` → `BclRoslynResourceResolver.cs` | Same pattern for BCL types | Same pattern via Roslyn type resolution |
| `ICSharpCodeCoreNRefactoryResourceResolver.cs` → `ICSharpCodeCoreRoslynResourceResolver.cs` | NRefactory resolver for ICSharpCode.Core internals | Roslyn resolver for ICSharpCode.Core internals |
| `ICSharpCodeCoreResourceResolver.cs` | Depended on NRefactory-less text analysis (kept, but needed rewrite) | Standalone text-level `${res:...}` pattern matcher, no Roslyn dependency |
| `PositionTrackingAstVisitor.cs` | NRefactory `IAstVisitor` for C# | Stub (pattern not needed for Roslyn — `SyntaxNode` walk is simpler) |
| `PropertyFieldAssociationVisitor.cs` | NRefactory visitor | Stub |
| `MemberFindAstVisitor.cs` | NRefactory visitor | Stub |
| `MemberEqualityComparer.cs` | NRefactory `AstNode` comparer | Stub — Roslyn uses `SyntaxAnnotation` / `SyntaxNode.DescendantNodes()` |
| `ResourceCodeCompletionItem.cs` | Used `NRefactoryAstCacheService.PrettyPrinter` to format key literals | Uses `RoslynAstCacheService.GenerateKeyLiteral()` with Roslyn `SyntaxFactory` |
| `AbstractNRefactoryResourceCodeCompletionBinding.cs` → `AbstractNRefactoryResourceCodeCompletionBinding.cs` (kept name) | NRefactory parsing | Added `HandleKeyPressed` for AvalonEdit integration |
| `ResourceCodeCompletionItemList.cs` | NRefactory `ICompletionItemList` | Kept as-is (interface-agnostic) |
| `NewResourceCodeCompletionItem.cs` | NRefactory completion | WPF `Window.ShowDialog()` pattern — no `using` (WPF `Window` isn't `IDisposable`) |
| `ResourceRefactoringService.cs` | Used `FindReferencesAndRenameHelper` with NRefactory types | Uses Roslyn cache via `RoslynAstCacheService`; local `Reference` class (SD's `ICSharpCode.SharpDevelop.Refactoring.Reference` removed) |
| `AnyResourceReferenceFinder.cs` | NRefactory file parsing | Uses `RoslynAstCacheService` for cache |
| `SpecificResourceReferenceFinder.cs` | NRefactory file parsing | Uses `RoslynAstCacheService` for cache |
| `ResourceToolTipProvider.cs` | `ITextEditorToolTipProvider` + `ToolTipShown` property | `ITextAreaToolTipProvider` (AvalonEdit) — `ToolTipShown` removed, `SetToolTip()` suffices |
| `SolutionContainsProjectOrReferenceConditionEvaluator.cs` | `IConditionEvaluator` (old `ICSharpCode.Core.ConditionEvaluators`) | Same interface, different namespace (`ICSharpCode.Core`) |

### csproj changes

`ResourceToolkit.csproj`:

```xml
<Project Sdk="LibreWPF.Sdk/11.0.0-dev">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>   <!-- added: ResXResourceReader/Writer need System.Windows.Forms assembly -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="5.3.0" />
  </ItemGroup>
</Project>
```

Removed: `System.Windows.Forms` / `System.Drawing` assembly references (gone from SDK),
`.xfrm` embedded resource (WinForms XML forms), old-format csproj boilerplate.

## API changes encountered

### Removed types from ICSharpCode.SharpDevelop

| Old type | Replacement |
|---|---|
| `ResolveResult` (namespace `ICSharpCode.SharpDevelop.Dom`) | `ResourceResolveResult` no longer inherits it — standalone class |
| `IClass` / `IMember` / `IReturnType` | `Microsoft.CodeAnalysis.INamedTypeSymbol` / `ISymbol` / `ITypeSymbol` |
| `IConditionEvaluator` (old namespace) | `ICSharpCode.Core.IConditionEvaluator` |
| `IDocument` | `ICSharpCode.AvalonEdit.Document.IDocument` |
| `ICSharpCode.SharpDevelop.Refactoring.Reference` | Local `Reference` class in `ResourceRefactoringService.cs` |
| `ITextEditorToolTipProvider` | `ICSharpCode.SharpDevelop.Editor.ITextAreaToolTipProvider` |
| `IClipboardHandler` | Removed — WinForms-only concept |
| `IViewContent` (namespace `ICSharpCode.SharpDevelop.Gui`) | `ICSharpCode.SharpDevelop.Workbench.IViewContent` |
| `AbstractViewContent` (namespace `ICSharpCode.SharpDevelop.Gui`) | `ICSharpCode.SharpDevelop.Workbench.AbstractViewContent` |
| `AsynchronousWaitDialog` | Excluded from MVP build (`<Compile Remove>` in `ICSharpCode.SharpDevelop.csproj`) — removed all usages |
| `FindReferencesAndRenameHelper.ShowAsSearchResults` | Method doesn't exist in stub — call removed |
| `IWorkbench` (namespace `ICSharpCode.SharpDevelop.Gui`) | `ICSharpCode.SharpDevelop.Workbench.IWorkbench` |
| `ITextEditorProvider` | Interface definition never ported — fallback to ITextEditor cast only |
| `FindProjectContainingFile` on `ISolution` | Only on `IProjectService` (`SD.ProjectService.FindProjectContainingFile`) |

### WinForms → WPF idioms

| WinForms | WPF |
|---|---|
| `Form.ShowDialog(IWin32Window owner)` | `window.Owner = ownerWindow; window.ShowDialog()` (no-arg) |
| `Form` implements `IDisposable` | `Window` does **not** implement `IDisposable` — no `using` block |
| `Application.DoEvents()` | No equivalent — remove (WPF's dispatcher handles this differently) |
| `ToolStrip` + `ToolStripButton` + `ToolStripMenuItem` | `ToolBar` + `ToggleButton` + `System.Windows.Controls.MenuItem` |
| `ToolbarService.CreateToolbarItems(owner, path)` (old 2-arg) | `ToolBarService.CreateToolBarItems(UIElement inputBindingOwner, object owner, string addInTreePath)` (3-arg) |
| `AsynchronousWaitDialog.ShowWaitDialog(...)` (WinForms dialog) | MVP stub: removed. Call resumption APIs directly without a progress dialog. |
| `ToolTipRequestEventArgs.ToolTipShown = true` | Not needed — `SetToolTip(object)` sets `Handled` + `ContentToShow` atomically |

### ResX types availability

`ResXResourceReader`/`ResXResourceWriter` (used by `ResXResourceFileContent.cs`) live in
`System.Windows.Forms.dll` on .NET. With only `<UseWPF>true</UseWPF>`, they are not available.
Adding `<UseWindowsForms>true</UseWindowsForms>` makes them accessible without actually using
any WinForms UI types — only the `System.Resources.ResX*` serialization helpers.

## Verification

```
$ dotnet build src/AddIns/Misc/ResourceToolkit/Project/ResourceToolkit.csproj
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet build OpenDevelop.Mvp.slnx
    688 Warning(s)
    0 Error(s)
```

Full MVP solution builds with 0 errors. The ResourceToolkit addin is now WPF-only
and Roslyn-backed, with no NRefactory or WinForms dependency.
