#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 0 ]]; then
  echo "Usage: $0"
  exit 1
fi

config="${DIST_CONFIG:-Release}"
script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
base_dir="$repo_root/src/Main/SharpDevelop/bin/${config}/net10.0-windows"
bundle_root="$repo_root/OpenDevelop.app"
bundle_macos="$bundle_root/Contents/MacOS"

rm -rf "$bundle_root"
mkdir -p "$bundle_root/Contents/Resources" "$bundle_macos"
cp "$script_dir/Info.plist" "$bundle_root/Contents"
if [[ -f "$script_dir/opendevelop.icns" ]]; then
  cp "$script_dir/opendevelop.icns" "$bundle_root/Contents/Resources"
fi

# OpenDevelop locates its addins and data at runtime by walking UP from the
# executable looking for data/resources/languages/LanguageDefinition.xml
# (SharpDevelopMain.FindApplicationRootPath), then loading *.addin from
# <root>/AddIns. The payload must therefore contain data/ and AddIns/ next to
# the executable — put them in Contents/MacOS so the walk resolves on the
# first step and never escapes the bundle.
populate_repo_payload() {
  local macos="$1"
  cp -Rp "$repo_root/data" "$macos/data"
  if ! command -v rsync >/dev/null 2>&1; then
    echo "build-application-bundle.sh: rsync is required to filter AddIn dependencies" >&2
    exit 1
  fi

  # AddIn build outputs contain their full dependency closures. Files already
  # supplied by the published host resolve from the application base directory,
  # so tell rsync not to copy them into the bundle in the first place. This keeps
  # the old basename/locale matching semantics without copying ~2 GB and then
  # walking the bundle again to delete it.
  local exclude_file
  exclude_file="$(mktemp "${TMPDIR:-/tmp}/opendevelop-addin-excludes.XXXXXX")"
  # Include every host asset type. Distribution builds already prevent new
  # CopyLocal duplicates; this also keeps stale XML docs, satellite resources,
  # fonts and extensionless native helpers from an old developer build out of
  # the bundle without first copying or deleting them.
  while IFS= read -r -d '' host_file; do
    printf '**/%s\n' "$(basename "$host_file")" >> "$exclude_file"
  done < <(find "$macos" -type f -print0)

  # Out-of-process child deployments: folders that carry their own
  # *.runtimeconfig.json/*.deps.json and are spawned via `dotnet exec` as separate
  # processes (Uno design host, WinForms design host, SharpDbg.Cli DAP debugger).
  # Such a process resolves dependencies ONLY from its own folder - unlike regular
  # AddIn dlls, which fall back to the application base directory - so the basename
  # dedup above would strip exactly the assemblies it needs and the child crashes
  # at startup (FileNotFoundException for StreamJsonRpc.dll / 
  # Microsoft.VisualStudio.Validation.dll in the Uno design host). Keep these
  # folders' contents intact; add any future out-of-process child host here.
  # Entries are rsync include patterns relative to AddIns/; `dir/***` keeps the
  # whole folder. rsync applies rules in order, so these includes must precede the
  # exclude-from below.
  local keep_addin_folders=(
    "DisplayBindings/WinUIXamlDesigner/UnoHost"
    "DisplayBindings/FormsDesigner/Host"
    "DisplayBindings/WpfDesign/Host"
    "DisplayBindings/GtkDesigner/Host"
    "DisplayBindings/MewUIDesigner/Host"
    "DisplayBindings/WorkflowDesigner/Host"
    "Debugger"
    "LanguageServices/XamlLanguageServer.Wpf"
  )
  local keep_args=()
  for folder in "${keep_addin_folders[@]}"; do
    keep_args+=(--include="$folder/***")
  done

  # WorkflowDesigner and StrideGameStudio are independently versioned external addins.
  # Their repositories deploy them into a local installed IDE for their own tests; the
  # base distribution must not carry either stale manifest or implementation.
  rsync -a \
    --exclude '*.pdb' \
    --exclude '**/ref/***' \
    --exclude '**/runtimes/win*/***' \
    --exclude '**/runtimes/linux*/***' \
    --exclude '**/runtimes/unix*/***' \
    --exclude 'LeXtudio.DevFlow.*' \
    --exclude 'CliclickSharp' \
    --exclude 'DisplayBindings/WorkflowDesigner/***' \
    --exclude 'DisplayBindings/StrideGameStudio/***' \
    "${keep_args[@]}" \
    --exclude-from "$exclude_file" \
    "$repo_root/AddIns/" "$macos/AddIns/"
  rm -f "$exclude_file"

  # XML files paired with a DLL are compiler/API documentation, not runtime
  # configuration. Preserve genuine layouts such as Decompiler/Layouts/ILSpy.xml.
  while IFS= read -r -d '' documentation; do
    assembly_name="$(basename "${documentation%.xml}.dll")"
    if [[ -f "${documentation%.xml}.dll" || -f "$macos/$assembly_name" ]]; then
      rm -f "$documentation"
    fi
  done < <(find "$macos/AddIns" -type f -name '*.xml' -print0)

}

src="$base_dir/publish"
if [[ ! -d "$src" ]]; then
  echo "Framework-dependent publish directory not found: $src" >&2
  exit 1
fi
cp -Rp "$src"/. "$bundle_macos/"

# Make the Addin SDK part of the installed IDE rather than a separately published
# NuGet package. The resolver is built as part of SharpDevelop's project graph.
sdk_source="$repo_root/src/SDK/OpenDevelop.Addin.Sdk/Sdk"
resolver_source="$repo_root/src/SDK/OpenDevelop.Addin.SdkResolver/bin/${config}/net10.0"
if [[ ! -d "$sdk_source" || ! -f "$resolver_source/OpenDevelop.Addin.SdkResolver.dll" ]]; then
  echo "OpenDevelop Addin SDK/resolver output was not built" >&2
  exit 1
fi
mkdir -p "$bundle_macos/Sdks/OpenDevelop.Addin.Sdk" "$bundle_macos/SdkResolvers/OpenDevelop.Addin.SdkResolver"
cp -Rp "$sdk_source" "$bundle_macos/Sdks/OpenDevelop.Addin.Sdk/"
cp -p "$resolver_source"/*.dll "$bundle_macos/SdkResolvers/OpenDevelop.Addin.SdkResolver/"
cat > "$bundle_macos/SdkResolvers/OpenDevelop.Addin.SdkResolver/OpenDevelop.Addin.SdkResolver.xml" <<'EOF'
<SdkResolver><Path>OpenDevelop.Addin.SdkResolver.dll</Path></SdkResolver>
EOF

# LibreWPF builds one native Win32-compatibility shim and exposes it under the
# P/Invoke library names used by WPF/AvalonDock. Its SDK target writes these to
# TargetDir after Build, not to framework-dependent PublishDir. They are required
# deployment assets, not duplicated AddIn dependencies.
win32_shims=(kernel32 user32 gdi32 dwmapi uxtheme shell32 gdiplus comdlg32)
for name in "${win32_shims[@]}"; do
  shim="$base_dir/$name.dll"
  if [[ ! -f "$shim" ]]; then
    echo "build-application-bundle.sh: required LibreWPF shim not found: $shim" >&2
    exit 1
  fi
  cp -p "$shim" "$bundle_macos/$name.dll"
done

populate_repo_payload "$bundle_macos"

echo "Bundle ready: $bundle_root"
