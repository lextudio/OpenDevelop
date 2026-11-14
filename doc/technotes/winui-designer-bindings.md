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
   `ConverterLanguage`, `StringFormat`, `FallbackValue`, `TargetNullValue`, and conversion to the
   target property's type. `Mode`/`UpdateSourceTrigger` matter only for write-back, which a preview
   does not need (values shown are design-time state).
4. **Rewrite the preview, never the source.** Replace each `{x:Bind ...}` in the XAML text sent to
   the renderer with the computed value (or, for templates, keep per-row `{Binding}` objects as the
   planner already does). Selection, hit-testing, the element tree and edits that write the source
   are unaffected.
5. **Re-evaluate on each render** and cache compiled expressions by text hash. A new project build
   (new code-behind or types) invalidates the instance and the cache.

The result: a binding edit - a new path, a converter, a string format, a different `x:DataType` -
re-renders at markup cost with the app's real design-time values; a C# change still needs a build,
because the instance and its types come from the compiled assembly. That is a narrow, explainable
last-build edge instead of "the whole XAML is last-build".

## Where the work lives

The evaluator must run where WinUI and the user assembly are loaded - the **Surface host**
(`Surface.exe`), not the IDE and not the DDP child, neither of which loads WinUI. So:

- fork (`lextudio/WinAppVSCE`, package `LeXtudio.WinUISurface`): a `BindingEvaluator` beside
  `SamplePlanner`, a design-time instance cache, and one new render option (for example
  `evaluateBindings: true`, alongside `sampleData`/`compiledType`);
- this repo: send the option and expose the mode (a DevFlow action and, later, a toolbar choice),
  and add a `{x:Bind}` probe page (BoundPage already is one);
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

## Done when

1. `BoundPage.xaml` shows `Text = "Bound from code-behind"` from markup mode, with no
   `OD_WINUI_COMPILED`.
2. Changing the binding in the source (`{x:Bind Title}` to `{x:Bind Subtitle}`, or adding a
   converter) updates the preview on save, without a project build or renderer restart.
3. A literal edit stays live (already true) and a code-behind edit still requires a build, with the
   status/report naming that reason.
4. Warm open/render stays close to the markup-path cost (13 s measured; the compiled path is 21.5 s).
5. `od.winui-designer.runtime-properties` reports the evaluated value, so the check reads the
   renderer's own state rather than a screenshot.
