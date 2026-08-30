# Default theme resources

Unmodified copies of every hand-authored `*_themeresources.xaml` resource dictionary from
Microsoft's [microsoft-ui-xaml](https://github.com/microsoft/microsoft-ui-xaml) repository
(MIT-licensed; see each file's own header), vendored here for design-time use only.

## Why they exist

The Microsoft WinUI 3 host is an unpackaged app. `XamlControlsResources` (the type real WinUI apps
merge into `Application.Resources` to get the Fluent v2 color/brush palette and default control
styles) is a **native** type whose construction reads compiled resources out of the WindowsAppSDK
package's own `resources.pri` (this is *separate* from the *consuming* app's PRI - it belongs to
the framework package). That native call throws `COMException 0x8000FFFF` when it runs unpackaged,
before a single resource is produced - see `Program.cs`'s note on why `XamlControlsResources` is
never merged.

These files are different: they are plain, hand-authored XAML - `Color`, `SolidColorBrush`,
`AcrylicBrush`, `Style`/`Setter` - with no compiled/native dependency. `XamlReader.Load` parses
them exactly like it parses the app's own resources, so loading them at startup gives the design
host the Fluent v2 color tokens and default text/control styles that real WinUI markup assumes are
always present, without needing MSIX PRI generation at all.

## Coverage

**Full**: every `*_themeresources.xaml` under `microsoft-ui-xaml/src/controls/dev/**` is here (as
of the vendored checkout - see Updating below), found via:

```sh
find src/controls -iname '*_themeresources.xaml'
```

Four matches from that search are deliberately excluded:

- `NavigationView/TestUI/CustomResources/NavigationViewCustomThemeResourcesPage.xaml` - not a
  resource dictionary at all (root is `<local:TestPage x:Class=...>`); the name only matches the
  glob by coincidence.
- `tools/GenerateNewControlProjectFiles/NEWCONTROL_themeresources.xaml` - a scaffold template for
  authoring a new control, its `<ResourceDictionary>` is empty.
- `CommonStyles/MenuFlyout_themeresources.xaml` - its default styles reference
  `TargetType="SplitMenuFlyoutItem"`, a control plain reflection cannot find in this WindowsAppSDK
  install (internal-only, or removed since this was vendored). Corpus runs that need this file's
  OTHER content (it also carries real MenuFlyoutItem/MenuFlyoutSubItem defaults) can instead rely on
  `FrameworkDefaultResources.PruneUnresolvableTargetTypes`, which drops just the unresolvable style
  and keeps the rest - this file is excluded here only because it was also the one file that
  exercises the conditional-XAML `revealBrushPresent` prefix (see below), so cutting it here doubled
  as removing that variable while diagnosing the SplitMenuFlyoutItem gap.
- `Materials/Reveal/RevealBrush_themeresources.xaml` - a conditionally-merged ALTERNATIVE default
  style for `ListViewItem`/`GridViewItem` (WinUI's now-legacy "Reveal" visual effect), gated in a
  real build by the same `?IsTypePresent(RevealBrush)` conditional-XAML check its xmlns declares.
  Merging it unconditionally alongside `ListViewItem_themeresources.xaml`/
  `GridViewItem_themeresources.xaml` produces TWO unkeyed default styles for the same TargetType,
  which is exactly as invalid as two elements sharing an explicit `x:Key` - `AppResourceBuilder`'s
  dedup (see below) treats an implicit style's TargetType as its identity for this reason, but the
  two files are still meant to be alternatives, not both-at-once, so only one belongs here.

Every remaining file was checked for constructs `XamlReader.Load` cannot handle in a loose (not
build-compiled) parse - `x:Bind`/`x:DataType`, and namespace prefixes pointing at anything other
than `Microsoft.UI.Xaml.*` (all of them resolve to real, always-loaded framework types; none
reference a C++-only or test-app-only type).

## How it works

`FrameworkDefaultResources.cs` merges the whole set through
`AppResourceBuilder.BuildFromDictionaryFiles`, the same engine that turns a project's own App.xaml
into the DDP `app/resources` payload. That engine already handles everything full coverage forces
it to confront across 80+ independently-authored files:

- Cross-file `ResourceDictionary.ThemeDictionaries` key collisions, at BOTH levels: the theme name
  itself (many files declare their own "Light"/"Dark"/"HighContrast" dictionary - merged by name,
  not concatenated as siblings) and an entry repeated under the same theme name across files (also
  observed in this set - merged by key, last file wins, not concatenated as siblings either).
- Duplicate top-level `x:Key`s across files (`TextBlock_themeresources_v2.5.xaml` intentionally
  redefines a few of `TextBlock_themeresources.xaml`'s styles) - last file wins.
- Duplicate UNKEYED default styles for the same `TargetType` (WinUI itself keys an implicit style
  by its TargetType, so two `<Style TargetType="ListViewItem">` with no `x:Key` collide exactly like
  two elements sharing an explicit key would - just reported by WinUI without a recognizable key
  name, which is what led to `Materials/Reveal/RevealBrush_themeresources.xaml`'s exclusion above).
- Forward `StaticResource` OR `ThemeResource` references within the merged set (real WinUI XAML
  routinely declares an implicit style before the named style it's `BasedOn`, and the framework's
  own files reach forward across FILES the same way - `CalendarDatePicker_themeresources.xaml`
  reaches into `CalendarView_themeresources.xaml` via `{ThemeResource DefaultCalendarViewStyle}`)
  - topologically reordered. ThemeResource needs this too, despite being a normally-lazy lookup:
    there is no live element/visual tree behind this single, isolated parse for it to defer to.
- xmlns prefix loss when an element is moved from its source file into the combined document
  (`xmlns:local`, `xmlns:controls`, ...) - re-stamped onto each moved element.
- A bare (unprefixed) `TargetType` that does not resolve at all in this environment
  (`FrameworkDefaultResources.PruneUnresolvableTargetTypes`, run only over the framework's own
  files, not over a project's App.xaml) - the offending `Style`/`ControlTemplate` is dropped rather
  than failing the whole merged dictionary.

## Updating

Source: `microsoft-ui-xaml/src/controls/dev/**/*_themeresources.xaml`, minus the four exclusions
above. Copy each file verbatim (unmodified) into this directory - flat, no subfolders (checked for
basename collisions when this set was assembled; there were none) - `WinUIXamlDesigner.MicrosoftHost.csproj`
embeds everything under `DefaultThemeResources/*.xaml` automatically, no csproj edit needed.
