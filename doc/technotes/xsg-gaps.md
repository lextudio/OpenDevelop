# XSG (XAML Source Generator) support: gap analysis

Status: research, 2026-11-25. Read-only audit of what OpenDevelop supports for projects compiled with LibreWPF's
`LibreWPF.Xaml.SourceGenerator` (`UseLibreWpfXamlSourceGenerator=true`), compared with the BAML path. Sources: the
technotes (`xsg-hot-reload.md`, `sharpdbg-enc.md`, `xaml-language-servers.md`), `git log`, the tests named below, and
`openavalon/LibreWPF/Documentation/XamlSourceGeneratorUsage.md`. Nothing here was run for this note, so "unknown"
means "no code or test found", not "broken".

## What is covered

| Area | State | Evidence |
|---|---|---|
| Hot Reload: text push, code delta (`{cs:}`, events, new `x:Name`), rude edit to restart, compile error to build failure | done, agent and SharpDbg | `XsgHotReloadEndToEndTests`, `WpfHotReloadJourneyTests.XsgProject_CsExpressionEdit_AppliesAsACodeDelta` |
| Debugger: breakpoint rebind after a delta, active-frame remap at unchanged statements | done (not on XSG `#line` code) | `sharpdbg-enc.md` E3b |
| Designer opens Simpler XAML (no `xmlns`) | done | `ImplicitXamlNamespacesTests`, designer journeys |
| Designer ignores compiled-binding directives (`x:DataType`, `x:CompileBindings`) | done | commit `bc9fee46c3` |
| LSP: implicit namespaces opt-in per document; `{cs:}` and binding directives raise no false diagnostic | done | winappvsce `ImplicitNamespacePolicyTests`, `CSharpExpressionValueTests` |

## Status update 2026-11-25 (committed locally as 81613d72ca, not pushed)

The editing-experience gaps are closed for the LibreWPF profile, in the syntax of .NET MAUI (the private `{cs:}` form is gone):

| Gap | State | Evidence |
|---|---|---|
| G1 editor semantics (colour, completion, hover, definition, errors with the build's ids) | closed | winappvsce unit tests; journeys `XsgProject_CsExpressionValue_HasColourCompletionAndDefinitionInTheEditor` |
| G2 generator diagnostics while typing | closed for LWXSG0011 and LWXSG0012 and the expression errors (LWXSG0013 is reported by the build only) | winappvsce validator tests |
| G3 compiled bindings (`x:DataType`, path completion) | closed | same journey, `{Nick|}` completes `Nickname` |
| G4 designer with expressions | closed (shown as written, editable in the Properties pad, undo/redo exact) | `..._IsShownAsWrittenInTheDesigner`, `..._CanBeEditedInTheDesignerAndIsSavedAsTyped` |
| Hot Reload of expression, event and name edits | closed, also under SharpDbg | `XsgHotReloadEndToEndTests` 9/9, `XsgProject_CsExpressionEdit_AppliesAsACodeDelta` |
| global-using types | closed in generator, language server and designer | `..._GlobalUsingType_LoadsInTheDesignerWithoutAnXmlns` |

G7 is half done: the read-only DevFlow action `od.wpf-designer.xsg-status` reports XSG or BAML, Simpler XAML, C# expressions and the global-using namespaces of
the active designer's project (asserted in `XsgProject_GlobalUsingType_LoadsInTheDesignerWithoutAnXmlns`); a status-bar entry is still open. A project template is **blocked, not forgotten**: `LibreWPF.Sdk` and the generator are published only to lextudio GitHub Packages (see `AGENTS.md`), so a template
would produce a project that does not restore for anyone without that feed; revisit when the packages are on a public feed.

G8 is half done: a generator error now reaches the Error List on the XAML file and line (`XsgProject_GeneratorError_ReachesTheErrorListOnTheXamlLine`). It did not before:
the generator's diagnostic on an additional file is printed as `CSC : error LWXSG0011: <xaml>(line,col): ...`, which `MinimalMSBuildEngine` did not parse, so only the follow-on CS0103
errors showed. A second regex (`GeneratorDiagnosticLine`) reads that shape. Still open, with what was found when starting them (2026-11-25):
- "View generated `.g.cs`": **done.** The build does not write the generator's output to `obj/` by default (the `obj/**/generated/...` files in the fixture are from a manual
  build with `EmitCompilerGeneratedFiles`), so the source is the Roslyn host, which already runs the generator over the XAML AdditionalDocuments. New protocol method
  `roslyn/generatedSources` (`IRoslynLanguageProtocol.RoslynGeneratedSourcesAsync`, dispatcher, `RemoteRoslynLanguageProtocol`, `InProcessRoslynLanguageProtocol`,
  `Roslyn.Host/Program.cs`, `CSharpVBLanguageService.GetGeneratedSourcesAsync`) returns the generated documents whose `#line` directives name a XAML file, from the compilation, so with
  unsaved text too. `GeneratedSourceService` (Base) wraps it; the designer's context menu has **View Generated Code** (`ViewGeneratedCode`), which writes the text read-only under the user's
  temp folder and opens it. `od.xsg.generated-sources` and `od.wpf-designer.view-generated-code` expose the two levels. Tests:
  `XsgHotReloadEndToEndTests.GeneratedSources_OfAXamlFile_AreReadOverTheProtocol` (this file's document only, per-TFM slice, nothing for a `.cs` file) and the assertions in
  `XsgProject_GlobalUsingType_LoadsInTheDesignerWithoutAnXmlns` (through the real IDE, including the command). The text editor's context menu carries the same item for `.xaml` files (`ActiveContentExtension` condition in `WpfDesign.addin`); only its registration is checked (`AddInManifestTests`), no journey opens that menu.
- Breakpoints in an expression: **verified.** `XsgHotReloadEndToEndTests.BreakpointOnAnExpressionLine_HitsInTheXamlFile` sets a breakpoint on the XAML line of
  `Text="{= 'Hot reload ready'}"` through `DapSession`, the application stops there and the top frame is `MainWindow.xaml` at that line (the generator's `#line` makes the XAML the
  document of the generated code). The refusal near `HotReloadDeltaProducer.cs:253` concerns only the Hot Reload active-frame line map, not breakpoints. A step over from that stop
  stops again with reason `step` in `MainWindow.xaml` (the same test), then up to five more steps over, each stopping with reason `step` (and on a XAML line while still inside the window's generated code). Not run: other expression shapes (an event lambda, an interpolation), step into, and an edit while stopped inside an expression.

Still open and deliberately not part of this stage: G5 (no CI entry; needs an observed `test-plan.json` entry iterated through pushes, which waits for
the owner's go-ahead), G6, G7 (status bar; template blocked on a public feed), G8 (edit-while-stopped, step into and other expression shapes unchecked), G9, G10, G11, G12, G13. Nothing in them blocks authoring; they are guards, measurements and documentation.
See `xsg-maui-alignment-plan.md` for the verified/unverified list per stage. The text below is the original audit.

## Gaps

Ordered by user-visible impact. "Where" names the repository that has to change.

### G1. `{cs:}` has no editor semantics (confirmed)

The language server only stays out of the way (`CSharpExpressionValueTests`: no diagnostic, and a real error beside it
is still reported). It does not parse the expression. There is no completion, hover, go-to-definition, rename or
type check inside `{cs: ...}`, and `CompletionProvider` handles ordinary markup extensions only. A wrong expression
surfaces only at build time as a generator diagnostic. Where: winappvsce (`WinUiXaml.LanguageServer`), plus the
Roslyn host if the expression is bound to the x:Class code-behind symbols.

### G2. Generator diagnostics are not shown while typing

The generator reports `LWXSG0003` to `LWXSG0010` and `LWXSG5001` only through the build. The IDE host already runs the
generator (it keeps XAML as AdditionalDocuments), but nothing found publishes those diagnostics as editor squiggles,
and no test pins that they reach the Error List with the XAML file and line. `LWXSG0004` (`{cs:}` while the feature
is off) and `LWXSG0006` (compiled-binding path names a missing property) are the ones an author hits first. Where:
OpenDevelop language-service layer, winappvsce.

### G3. Compiled bindings are invisible to the editor

`x:DataType` and binding paths are checked by the generator (`LWXSG0005`/`0006`/`0007`). The LSP accepts the
directives but does not resolve the type or complete binding paths from it. Same shape as G1. Where: winappvsce.

### G4. Designer behaviour for `{cs:}` is unknown

No code or test in `src/AddIns/DisplayBindings/WpfDesign` or `externals/WpfDesigner` mentions `{cs:}`. The designer
reads XAML with `XamlDom`, which skips most namespace-qualified attributes, so a `{cs:}` value may render as a literal
string, be dropped, or fail. Needs an experiment on `tests/fixtures/XsgLocalFeedFixture` (open `MainWindow.xaml`, which
carries `{cs: "Hot reload ready"}`, and read what the design surface shows and whether saving round-trips the text
byte for byte). The round-trip baseline already records the fixture's XAML files. Where: WpfDesigner, then a journey.

Measured 2026-11-25 on the MAUI-syntax fixture (`Text="{= 'Hot reload ready'}"`): `XsgProject_OldAndNewStyleXaml_BothLoadInTheDesigner`,
`XsgProject_CompiledBindingDirectives_DoNotStopTheDesigner` and `XsgProject_WithSimplerXamlOff_...` pass, so the document loads
(`designerLoaded`) and loading leaves the file byte-identical. **Fixed 2026-11-25 (local, uncommitted).** Before: the designer's XamlDom parsed `{= 'x'}` as a markup extension, failed, and
dropped the property, so `Text` read as an empty string (measured with the read-only DevFlow action
`od.wpf-designer.properties-pad.get <property>`) and an edit in the Properties pad would have overwritten the expression. Now
`XamlExpressionSyntax.IsExpression` (WpfDesign.XamlDom, a third copy of the generator's rule) routes such a value to a
`XamlTextValue` that keeps the attribute text and is not applied to the design instance (`XamlProperty.UpdateValueOnInstance`);
the host reports the property as `Kind = Reference`, read-only, with the authored text, so the pad shows `{= 'Hot reload ready'}`
and cannot replace it. The design surface itself still shows the property's default (an empty label): nothing evaluates the
expression. `XsgProject_CsExpressionValue_IsShownReadOnlyInTheDesigner` pins it (with a literal as the control); the three XSG
designer journeys and `LibreWpf_PropertiesPadEditAndSave_ChangesOnlyTheEditedAttribute` still pass. Remaining: an expression
on a non-string property (`Width="{Size * 2}"`) and an event lambda are covered by the same path but not tested.

### G5. XSG tests do not run in CI

`XsgLocalFeedFixture` is not built in CI, so every XSG test skips there (`xsg-hot-reload.md`, "Route D end to end").
The reason is real: adding it to a plan entry's `additionalProjects` ships it in every lane's archive and starts
`XsgProject_CsExpressionEdit_AppliesAsACodeDelta` (a `required` lane on Windows x64 and macOS ARM64) plus SharpDbg
variants that `Assert.True` on the debugger add-in. Needed: a separate observed entry, journey held back, promoted
after two green runs (the rule in `AGENTS.md`). Without it nothing guards Hot Reload, the designer or the LSP for XSG.
Where: `.github/ci/test-plan.json`.

### G6. Evidence is macOS arm64 only

Every XSG run recorded is on macOS arm64. Windows (x64/ARM64) and Linux are untested for Route D, SharpDbg `ApplyChanges`
under the XSG fixture, and the Simpler XAML designer path. Follows from G5.

### G7. No discoverability: template, switch or status

No project template or wizard sets `UseLibreWpfXamlSourceGenerator` (searched `data/templates` and the `.xpt`/`.xft`
files). The only IDE-side reader is `XsgProjectFeatures` (Simpler XAML) and `HotReloadWorkflow`. There is no project
property page entry, no status-bar or Output indication that a project is on XSG, and no action reporting it, which
makes support questions ("is this project BAML or XSG?") answerable only from the csproj. Where: OpenDevelop
templates and project options; a read-only `od.*` DevFlow status action would also help tests.

### G8. Generated-code navigation and `#line`

The generator emits `#line`, so build errors should point at the XAML line. Not found: a test that a generator error
reaches the Error List on the XAML file, nor a test that stepping or a breakpoint in a `{cs:}` expression lands on the
XAML. SharpDbg deliberately refuses the active-frame remap for `#line` code (`HotReloadDeltaProducer.cs`, comment near
line 253), so an edit while stopped inside XSG-generated code finishes the old code. There is also no "view generated
`.g.cs`" action. Where: OpenDevelop, SharpDbg.

### G9. Stepping state during a delta

Open since E3b: a step in progress when a delta lands. Tracked in `sharpdbg-enc.md`; it is the one open item of
question 1 in `xsg-hot-reload.md`.

### G10. IDE cost of whole-compilation regeneration

Upstream notes that generation is not incremental across the compilation, so any C# edit regenerates all XAML, "costly
for the IDE with many XAML files". The host runs the generator lazily when a compilation is first needed, and the
solution-open measurement in `xsg-hot-reload.md` covers loading, not generation. No measurement of typing latency or
Hot Reload delta time in a project with many XAML files was found. Where: measure first (OpenDevelop), then LibreWPF.

### G11. Generator limitations that surface in the IDE

From the LibreWPF usage document, still open as of this audit:

- `x:Uid` localization is unsupported.
- Partial support for merged dictionaries, template inner graphs and complex markup extensions; unsupported constructs
  are warned (`LWXSG0008`) and left out, which means the running app can differ from the designer's picture.
- NativeAOT: `PublishAot` makes `LWXSG0007`/`0008` errors; framework assemblies are not yet AOT-tested.
- Mixed XSG and BAML assemblies are supported but `HotReloadWorkflow` decides per project; a solution with both has no
  test.

### G12. Other designers and runtimes

Only the WPF designer was audited. The MAUI and WinUI designers and the Microsoft WPF runtime have their own XAML
language-server keys; how they treat a `cs:` prefix is not established. `ImplicitNamespacePolicy` is a WPF-profile
concern; nothing says it is rejected cleanly elsewhere.

### G13. Stale upstream documentation

`XamlSourceGeneratorUsage.md` still says Hot Reload is "not yet supported on the source-gen path", that the designer
"expects BAML", and that `#line` is "planned". All three are out of date against this repository. Where: openavalon
(`LibreWPF/Documentation`), through its own publish workflow.

## Alignment with .NET MAUI (web check, 2026-11-25)

Authoritative source: the dotnet/maui repository itself (`main`), read via the GitHub API:
`docs/specs/XamlCSharpExpressions.md` (the spec), `src/Controls/src/SourceGen/CSharpExpressionHelpers.cs` and
`ExpressionAnalyzer.cs`, `src/Controls/src/Xaml/MarkupExpressionParser.cs`, and
`src/Controls/src/Core/AllowImplicitXmlnsDeclarationAttribute.cs`. An earlier pass over blog posts is superseded.

**C# expressions.** MAUI's form is a bare brace value with no prefix: `{Items.Count}`, `{$'{First} {Last}'}`,
`{Price * GetTaxRate()}`, event lambda `{(s, e) => Count++}`. Rules from the spec:

- Resolution order for a simple `{Foo}`: markup extension, then a `Binding` against `x:DataType`, then a member of the
  page, then a static member (global usings respected).
- Disambiguation: `{= Foo}` forces an expression, `{this.Foo}` is the page, `{.Foo}` is the BindingContext,
  `{prefix:Name}` is always a markup extension.
- Strings use single quotes (`'it\'s'`); word operators `AND OR LT GT LTE GTE` avoid XML escaping; CDATA in element
  syntax is the alternative; `prefix:Type.Member` expands an xmlns prefix inside an expression.
- Diagnostics: `MAUIX2007` (identifier matches both a markup extension and a property, extension wins), `MAUIX2008`
  (member on both page and BindingContext), `MAUIX2009` (not found), `MAUIX2010` (two-way becomes one-way),
  `MAUIX2013` (async lambda).
- A simple path (`{Name}`, `{User.Name}`) is a two-way binding; compound expressions are one-way.

So `{cs:}` is **not** MAUI syntax, and the semantics differ: MAUI expressions are mostly bindings with compile-time
checking, while LibreWPF embeds `{cs: expr}` verbatim as C# evaluated once (`xsg-hot-reload.md`, `{cs:}` edits are code
deltas). A MAUI `{Name}` has no `cs:` equivalent, and `{cs: ...}` would be read by MAUI's parser as a markup extension
named `cs:`.

**Implicit namespaces.** The same idea, with one MAUI addition. In MAUI an omitted `xmlns` means the global
namespace `http://schemas.microsoft.com/dotnet/maui/global`, which also exposes types of the current assembly named by
`global using` directives (MSBuild- and SDK-generated ones included; aliases, `global using static` and referenced
assemblies excluded). Implicit xmlns declarations are always enabled; the `AllowImplicitXmlnsDeclaration` attribute is
`[Obsolete]`, its `allow: false` opt-out "is no longer honored", and it goes in .NET 12.

| | MAUI (source) | LibreWPF XSG | Aligned |
|---|---|---|---|
| Omitted `xmlns` | default maui namespace and `x` | presentation and `x` | yes |
| Global usings expose types | yes, via the `.../maui/global` namespace | not checked in the generator or LSP | **unknown, likely gap** |
| Default | always on | opt-in, `LibreWpfSimplerXaml` follows `UseLibreWpfXamlSourceGenerator` | no |
| C# expression syntax | bare `{expr}`, `{= expr}`, `{this.x}`, `{.x}`, lambdas | `{cs: expr}` | **no** |
| Diagnostics | `MAUIX2007`..`2013` | `LWXSG0003`..`0010`, `5001` | n/a |

Decision for LibreWPF, which is the owner's call: keep `cs:` (explicit, no collision with markup extensions, and its
meaning is plain C#), or adopt MAUI's binding-first form. If MAUI's form is adopted, the winappvsce validator and a
shared MAUI/WPF language service must apply MAUI's resolution order, which needs the `x:DataType` type and the page
type; today the validator only tolerates `{cs:}` (G1, G3). Follow-up: the global-using exposure is worth checking
against the generator, since it decides whether Simpler XAML can name project controls without an `xmlns:`.

## Suggested order

1. G5 (CI entry), because G6 and any later fix are unguarded without it.
2. G4 (one experiment), it decides whether the designer needs work or only a test.
3. G2 and G8 together: surface generator diagnostics with correct XAML positions, then pin it with one journey.
4. G1 and G3: the larger language-server feature, using the compilation the host already owns.
5. G7 and G13: small, and they make the rest findable.
6. G10 and G12: measure before committing to work.
