# Live `{x:Bind}` values in the WinUI designer

Companion to `winui-designer-plan.md` (step 8) and `winui-designer-landscape.md`. It answers one
question: **what is the best mechanism for showing real `x:Bind` values in a text-authoritative
designer preview, without rebuilding the project on every edit?**

## Why the obvious answers are not enough

`{x:Bind}` is compiled. The XAML compiler emits a `Bindings` class and an `InitializeComponent`
that creates its bindings and calls `Update()`, so a value exists only when generated code runs.
WinUI provides no runtime evaluator: `Microsoft.UI.Xaml.Markup.XamlReader` cannot honour `{x:Bind}`
(the Surface fork has to *drop* it; see `DesignModeSignal.cs`), and there is no `d:` variant. What
WinUI does provide are two indirect routes:

| Route | Real values | Live on a source edit | Cost |
|---|---|---|---|
| Instantiate the compiled page (`compiledType`) | yes (generated code + code-behind) | no - it is the **last build** | +8.5 s warm open, needs a project build |
| XAML Diagnostics on a running compiled app | yes | non-bound properties only; an `x:Bind` expression is generated code (patcher: `restart required`) | needs the app running |
| Markup preview with `sampleData` | no - values synthesised **from types** (`SamplePlanner`) | yes | none |
| Classic `{Binding}` + design-time data | no `x:Bind` semantics (reflection only) | yes | not `x:Bind` |

Measured on `BoundPage.xaml` (2026-11-13): compiled preview shows `Text = "Bound from code-behind"`
and takes 21.5 s warm; markup preview shows it empty and takes 13 s. Rebuilding the project is what
the compiled route costs per binding edit, which is exactly what the plan's "live means no restart"
rule rejects.

## The mechanism: evaluate the bindings, not the page

Keep the **edited markup as what is rendered** (so every XAML edit is live) and evaluate each
`{x:Bind}` expression against a **compiled page instance kept only as design-time state**:

1. **One design-time instance per document.** The surface constructs the page class from the
   `--user-dll` assembly once (not per render) and never shows it. `DesignModeSignal` is already
   `WINUI_XAML_DESIGN_MODE=1`, so the app's constructor can seed the statics and sample data its
   `x:Bind`s read - the app supplies real design data instead of the tool guessing it.
2. **Resolve each binding expression.** The fork already parses every `{x:Bind}` with Roslyn
   (`SamplePlanner` + `SampleTypeModel`): member chains, `x:DataType` for templates, converters,
   name conventions. Extend that resolver from "produce a value from the type" to "evaluate the
   expression against the instance": reflection for property/field paths, a cached compiled delegate
   for function/static bindings (Roslyn is already a dependency of the payload).
3. **Apply the binding semantics** the XAML compiler would: `Converter`, `ConverterParameter`,
   `ConverterLanguage`, `FallbackValue`, `TargetNullValue`, and conversion to the target property's
   type. `{x:Bind}` has **no `StringFormat`** (the XAML compiler rejects it with WMC0011, measured on
   the fixture; a format comes from a converter or a function), so there is nothing to apply.
   `Mode`/`UpdateSourceTrigger` matter only for write-back, which a preview does not need (values
   shown are design-time state).
4. **Rewrite the preview, never the source.** Replace each `{x:Bind ...}` in the XAML text sent to
   the renderer with the computed value (or, for templates, keep per-row `{Binding}` objects as the
   planner already does). Selection, hit-testing, the element tree and edits that write the source
   are unaffected.
5. **Re-evaluate on each render** and cache compiled expressions by text hash. A new project build
   (new code-behind or types) invalidates the instance and the cache.

The result: a binding edit - a new path, a converter, a function call, a different `x:DataType` -
re-renders at markup cost with the app's real design-time values; a C# change still needs a build,
because the instance and its types come from the compiled assembly. That is a narrow, explainable
last-build edge instead of "the whole XAML is last-build".

## Where the work lives

The evaluator must run where WinUI and the user assembly are loaded - the **Surface host**
(`Surface.exe`), not the IDE and not the DDP child, neither of which loads WinUI. So:

- fork (`lextudio/WinAppVSCE`, package `LeXtudio.WinUISurface` 0.5.0): `surface/Surface/BindingEvaluator.cs`
  beside `SamplePlanner`, a design-time instance cache in `RenderHost`, and two render fields
  (`evaluateBindings`, `designType`) alongside `sampleData`/`compiledType`; unit checks live in
  `surface/Surface.Tests` and the fork's release workflow builds, smoke-tests (x64 and ARM64) and publishes it;
- this repo: sends the fields, derives `designType` from `x:Class`, and defaults the mode on
  (`OD_WINUI_LIVE_BINDINGS=0` opts out); a toolbar choice is still to come;
- no renderer change for the markup path itself: it is the same `XamlCleaner` substitution point
  the sample-data mode uses today.

## Limits to state honestly

- Values are **read-only design-time state**: two-way/`UpdateSource` effects are not simulated.
- Code-behind/type changes need a project build; XAML-only changes do not.
- A binding whose expression calls into app singletons may fail under design mode; the design-mode
  signal is the app's hook to seed them, and a failed binding falls back to the type-based sample
  value and is reported, never thrown into the render.
- Templates without design-time items keep the generated sample rows; they are not real app data.
- The evaluator executes app code in the preview process (as compiled preview already does), so the
  design-mode branch is the app's contract for doing so safely.

## Alternatives, and why not first

| Alternative | Why not the first step |
|---|---|
| Incremental per-page design-time build + collectible hot swap | Highest fidelity (real XBF/generated code), but XBF/resource identity, `Application.LoadComponent`, and the metadata provider make it a design-time app build in disguise; latency in seconds and a large surface for failure |
| Self-applied EnC deltas into the compiled preview | Fits method-body binding changes, but adding/removing a binding is a rude edit, and binding code, symbol matching and an EnC host are a much larger dependency than an evaluator |
| `{Binding}` + design-time data as the normal mode | It is not `x:Bind`: no function bindings, no compiled member access, different null/fallback rules |
| Keep the compiled preview only | Correct values, but every binding edit is a rebuild + host restart - the opposite of a live designer |

## Relationship to the landscape and the plan

- `winui-designer-landscape.md` §7.1 already specified this compiled-page route and measured it (fork `e397491`,
  `compiledType`, the `0x8001010E` bootstrap guard, `BOUND VALUE 42`/`7`, last build and a rebuild+restart trigger).
  §7.8 records that the upstream surface never runs generated binding code, and the appendix "Compiled-page route
  dropped (2026-11)" records the earlier decision to drop it because a compiled page is not a live preview.
- This note therefore does not reopen that decision: the compiled page stays the **reference** mode, re-added once the
  C++ toolchain existed, and the mechanism here is the **live** tier that the landscape calls a hot-reload problem.
  `od.winui-designer.rebuild-compiled` is not restored; the default preview is not a build.
- The landscape's §7.1 "compiled preview application" (real XAML compiler/PRI, `built|cached|degraded|failed`, a rebuild
  policy) remains the fidelity direction; the evaluator is what makes the default preview show real values *between*
  builds. If the evaluator hits an expression it cannot honour, falling back to the compiled reference is the honest
  answer rather than a placeholder.

## Result (2026-11-14)

The mechanism was built end to end, measured on `BoundPage.xaml`, and shipped: the evaluator lives in the fork
(`surface/Surface/BindingEvaluator.cs`, package 0.5.0, tag `surface-v0.5.0`; the fork's release workflow built both
architectures, smoke-tested the x64 surface and the packaged ARM64 surface, and published it). This repo sends
`evaluateBindings`/`designType` on every render request and **defaults the mode on**; `OD_WINUI_LIVE_BINDINGS=0` opts out.

What the evaluator does:

- parses the markup being rendered and, for each `{x:Bind ...}` on a text-valued attribute, evaluates a
  **member path** by reflection against one page instance (`Activator.CreateInstance(userDll.GetType(x:Class))`),
  cached per type;
- evaluates **function and static bindings** by compiling the expression with Roslyn against the loaded project
  assembly (`Shout(Title)` becomes `__page.Shout(__page.Title)`; `local:Helper.Fmt(Title)` maps the XAML prefix to
  the CLR namespace the document declares), with the compiled invoker cached per rewritten expression;
- resolves a **converter** from the document's own resource declaration (`<local:SuffixConverter x:Key="Suffix"/>`),
  so a XAML-only converter edit is live too; falls back to the design instance's resources, then skips;
- applies `ConverterParameter`/`ConverterLanguage` and `FallbackValue`/`TargetNullValue`, and leaves attached-property
  targets and template content to `XamlCleaner`'s existing placeholder/sample behaviour;
- logs `LIVEBIND: evaluated N, skipped M` per render (plus the instance type and any resolution failure);
- is mutually exclusive with `compiledType`.

Measured (WinUISample fixture, warm provisioner cache, x64):

| Check | Result |
|---|---|
| markup mode, no flag (yesterday's baseline) | `BoundText.Text` empty |
| markup mode + evaluator | `Text = "Bound from code-behind"`; `LiteralText` still `"literal from the last build"` |
| converter declared in the document | `ConvertedText` = `"Bound from code-behind (design)"` (the converter instance is created from the markup) |
| function binding | `FunctionText` = `"BOUND FROM CODE-BEHIND!"`; the first compile takes about 3 s, the cached invoker about 0.2 s |
| **cross-check, compiled reference** | the same document rendered with `OD_WINUI_COMPILED=1` reports all four values identically |
| cost of evaluation | the substitution takes about 1 ms of a render (`Apply` 00:32:30.107 -> .109) |
| warm open, surface host | about 10 s (provisioner cache hit), against 21.5 s measured for the compiled reference |
| live binding edit, no build | `Text` edited through the Properties pad to `{x:Bind BoundRoot.Name}` -> preview reports `BoundRoot` in 2.4 s, including the pad edit |
| live function/converter edit, no build | `{x:Bind Shout(Shout(Title))}` -> `"BOUND FROM CODE-BEHIND!!"` in 2.6 s; `ConverterParameter=live` -> `"Bound from code-behind (live)"` in 2.5 s |
| undo | restores the source attribute and the preview reports the original values again |

So the two claims that mattered are true: an `{x:Bind}` in the *markup* preview now shows the real design-time value,
and a binding edit - a path, a function call, a converter - re-renders at markup speed without a project build or
renderer restart. The remaining work is `x:DataType` roots, templates, non-text targets with type conversion and
`BindBack`/two-way, plus a toolbar switch for the mode.

Three findings worth recording:

- the shell that renders is the **DDP child**, not the IDE process, so the `SurfaceDesignDocument`/`SurfaceConnection`
  changes must reach `AddIns/.../WinUIXamlDesigner/SurfaceHost/` as well as the AddIn folder; a stale copy there made
  the feature look inert (the surface logged `evaluate=False`) even though the IDE's copy was current;
- the design-time instance is created successfully for pages that have code-behind only because the project runs
  `WINUI_XAML_DESIGN_MODE=1` and its bootstrap initializers are off - the same precondition the compiled reference
  already has;
- `{x:Bind}` has **no `StringFormat`**: `Text="{x:Bind Title, StringFormat='[{0}]'}"` is a build error (WMC0011, "Unknown
  member 'StringFormat' on element 'BindExtension'"). A format has to come from a converter or a function, so the
  evaluator does not pretend to support one.

The published-package check (payload rebuilt from `LeXtudio.WinUISurface` 0.5.0, provisioner cache cleared, no
environment variable set) reports the same four values, which is what "default" means here.

## Done when

1. `BoundPage.xaml` shows `Text = "Bound from code-behind"` from markup mode, with no
   `OD_WINUI_COMPILED`. **Met and shipped** in surface 0.5.0, on by default (`OD_WINUI_LIVE_BINDINGS=0` opts out).
2. Changing the binding in the source (`{x:Bind Title}` to `{x:Bind Subtitle}`, or adding a
   converter) updates the preview on save, without a project build or renderer restart. **Met**: member paths,
   a converter declared in the document, and function bindings all re-render live; the values match the compiled
   reference on the same document.
3. A literal edit stays live (already true) and a code-behind edit still requires a build, with the
   status/report naming that reason. Literal edits are live; the "requires a build" report is not built yet.
4. Warm open/render stays close to the markup-path cost (13 s measured; the compiled path is 21.5 s).
   Measured at about 10 s warm with the evaluator.
5. `od.winui-designer.runtime-properties` reports the evaluated value, so the check reads the
   renderer's own state rather than a screenshot. Met.
