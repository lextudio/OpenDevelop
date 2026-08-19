# Repo-specific notes

## Finding and adding VS toolbar/menu icons

Source library: `/Users/lextm/Downloads/VS2017 Image Library/VS2017/<IconName>/` — one folder per
icon, each containing `<IconName>_16x.xaml` (+ `.png`/`.svg`/`.bmp` variants at other sizes). This
is the official Visual Studio 2017 Image Library; search it by folder name for a concept (e.g.
`Label`, `DisplayName`, `ShowTemplateRegionLabel`, `AlignLefts`).

```bash
find "/Users/lextm/Downloads/VS2017 Image Library/VS2017" -maxdepth 1 -iname "*label*"
```

### How an icon gets resolved at runtime

`PresentationResourceService.GetImageSource("Icons.16x16.<Key>")`
(`src/Main/ICSharpCode.Core.Presentation/PresentationResourceService.cs`) resolves `<Key>` to a
resource path by, in order:
1. An explicit entry in `xamlResourceMap` (full path override).
2. An explicit entry in `xamlResourceAliases` (`<Key>` → a plain icon name, still goes through step 3).
3. **The default convention**: take the last `.`-separated segment of `<Key>`, strip a trailing
   `Icon` suffix if present, and look up
   `Resources/VS2017/<IconName>/<IconName>_16x.xaml` embedded in the
   `ICSharpCode.Core.Presentation` assembly.

So `"Icons.16x16.FormsDesigner.AlignLefts"` → icon name `AlignLefts` → embedded resource
`Resources/VS2017/AlignLefts/AlignLefts_16x.xaml` — no alias entry needed for a straightforward
name; only add one when the desired `<Key>` doesn't already end in the real icon folder's name.

A missing/unregistered icon does **not** throw — `GetImageSource` just returns `null` (blank
icon), logged as a `WARN "Could not load XAML icon ... Cannot locate resource ..."`. That warning
in the app log is the tell that an icon needs to be added, not a real error to chase.

### Adding a new icon

1. Copy the whole icon folder (just the `_16x.xaml` is required; the rest is unused) from the VS
   Image Library into `src/Main/ICSharpCode.Core.Presentation/Resources/VS2017/<IconName>/`.
2. No csproj change needed — `ICSharpCode.Core.Presentation.csproj` already globs the whole
   `Resources\VS2017\**\*.xaml` folder as embedded `<Resource>` items.
3. Reference it as `"Icons.16x16.<IconName>"` (or any key whose last segment/alias resolves to
   `<IconName>`) via `IconService.GetImageSource(...)` /
   `PresentationResourceService.GetImageSource(...)`.
4. Verify live: open the feature in the running app and check the app log for the "Could not load
   XAML icon" warning — its absence confirms the resource was actually found and loaded.
