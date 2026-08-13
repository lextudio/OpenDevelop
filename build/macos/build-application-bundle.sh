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

  rsync -a \
    --exclude '*.pdb' \
    --exclude 'LeXtudio.DevFlow.*' \
    --exclude 'CliclickSharp' \
    --exclude-from "$exclude_file" \
    "$repo_root/AddIns/" "$macos/AddIns/"
  rm -f "$exclude_file"

}

src="$base_dir/publish"
if [[ ! -d "$src" ]]; then
  echo "Framework-dependent publish directory not found: $src" >&2
  exit 1
fi
cp -Rp "$src"/. "$bundle_macos/"

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
