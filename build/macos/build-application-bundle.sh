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

if ! command -v rsync >/dev/null 2>&1; then
  echo "build-application-bundle.sh: rsync is required to assemble an incremental bundle" >&2
  exit 1
fi

# Do not delete and recreate the complete application bundle on every distribution iteration.
# AddIns alone can contain several gigabytes of dependency closures. rsync --delete retains the
# exact-payload guarantee (stale files still disappear) while reusing unchanged host, SDK, data and
# filtered AddIn files from the previous bundle.
mkdir -p "$bundle_root/Contents/Resources" "$bundle_macos"
cp "$script_dir/Info.plist" "$bundle_root/Contents"

# Info.plist ships a placeholder version; stamp the real one so Finder, Spotlight and
# "About This Mac > System Report" agree with the version inside the binaries. Both come from the
# same source of truth as the assemblies - the git tag, via GitVersion (see GitVersion.yml) - read
# here out of the generated GlobalAssemblyInfo.cs rather than recomputed, so the bundle can never
# disagree with what was actually compiled.
global_assembly_info="$repo_root/src/Main/GlobalAssemblyInfo.cs"
if [[ -f "$global_assembly_info" ]]; then
  read_const() { sed -n "s/.*public const string $1 = \"\([^\"]*\)\".*/\1/p" "$global_assembly_info" | head -1; }
  major="$(read_const Major)"; minor="$(read_const Minor)"; build="$(read_const Build)"; revision="$(read_const Revision)"
  if [[ -n "$major" && -n "$minor" && -n "$build" ]]; then
    short_version="${major}.${minor}.${build}"
    # CFBundleVersion must increase between releases of the same short version; the commit count
    # since the tag is exactly that counter.
    bundle_version="${short_version}.${revision:-0}"
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${short_version}" \
                            -c "Set :CFBundleVersion ${bundle_version}" \
                            "$bundle_root/Contents/Info.plist"
    echo "Stamped bundle version ${bundle_version} (short ${short_version})"
  else
    echo "warning: could not parse a version from ${global_assembly_info}; Info.plist keeps its placeholder" >&2
  fi
else
  echo "warning: ${global_assembly_info} not found (build the host first); Info.plist keeps its placeholder" >&2
fi
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
  local exclude_file="$2"
  rsync -a --delete "$repo_root/data/" "$macos/data/"

  # AddIn build outputs contain their full dependency closures. Files already
  # supplied by the published host resolve from the application base directory.
  # The caller derives this list from *this* host publish, rather than inspecting
  # the existing bundle: the latter also contains prior AddIns on an incremental
  # run and would accidentally exclude the very files that need refreshing.
  # This retains the old basename/locale matching semantics without copying ~2 GB
  # and then walking the bundle again to delete it.

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
    "LanguageServices/WinUIXamlLanguageServer"
    "LanguageServices/UnoXamlLanguageServer"
    "LanguageServices/ProGpuWinUIXamlLanguageServer"
    "LanguageServices/LibreWpfXamlLanguageServer"
  )
  local keep_args=()
  for folder in "${keep_addin_folders[@]}"; do
    keep_args+=(--include="$folder/***")
  done

  # WorkflowDesigner and StrideGameStudio are independently versioned external addins.
  # Their repositories deploy them into a local installed IDE for their own tests; the
  # base distribution must not carry either stale manifest or implementation.
  rsync -a \
    --delete \
    --delete-excluded \
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
# These payload roots have a different source and their own rsync --delete pass below. Excluding
# them here is load-bearing: otherwise the host sync would delete the previous AddIns tree before
# the filtered AddIns sync can compare it, turning every bundle refresh back into a full copy.
#
# The host publish is RID-less, so it carries every platform's runtimes/ tree. Windows needs its
# win-x64/win-arm64 trees (one payload serves both dotnet hosts there), but macOS can load none of
# them: skip them exactly as the AddIns sync below does. They were ~530 MB of the bundle, including
# a Windows libSkiaSharp.pdb and LibreWPF.Transport's full per-RID managed payload.
rsync -a --delete \
  --exclude '/AddIns/***' \
  --exclude '/data/***' \
  --exclude '/Sdks/***' \
  --exclude '/SdkResolvers/***' \
  --exclude '/runtimes/win*/***' \
  --exclude '/runtimes/linux*/***' \
  --exclude '/runtimes/unix*/***' \
  "$src/" "$bundle_macos/"
# --delete leaves excluded paths alone (and --delete-excluded would also wipe the payload roots
# above), so remove trees an older bundle already carries.
rm -rf "$bundle_macos"/runtimes/win* "$bundle_macos"/runtimes/linux* "$bundle_macos"/runtimes/unix*

# Make the Addin SDK part of the installed IDE rather than a separately published
# NuGet package. The resolver is built as part of SharpDevelop's project graph.
sdk_source="$repo_root/src/SDK/OpenDevelop.Addin.Sdk/Sdk"
resolver_source="$repo_root/src/SDK/OpenDevelop.Addin.SdkResolver/bin/${config}/net10.0"
if [[ ! -d "$sdk_source" || ! -f "$resolver_source/OpenDevelop.Addin.SdkResolver.dll" ]]; then
  echo "OpenDevelop Addin SDK/resolver output was not built" >&2
  exit 1
fi
mkdir -p "$bundle_macos/Sdks/OpenDevelop.Addin.Sdk/Sdk" "$bundle_macos/SdkResolvers/OpenDevelop.Addin.SdkResolver"
rsync -a --delete "$sdk_source/" "$bundle_macos/Sdks/OpenDevelop.Addin.Sdk/Sdk/"
rsync -a --delete --include '*.dll' --exclude '*' "$resolver_source/" "$bundle_macos/SdkResolvers/OpenDevelop.Addin.SdkResolver/"
cat > "$bundle_macos/SdkResolvers/OpenDevelop.Addin.SdkResolver/OpenDevelop.Addin.SdkResolver.xml" <<'EOF'
<SdkResolver><Path>OpenDevelop.Addin.SdkResolver.dll</Path></SdkResolver>
EOF

# LibreWPF builds one native Win32-compatibility shim and exposes it under the
# P/Invoke library names used by WPF/AvalonDock. Its SDK target writes these to
# TargetDir after Build, not to framework-dependent PublishDir. They are required
# deployment assets, not duplicated AddIn dependencies.
win32_shims=(kernel32 user32 gdi32 dwmapi uxtheme shell32 gdiplus comdlg32 PresentationNative_cor3)
for name in "${win32_shims[@]}"; do
  shim="$base_dir/$name.dll"
  if [[ ! -f "$shim" ]]; then
    echo "build-application-bundle.sh: required LibreWPF shim not found: $shim" >&2
    exit 1
  fi
  cp -p "$shim" "$bundle_macos/$name.dll"
done

# Build the AddIn filter from only current host-owned files.  Do this after the
# shim copy, because those DLLs are also resolved from the application base
# directory, but never derive it from bundle_macos: an existing incremental
# bundle includes prior AddIns, data and SDK files that do not belong in this
# ownership set.
host_exclude_file="$(mktemp "${TMPDIR:-/tmp}/opendevelop-addin-excludes.XXXXXX")"
trap 'rm -f "$host_exclude_file"' EXIT
{
  while IFS= read -r -d '' host_file; do
    printf '**/%s\n' "$(basename "$host_file")"
  done < <(find "$src" -type f -print0)
  for name in "${win32_shims[@]}"; do
    printf '**/%s.dll\n' "$name"
  done
} | LC_ALL=C sort -u > "$host_exclude_file"

populate_repo_payload "$bundle_macos" "$host_exclude_file"
rm -f "$host_exclude_file"
trap - EXIT

echo "Bundle ready: $bundle_root"
