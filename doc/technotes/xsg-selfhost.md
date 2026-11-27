# Building and running OpenDevelop itself with the LibreWPF XAML Source Generator (XSG)

Status: experiment, 2026-11-26, macOS arm64, LibreWPF.Sdk / generator `0.1.0-preview.69.65.1` plus the generator fixes committed in
`../openavalon/LibreWPF` (`c3c74ac07`, `1a182ee1b`; see below), .NET SDK `/usr/local/share/dotnet`.

**Result: with those generator fixes the IDE core builds with XSG and runs.** Started with `OD_TEST_MODE=1`: no FATAL/ERROR in the
log, the DevFlow agent answers, `od.open-solution` (`tests/fixtures/ProfilerFixture`), `od.open-file` for a `.cs` and a `.xaml`
(`AvalonEditViewContent`) and `od.new-solution-dialog` work, the UI tree has ~1850 nodes. Only the test-fixture projects fail to build
(below), and two icon files log WARNs. Not checked: the pads and dialogs one by one, the designers, debugging, Hot Reload.

## How to build the IDE with XSG (nothing of this is in the repo)

`LIBREWPF_USE_XSG=1` (or `-p:UseLibreWpfXamlSourceGenerator=true`) used to skip BAML only and not reference the generator, so an IDE project
compiled no XAML at all (first symptom: `ColorPicker.xaml.cs CS0115`). **The SDK now adds the reference itself**
(`ProGPU.Wpf.Sdk.XamlSourceGeneratorFeatures.targets` in `../openavalon`, uncommitted; test: `packaging/ProGPU.Wpf.Sdk/tests/test-xsg-autoreference-target.sh`):
with the switch on, a `.csproj` or `.vbproj` gets `PackageReference LibreWPF.Xaml.SourceGenerator` at `$(ProGpuPackageVersion)` (`VersionOverride` under
central package management); `LibreWpfXsgAutoReference=false` or a reference of the project's own keeps the project's choice. F# gets nothing there
(no Roslyn generator in the F# compiler; a separate package and MSBuild task, see the F# plan). With it, building the IDE needs only
`LIBREWPF_USE_XSG=1`. The same targets file also fixes the **Aero ambiguity**: `PresentationFramework.Aero`, `Aero2`, `AeroLite`, `Luna`, `Royale`
and `Classic` declare the same `Microsoft.Windows.Themes` types (BAML picks one by the `assembly=` of the xmlns, generated C# cannot, CS0433), so
under the generator `_LibreWpfXsgDropDuplicateThemeReferences` leaves only the Aero reference in the compile references (still copied and loaded at
run time; `LibreWpfXsgKeepThemeReferences=true` keeps them all). `AvalonEdit.AddIn` and `Debugger.AddIn`, the projects that use those types, build
with it and nothing else.

(the Uno fixture `UnoHotReloadFixture` must also stay out of the switch.)

Build: `LIBREWPF_USE_XSG=1 dotnet build tests/OpenDevelop.IntegrationTests/OpenDevelop.IntegrationTests.csproj -c Debug
-p:CustomBeforeMicrosoftCommonTargets=<the file above> -p:RestoreIgnoreFailedSources=true -p:EmitCompilerGeneratedFiles=true`
(`RestoreIgnoreFailedSources`: a changed package graph makes NuGet re-query the `lextudio_github` source for `ProGPU.*` and fail with
401 although the local feed has every package.) Generated files appear under `obj/Debug/**/generated/LibreWPF.Xaml.SourceGenerator/`.
The submodules (`externals/*`, and `externals/cps/externals/project-system`) must be initialised in a fresh worktree or the shell does
not compile for reasons unrelated to XSG.

Pitfall while iterating on the generator: replacing the analyzer dll in `artifacts/nuget-packages/librewpf.xaml.sourcegenerator/.../analyzers/`
does **not** make MSBuild recompile a project (`dotnet build --no-incremental` only rebuilds the entry project). Delete the
`obj/Debug/**/*.CoreCompileInputs.cache` files (and stale `generated/` folders) first.

Mixed mode (some projects XSG, some BAML) is not reliable with the environment variable: the BAML two-pass `*_wpftmp.csproj` re-evaluates
without the project's own `UseLibreWpfXamlSourceGenerator=false` and fails with `MSB4057 _CompileTemporaryAssembly`. Not needed any more:
VB projects now use XSG too (next section). The package must carry the generator under `analyzers/dotnet/vb` as well (done in the generator's
csproj; the experiment copied the dll there by hand). After `dotnet build-server shutdown`-free iterations a stale compiler server can keep an
old analyzer loaded; shut it down when a generator change seems to have no effect.

## Visual Basic

`[Generator(LanguageNames.CSharp, LanguageNames.VisualBasic)]`: for a VB compilation the generator builds the same C# object graph, parses it
back with Roslyn and prints it as Visual Basic (`XamlSourceGenerator.VisualBasic.cs`, `VbPrinter`), file `*.g.vb`. Handled: field
declarations, `InitializeComponent`, object creation with initializers, casts, `typeof`, attached-property calls, event wiring
(`AddHandler`/`RemoveHandler ... AddressOf`), the deferred name references, `x:Name` registration through `RegisterName`, a canned VB copy
of the markup-extension helper; `Namespace Global.<real namespace>` (x:Class in VB is relative to the root namespace: the class is found in
the compilation and its namespace used); the base class from the root element when the VB part has no `Inherits`. Not available in VB
(reported as `LWXSG1001`, or the feature is switched off for VB): C# expressions (`{= ...}`, `{cs:}`), compiled bindings, hot reload,
the module initializer that registers a component by resource path (loading a page by URI), the `#line` mapping. `VBBinding` (3 XAML files)
builds and the IDE starts with them. Tests: `VisualBasicTranslationTests.cs` (printer, and a generator run on a VB compilation).

## F# (2026-11-26)

F# has no source generators and no partial classes, so it cannot work like C# and VB. Instead (all in `../openavalon/LibreWPF`, commit `1a182ee1b`):
- `src/LibreWPF.Xaml.FSharp.Tool` (C# console): runs the same `XamlSourceGenerator` over a **C# compilation made only of the project's reference
  assemblies** (no project source needed, so the F# project's own types are not required: the x:Class type is generated), switched by
  `build_property.LibreWpfXsgOutputLanguage=FSharp` (C# expressions and compiled bindings off, no pattern-matching output), and prints the
  generated C# as F# (`FSharpPrinter`, the same idea as the VB printer) to `obj/xsg/<class>.g.fs`.
- `packaging/ProGPU.Wpf.Sdk/targets/ProGPU.Wpf.Sdk.XamlSourceGeneratorFSharp.targets` (imported by `Sdk.targets`): for a `.fsproj` with the switch on,
  before `CoreCompile` it writes the resolved references, runs the tool on `@(Page);@(ApplicationDefinition)` and puts the generated files **in front of**
  the project's own `Compile` items (F# is order sensitive). Default tool path `tools/xsg-fsharp/` next to the targets (the SDK package ships it, see below; override with
  `LibreWpfXsgFSharpTool`).
- Shape of the output: each `x:Class` is an F# class `type Picker() as this = inherit UserControl()` that declares the `x:Name` fields (`val mutable`),
  builds the tree in `InitializeComponent`, called from the constructor, and wires event handlers **by name through reflection on the object's run-time
  type** (the generated file comes before the code that has the handlers): the user writes `type MyPicker() = inherit Picker()` with
  `member this.OnOk(sender, e) = ...`.
- Proved with `tests/fixtures/FSharpWpfFixture` (new): an F# project with one UserControl compiles with the SDK targets alone, and running it shows the
  tree built, `x:Name` fields set, names registered, and a `TextChanged` handler of the subclass called. Printer tests: `FSharpTranslationTests.cs`.
- **The tool ships in the SDK package** (`LibreWPF.Sdk`, `tools/xsg-fsharp/`, packed by `ProGPU.Wpf.Sdk.ArchNeutral.csproj` like
  `PresentationBuildTasks`; checked with `dotnet pack`), and the default `LibreWpfXsgFSharpTool` points there. An F# project is now just
  `<Project Sdk="LibreWPF.Sdk">` with `UseWPF` and `UseLibreWpfXamlSourceGenerator=true`: `*.xaml` become `Page` items by default
  (`EnableDefaultPageItems=false` turns that off), nothing else.
- **F# executables start the platform**: the SDK's bootstrap is a C# module initializer, which F# has not. `ProGPU.Wpf.Sdk.PortableBootstrap.fs` is its
  F# form (same constants, WPF/Forms/native-MIL branches), compiled in front of the user's files for an executable. An F# exe with an explicit
  `[<EntryPoint>]` initializes files lazily, so the start is the idempotent function `ProGpu.Wpf.Sdk.PortableBootstrap.ensure ()`: the generated classes call
  it before building their tree (the tool's `--bootstrap`), a program that creates windows in code calls it first in `main`.
- `tests/fixtures/FSharpWpfFixture` shows the whole thing running on LibreWPF (macOS): XAML tree, fields, names, a handler of the subclass, and a `Window`
  with the control shown and closed (`window shown, loaded=true`).
- Custom markup extensions, compiled bindings and merged loose dictionaries work in F# output (the fixture uses them); registration by resource path
  happens when the first generated class of an assembly is constructed (no module initializer in F#).
- Not done: C# expressions, hot reload; handlers are checked
  only at run time; `x:Class` types whose base is a project type are not possible (the tool sees only the references); the LibreWinForms branches of the F#
  bootstrap are translated but not exercised; the `ProGPU.Wpf.Sdk.PortableBootstrap.cs/.fs` pair must be kept in step by hand.

All three languages need the translated output to register names through `RegisterName`, which needs the root's `NameScope`: the generator now
creates it when the root is a `FrameworkElement` (a `UserControl` has none, a `Window` already does).

## Generator fixes made in `../openavalon/LibreWPF/src/LibreWPF.Xaml.SourceGenerator` (committed in LibreWPF `c3c74ac07` and `1a182ee1b`; 249 generator tests pass)

New tests in `SelfHostGapTests.cs` (9). Each item is a construct OpenDevelop's XAML uses that BAML accepted:

1. keyword `x:Name` (`this`, `event`) escaped (`@this`);
2. code-behind `partial class Foo` without a base class: the generated part names the root element's type as base (also when there is no
   code-behind class at all), and follows the code-behind's accessibility (`internal partial class`) or `x:ClassModifier`;
3. `[XmlnsDefinition]` URN xmlns of referenced assemblies (`urn:TomsToolbox.Wpf.Styles`) resolved for elements, `x:Static`, `x:Type`,
   attached properties and property elements;
4. named root element as an `ElementName` binding source, typed as the `x:Class` type;
5. root element: content by `[ContentProperty]` of its real type (a project's own `OptionPanel : UserControl` is not a Panel because of its
   name), `<Grid.RowDefinitions>` / `<Window.TaskbarItemInfo>` / `<Window.Title><Binding/>` property elements, attached properties and attached
   events (`FocusManager.FocusedElement`, `TextBoxBase.TextChanged`, `VirtualizingStackPanel.IsVirtualizing`, `Setxxx` found on a base type),
   `x:ClassModifier`/`x:Subclass`/`mc:Ignorable`/`d:*` not written as properties, custom markup extensions, event handlers;
6. event handlers of controls: any CLR event of the type or a base (`TextChanged`, `SelectionChanged`), not a fixed name list;
   routed events on template nodes (`AddHandler` on the factory); a non-routed event in a template is reported, not emitted as a property;
7. a project's own markup extensions (`{core:Localize ...}`, `{sd:OptionBinding Type.Prop, Converter={StaticResource c}}`, also as an
   element in a collection and as a resource such as `<x:StaticExtension x:Key=.../>`): constructed in generated code, value obtained
   through `ProvideValue` with an `IProvideValueTarget` (generated helper `__XsgApplyExtension`);
8. WPF length units (`14pt`, `1in`, `2.54cm`); text for types with a `TypeConverter` (`<Brush x:Key=..>#DADFEA</Brush>`, `DataGridLength`,
   `<SolidColorBrush>` text) converted at run time; text content with white space is collapsed and newline/tab escaped in string literals;
   `<Hyperlink>text</Hyperlink>` becomes a `Run`;
9. element in another xmlns with the name of a WPF type (`b:EventTrigger` of Microsoft.Xaml.Behaviors) no longer resolves to WPF's;
   `<b:Interaction.Triggers>` (attached property element with a prefix) uses the owner's `Get...` collection;
10. `{TemplateBinding Level, Converter={StaticResource C}}`; `<Border.Resources><ResourceDictionary/></Border.Resources>` is a value, not an
    entry; `Label.Target="box"` (an element property written as an x:Name, possibly declared later) assigned after the tree is built;
11. `x:FieldModifier`; fields stay `internal` (BAML default); a field the code-behind already declares is not declared again;
    `_contentLoaded` field (the BAML part had it; WpfDesigner's code-behind uses it);
12. compiled bindings: a member the declared type lacks is only a warning (reflective binding) when the source is a RelativeSource /
    ElementName or an `object` (`DesignItem.Component.X`), not error `LWXSG0006`;
13. `DependencyProperty.FromName` (internal) replaced by `DependencyPropertyDescriptor.FromName` where it is not accessible;
14. `StaticResource` on a non-element (`GridViewColumn.CellTemplate`) or inside a template (a converter in `ControlTemplate.Resources`):
    looked up on the element / template that declares it, not on the page (`this`), which does not have it; hot-reload handler of a
    `ResourceDictionary` root no longer touches `Dispatcher`.

15. an `ISupportInitialize` object that is not an element (the designer's brush/transform helper types) is bracketed by `BeginInit`/`EndInit`
    like the XAML reader does. Without it the WPF designer's brush handles were empty under XSG (its `GradientBrush` helpers
    initialise in `EndInit`);
16. `{TemplateBinding}` named options in any order; `<Window.Resources><ResourceDictionary>` holding only entries is built on a dictionary the
    root already has; the generated `_contentLoaded` field is silenced for CS0414; the SDK does not add a second generator reference when the
    project has one as `PackageReference`, `ProjectReference` or `Analyzer` (any case).

## What is still open

Generator / SDK (`../openavalon`):
- Aero ambiguity: fixed in the SDK (above). A type that exists only in a dropped theme assembly (not in Aero) would now be unresolved: none seen.
- The two icon WARNs at startup (`Viewport3D`, `PropertyPad`: `Could not load XAML icon ...`) are **not** an XSG problem: those files do not
  exist under `Resources/VS2026`, the icons are referenced but missing (the VS2026 icons are `Resource` items and load fine).
- **Entry point**: `App.xaml` as `ApplicationDefinition` gets no generated `Main` (fixtures `WpfHotReloadFixture`,
  `HotReloadClassLibHost` fail with CS5001); intended for XSG projects (own `Program.cs`) but a BAML-to-XSG migration must know.
- **No mixed mode per project** with the environment variable (see above). `UseLibreWpfXamlSourceGenerator=true` now adds the generator
  `PackageReference` itself (SDK, `LibreWpfXsgAutoReference=false` opts out).
- Visual-tree fidelity is unchecked: events in templates that are not routed are dropped (reported), `FindResource` is now evaluated at
  construction time (BAML defers), `Label.Target` style name references are deferred to the end of `InitializeComponent`.

OpenDevelop (adjustable XAML/projects):
- `AvalonEdit.AddIn/.../ContextActionOptions.xaml`: `DataTemplate DataType="Group"` named no type (BAML accepted it silently and never
  matched); changed to `{x:Type CollectionViewGroup}` (an actual fix). `SharpTreeNodeView` also got null guards (`ParentItem == null` while the node is detached), which BAML's lazy template loading hid.
- Add the generator package reference for the IDE projects (C# and VB) behind a property, drop `Aero2`/`AeroLite`/... from the compile
  references (done in the SDK), and keep the Uno/MAUI fixtures out of the switch.
- `tests/fixtures/*` that are WPF applications need their own `Program.cs` for XSG.

WPF designer journey under XSG: `WpfDesigner_TransformResourceBrushAndHandleEditsRoundTrip` reaches the same step as with BAML (the native drag
delivers no mouse-down on this machine, an environment problem); the handle checks before it pass.

Verified: build of every IDE project (C# and VB) with XSG; startup, DevFlow, solution open, `.cs` and `.xaml` open, New Solution dialog.
Not verified: the rest of the UI (the VB option panels were compiled, not opened), designers, debugger, Hot Reload, Release/NativeAOT, performance.
