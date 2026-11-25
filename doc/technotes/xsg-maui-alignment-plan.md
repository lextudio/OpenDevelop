# Plan: align XSG C# expressions and Simpler XAML with .NET MAUI

Status: plan, 2026-11-25. Decision (owner): drop LibreWPF-only syntax such as `{cs: ...}` and follow MAUI. Language
service and every other module follow the generator. Iterate on a **local NuGet feed**; commit and push only after
OpenAvalon and OpenDevelop are both stable. Reference: `xsg-gaps.md`, "Alignment with .NET MAUI", which cites MAUI's own
spec (`dotnet/maui` `docs/specs/XamlCSharpExpressions.md`) and sources.

## Ground rules

- No commit, no push in any repository until the owner says so. A push to `openavalon` starts its 1.5-2 hour publish
  workflow, so it is the last step, not a way to test.
- Local iteration follows `xsg-hot-reload.md`, "Local iteration": pack the changed package with a **new unique version**
  every time into `OpenDevelop/artifacts/openavalon-feed`, move the pin locally, write `<package>.nupkg.verified` beside it
  so `sync-openavalon-feed.ps1` skips it. NuGet caches a version forever, so never reuse one.
- `{cs:}` is removed, not kept as an alias. Existing fixtures, tests and notes move to the MAUI form in the same change.

## What changes, by component

| Component | Repository | Change |
|---|---|---|
| Generator: parse and resolve expressions | openavalon `LibreWPF.Xaml.SourceGenerator` | replace the `{cs:}` branch (`ConvertAttributeValue`, `ReportCSharpExpressions`) with MAUI's rules |
| Generator: diagnostics | same | replace `LWXSG0004` with the expression diagnostics below |
| Generator: Simpler XAML | same | add global-using exposure (MAUI's `.../maui/global`-style namespace) |
| Switches | `LibreWPF.Sdk` | retire `LibreWpfCSharpExpressions`; decide whether `LibreWpfSimplerXaml` stays |
| Language server | winappvsce | validator knows expression values; later completion, hover, diagnostics |
| Hot Reload classification | OpenDevelop `HotReloadWorkflow` | recognise an expression value instead of `StartsWith("{cs:")` |
| Designer | OpenDevelop, WpfDesigner | tolerate the new value form (G4 experiment first) |
| Fixture and tests | all three | `XsgLocalFeedFixture`, generator tests, Base tests, winappvsce tests |
| Docs | all three | `XamlSourceGeneratorUsage.md`, `xsg-hot-reload.md`, `xsg-gaps.md` |

## Stage 1: syntax, parsing, event lambdas (no binding resolution yet)

Goal: a document written for MAUI's syntax compiles under XSG, using plain C# semantics for the part MAUI would resolve
as a binding.

1. A `{...}` attribute value is an expression when it is not a markup extension. Markup extension wins on a bare name
   match (MAUI `MAUIX2007`, a warning); `{= expr}` forces an expression; `{prefix:Name}` is always a markup extension.
2. Single-quoted strings become double-quoted C#; `\'` escapes; char literals kept when the target parameter is `char`.
3. Operator aliases `AND OR LT GT LTE GTE` (case-insensitive, need surrounding spaces) expand before parsing.
4. `$'...'` interpolation, ternaries inside holes are parenthesised as in the spec.
5. `{this.Foo}` is a member of the root `x:Class`; `{.Foo}` is a DataContext member (Stage 2; Stage 1 reports it).
6. Event attributes accept a lambda `{(s, e) => ...}`; the generator emits a method of the event's delegate signature and
   wires it (`-=` before `+=`, so `InitializeComponent` stays idempotent for Hot Reload). Async lambdas are an error
   (MAUI `MAUIX2013`).
7. Types in expressions resolve by C# rules (global usings) plus `prefix:Type.Member` expansion through the document's
   xmlns map.
8. `#line` stays on every generated statement so errors and the debugger point at the XAML line.

Done when: generator unit tests cover each rule above and the old `{cs:}` tests are rewritten, not deleted; the
`XsgLocalFeedFixture` runs on the new syntax and the existing Base end-to-end Hot Reload tests still pass.

## Stage 2: binding-first resolution (MAUI semantics)

For a simple path the generator resolves, in MAUI's order: markup extension, then a `Binding` against `x:DataType`, then a
member of the root class, then a static member. It reuses the compiled-binding code that already exists
(`XamlSourceGenerator.CompiledBindings.cs`). Needed on top:

- a `Binding` target must be a dependency property in WPF (MAUI says "bindable"); otherwise fall through to local access.
- two-way for a simple path on a two-way property; complex expressions are one-way (`MAUIX2010`-style info).
- conflict and not-found diagnostics (`MAUIX2008`, `MAUIX2009` equivalents) and a decision on diagnostic ids: reuse MAUI's
  `MAUIX*` numbers or keep the `LWXSG*` family. Recommendation: keep `LWXSG*`, because the ids are product-specific and
  a shared id would send a user to MAUI's documentation for a WPF project.
- `{.Foo}` and `{this.Foo}` disambiguation.

Done when: a MAUI spec example, with the `ContentPage` swapped for a `Window`, produces the same binding/local/static
decisions as the spec table, verified by generator tests that compile the output.

## Stage 3: Simpler XAML parity

- Expose types of the current assembly named by `global using` directives (SDK- and MSBuild-generated ones included;
  aliases, `global using static` and referenced assemblies excluded, as in MAUI) under an implicit global namespace.
  First check whether the generator already does something similar; the audit did not.
- Decide the default. MAUI is always on. LibreWPF is opt-in because the XSG itself is opt-in; revisit when XSG becomes
  the default, not before.

## Follow-the-generator work (starts when Stage 1's syntax is fixed)

- **winappvsce validator**: stop special-casing `{cs:}`; an attribute value that is an expression raises no markup
  extension diagnostic. Update `CSharpExpressionValueTests`. Stage 2 adds the `x:DataType` type and root class so the
  server can resolve `{Foo}` the same way.
- **Language service features** (G1, G2, G3): completion and hover inside an expression, generator diagnostics as
  editor squiggles with XAML positions. Built on the Roslyn host's compilation, which already runs the generator.
- **HotReloadWorkflow.Classify**: today `value.StartsWith("{cs:")` marks generated code. With the MAUI form a value
  `{Foo}` could be an expression or a markup extension, so classification should ask whether the value is an expression
  by the same rule as the generator (a shared helper is the safest route; the alternative is the over-recognising
  heuristic, which only costs a rebuild). Event lambdas also become a code delta.
- **Designer**: run the G4 experiment on the new syntax; expect the value to be ignored or shown as text.

## Order and local loop

1. Stage 1 in `openavalon/LibreWPF` with unit tests (`LibreWPF.Xaml.SourceGenerator.Tests`).
2. Pack `LibreWPF.Xaml.SourceGenerator` locally, unique version, point `XsgLocalFeedFixture` and the OpenDevelop pin at it.
3. Move OpenDevelop and winappvsce tests to the new syntax; run `XsgHotReloadEndToEndTests` one method at a time and the
   XSG designer journey.
4. Stage 2, repeat 2-3.
5. Stage 3, repeat 2-3.
6. Stop and report. Commit and push only when the owner says OpenAvalon and OpenDevelop are stable.

## Risks

- **Silent behaviour change.** MAUI's `{Foo}` can bind, where `{cs: Foo}` evaluated once. Stage 1 keeps plain C#
  evaluation for what MAUI would bind, so Stage 1 and Stage 2 differ on the same text. Stage 1 output must therefore be
  labelled preview in the usage doc.
- **Markup extension collisions.** `{Binding}`, `{StaticResource}` and WPF's own extensions are names a user may also
  have as a property. MAUI's rule (extension wins, warn) is adopted unchanged.
- **MAUI's syntax is still moving** (preview). Keep the expression parser behind one internal type so a later change is
  local; do not scatter the syntax rules across the generator.
- **Three repositories at three versions.** Moving the pin and the packed version together is manual; a stale pin gives
  "the test still runs the old generator". Check the generated `*.g.cs` before assuming a logic bug.

## Progress (2026-11-25, local, nothing committed)

Stage 1 is done in code:

- openavalon `LibreWPF.Xaml.SourceGenerator`: `XamlExpressions.cs` holds the syntax; `{cs:}` is gone; event lambdas hold their
  delegate in a field so `InitializeComponent` stays idempotent when the event is known to the compilation; new
  `LWXSG0011` (async lambda) and `LWXSG0012` (`{.Foo}`, until Stage 2). 186 generator tests pass, including the new
  `XamlExpressionTests`.
- Packed as `LibreWPF.Xaml.SourceGenerator 0.1.0-local.3` into `OpenDevelop/artifacts/openavalon-feed`. The global pin
  (`ProGpuPackageVersion`) is unchanged; only `tests/fixtures/XsgLocalFeedFixture` references `0.1.0-local.3` directly, so
  **move it back to `$(ProGpuPackageVersion)` when a real publish exists**. Repack with a new version for every generator
  change.
- OpenDevelop: `XamlExpressionSyntax` (a copy of the generator's rule, with parity tests) feeds `HotReloadWorkflow`; the
  fixture XAML and the Hot Reload tests use the new syntax. Run one at a time against the real LibreWPF fixture:
  `CsExpressionEdit_ReachesTheLiveWindowThroughACodeDelta`, `EventHandlerEdit_ReachesTheLiveWindowThroughACodeDelta` and
  `NameRemoval_IsARestartAndLeavesTheSessionUsable` pass.
- winappvsce: `CSharpExpressionValueTests` use the MAUI forms and pass; the validator needed no code change.

Not run: the SharpDbg variants, `XsgProject_CsExpressionEdit_AppliesAsACodeDelta` (IDE journey), the designer journeys on the
new fixture XAML (G4 is still unmeasured), and the rest of the Base suite.

## Progress, editing experience and Stage 2 (2026-11-25, local, nothing committed)

Stage 2 (generator, `0.1.0-local.4`): a simple path `{Name}` / `{User.Name}` becomes a compiled `Binding` against `x:DataType` when the
target is a dependency property and the DataContext type has the first name; `{.Name}` is that binding explicitly (reflective without an
`x:DataType`); `{this.Name}` is the page; a name on both the page and the DataContext is `LWXSG0012`; anything else is plain C#. `LWXSG0012`
now means that conflict (it was "`{.Foo}` not supported"). 195 generator tests pass (`ExpressionBindingTests`).

Editing experience, all in the language server (winappvsce) and proved through the real IDE by
`XsgProject_CsExpressionValue_HasColourCompletionAndDefinitionInTheEditor`:

| Feature | Where | Evidence |
|---|---|---|
| Colour (variables, properties, methods, strings, numbers, operators, the operator words, `$'...'` holes) | `XamlExpressionLexer`, `XamlSemanticTokens` (legend extended by six types) | `ExpressionSemanticTokensTests`, IDE journey |
| Completion (DataContext members first, then the page, keywords, dotted paths, `this.`, a leading `.`, interpolation holes; nothing inside strings) | `CompletionProvider.Expressions.cs` | `ExpressionCompletionTests` (12), IDE journey |
| Hover and go to definition on a name | `ResolveExpressionMemberAt`, wired into `ResolveNamedSymbolAsync` | `ExpressionCompletionTests` (10), IDE journey (definition) |
| Errors while typing, with the build's ids (`LWXSG0011` async lambda, `LWXSG0012` name on page and DataContext) | `XamlValidator.Expressions.cs` | `ExpressionDiagnosticsTests` (10), IDE journey (`LWXSG0011`) |
| Not mistaken for a markup extension | `XamlParser` (`csharpExpressions`), `ImplicitNamespacePolicy.ExpressionsEnabled` (project has the generator on and `LibreWpfCSharpExpressions` not false) | `ExpressionSemanticTokensTests` |
| Designer: expression text kept, shown read-only in the Properties pad | `XamlExpressionSyntax` in WpfDesigner's XamlDom | `XsgProject_CsExpressionValue_IsShownReadOnlyInTheDesigner` |

The rule "is this value an expression" now exists in five places that must change together: generator, OpenDevelop Base
(`XamlExpressionSyntax`), WpfDesigner XamlDom, winappvsce `WinUiXaml.Xaml`, and the LSP lexer's assumptions.

Not done or not verified: hover through the real IDE (no DevFlow action returns a hover; unit-tested only), completion of static types
(`Math.`, `DateTime.`; implemented, untested), types named by `global using` (Stage 3), the Properties pad stays read-only for expressions, only the
`librewpf` profile reads expressions (MAUI and the other XAML dialects do not), the winappvsce full suite still has the 39 failures it has
at its own HEAD (`AllowedRootBoundaryTests`, `ServerShutdownTests`, `XamlDocumentLinksTests`, `XamlResourceGraphTests`; checked in a clean worktree).

## Progress, Stage 3 and the remaining checks (2026-11-25, local, nothing committed)

Stage 3 (generator `0.1.0-local.5`, and the language server): a type of a namespace named by the project's own `global using` directives
(no alias, no `static`; the SDK's implicit usings too, since MSBuild writes them as a source file; output of other generators is not in the
compilation yet) is found in the presentation namespace without an `xmlns`. The generator resolves it after the presentation namespaces
(`GlobalUsingTypeTests`, 4); the language server resolves it for the LibreWPF profile only (`XamlTypeSystem.GlobalUsingTypesEnabled`),
so the validator reports no unknown type and element-name completion offers it (`GlobalUsingTypeTests`, 9). Like the generator it is
also applied when a default `xmlns` is written out, which MAUI leaves unchanged; the presentation namespace wins any clash.

Also checked now: hover through the real IDE (`od.quick-info`, part of the editing journey), static-type completion
(`{Helper.|}`, `Math.`: types are looked up in the page's namespaces, the global usings, `System`, then the XAML namespaces),
the editing journey with hover and `LWXSG0011` on `0.1.0-local.5`, the IDE hot reload journey, the designer journey, and the Base
hot reload end-to-end test.

**The WPF designer knows global-using types too (done).** The protocol change turned out to be additive, like `ImplicitXamlNamespaces`
before it (a child that does not know the field reads an empty list), so no version bump and no change for the external add-ins.
`DesignerDocumentSnapshot.GlobalUsingNamespaces` is filled by `XsgProjectFeatures.GlobalUsingNamespaces` (the SDK's `Using` items and the
`global using` lines of the project's C# files, cached by file time) when Simpler XAML is on; the host adds them as lookup-only
namespaces of the default namespace (`XamlTypeFinder.AddLookupNamespace`, project assembly and references), so the designer never writes
them back. Journey: `XsgProject_GlobalUsingType_LoadsInTheDesignerWithoutAnXmlns` (a built copy of the fixture whose window holds
`<Fancy/>`; the element is in the design tree and the file is unchanged).

**One rule, four copies, now checked.** `ExpressionRuleConsistencyTests` runs 49 values through OpenDevelop Base, WpfDesigner's XamlDom,
winappvsce's `WinUiXaml.Xaml` and the generator (loaded from their build output) and fails on any disagreement or any copy not built.
The editor's `LWXSG0012` uses the generator's own test for a bindable target (a static `<Name>Property` field), not an approximation of it.
The Properties pad keeps expressions read-only on purpose: a text field would invite typing over code that the designer cannot evaluate.

## Progress, compound expressions (2026-11-25, local, nothing committed; generator `0.1.0-local.9`)

Stage 2 covered a simple path only. MAUI also reads every name of a compound expression (`{Age + 1}`, `{$'{Label}: {Age}'}`,
`{Name.ToUpper()}`) through the same rules, and before this the build failed with CS0103 on such a name while the editor accepted it. Now every name
that the DataContext type (`x:DataType`) has is read through its own compiled `Binding`; the expression is evaluated by a method of the page
(`__XsgExpression_N`) behind a generated `IMultiValueConverter` (`__XsgExpressionConverter`) in a `MultiBinding`, one-way. Page names and statics
stay C# inside that method; `{.Name}` is explicit; a name on both the page and the DataContext is `LWXSG0012`. Not bound: an expression with a
lambda, a CLR (non-dependency) target property, an expression with no DataContext name (plain C# as before). Unit tests:
`ExpressionBindingTests` (208 generator tests in all).

Two defects only the real LibreWPF showed, both fixed and pinned by `XsgHotReloadEndToEndTests.ExpressionOverTheDataContext_IsComputedByTheLiveWindow`
(the live window computes `Computed.Text == "42"` and `Described.Text == "Ada: 41"`, with a plain `{Binding}` as the control):

- A `MultiBinding` does not convert its converter's result to the property's type (a `Binding` does), so an `int` for `Text` was rejected and the
  label stayed empty. The converter now converts to the target type.
- The evaluation as a lambda in `InitializeComponent` broke Hot Reload: after a delta the old element tree still calls its converter while it is
  replaced, and EnC had marked the lambda deleted (`HotReloadException: Attempted to invoke a deleted lambda`; the agent died, three end-to-end
  tests and the IDE journey failed). A method of the class is updated in place, so the evaluation is a method now. The compiled-binding
  descriptors' `static o => o.X` lambdas have the same shape but survived the same test (their delegates are not called again on that path); keep it
  in mind if a delta ever reports a deleted lambda there.

Also fixed: the `LWXSG0012` message printed the whole expression where it meant the name.

Re-run after both fixes, all passing: the Base end-to-end tests (including the two SharpDbg variants), the IDE hot reload journey, the editing
journey, the designer journeys and the global-using journey. The fixture window gained a `Probe` DataContext and three bound labels
(`Classic`, `Computed`, `Described`).

The same end-to-end test also runs, in the live window, the other expression forms: a simple path `{Label}` and the explicit `{.Label}` (both bindings,
`"Ada"`) and an event lambda on `Loaded` (`{(s, e) => LambdaRan!.Text = 'lambda ran'}`, which fires and writes). The fixture window now holds
`Classic`, `Simple`, `Explicit`, `LambdaRan`, `Computed` and `Described`. Re-run after adding them: the Base end-to-end tests (one SharpDbg variant
included), the IDE hot reload journey and two designer journeys, all passing.

## Progress, the Properties pad edits expressions (2026-11-25, local, nothing committed)

The pad no longer holds an expression read-only. Its row shows the authored text (`Kind` String, not read-only) and a value typed into it that is an
expression is written as the attribute text exactly as typed: `design/set-property` wraps it in `XamlExpressionSyntax.Text`, which
`XamlDocument.CreatePropertyValue` turns into a raw attribute value (a plain string with a leading brace would have been escaped as `{}` literal text).
A literal typed over an expression replaces it. The earlier reason for read-only (the escaping) is gone, so the statements above that say the pad stays
read-only are superseded. Journey: `XsgProject_CsExpressionValue_CanBeEditedInTheDesignerAndIsSavedAsTyped` (a copy of the fixture; typed
expression, literal over expression, expression over literal; the saved file differs from the original by exactly the edited attribute). The undo
stack goes through the same `PropertyChangeAction` as any property edit; it was not exercised separately.

Two checks added afterwards, both passing: undo and redo of those pad edits bring the expression text back exactly as written (in the same
journey), and the editing journey now covers the DataContext path through the real IDE and a real project: with `x:DataType="local:Customer"`,
`{Nick|}` completes `Nickname`, and hover and go to definition on `{Visits + 1}` land on `Customer.Visits` in `Customer.cs`. The statement above
that undo "was not exercised separately" is superseded.

Hot Reload of a compound expression is covered too: `CompoundExpressionEdit_ReachesTheLiveWindowThroughACodeDelta` edits `{Age + 1}` to `{Age + 2}`
(the live label shows `43`), then to `{$'{Label} is {Age + 1}'}` (two DataContext names instead of one; `Ada is 42`), then reverts (`42`), each delta
building on the generation the application runs. It also passes under SharpDbg (`CompoundExpressionEdit_UnderSharpDbg_ReachesTheLiveWindow`).
