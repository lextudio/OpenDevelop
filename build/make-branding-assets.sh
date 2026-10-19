#!/usr/bin/env bash
#
# Regenerate the two product-icon binaries from the square master artwork.
#
#   images/opendevelop.png  ->  build/windows/opendevelop.ico
#                               (the shell's PE icon, the icon embedded as Resources\opendevelop.ico
#                                and set explicitly by Workbench/WpfWorkbench.xaml, and the apphost
#                                built for OpenDevelop.Launcher)
#                           ->  build/macos/opendevelop.icns
#                               (CFBundleIconFile of OpenDevelop.app)
#
# Only the serialisation is scripted; the masters stay hand-made. The running window icon comes from
# the .ico on every platform (WpfWorkbench.xaml sets Window.Icon explicitly, because the app runs as
# "dotnet exec OpenDevelop.dll"), while Finder and the Dock read the .icns, so both must be rebuilt
# from the same source in one go. Afterwards ./build.ps1 shell re-embeds the .ico and the splash, and
# ./dist.ps1 refreshes OpenDevelop.app and the DMG. See AGENTS.md, "Product branding".
#
# The ico used to be derived from the icns (which is a PNG container); generating both from the
# master directly keeps the two in step and avoids the intermediate's own scaling.

set -euo pipefail

if [[ $# -gt 1 ]]; then
  echo "Usage: $0 [square-master.png]"
  exit 1
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
master="${1:-$repo_root/images/opendevelop.png}"
icns_out="$repo_root/build/macos/opendevelop.icns"
ico_out="$repo_root/build/windows/opendevelop.ico"

if [[ ! -f "$master" ]]; then
  echo "make-branding-assets.sh: master image not found: $master" >&2
  exit 1
fi

for tool in sips iconutil python3; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "make-branding-assets.sh: $tool is required (macOS image tooling plus python3)" >&2
    exit 1
  fi
done

if ! python3 -c "import PIL" >/dev/null 2>&1; then
  echo "make-branding-assets.sh: Pillow is required for the multi-size .ico (python3 -m pip install Pillow)" >&2
  exit 1
fi

dimensions() {
  sips -g pixelWidth -g pixelHeight "$1" 2>/dev/null \
    | awk '/pixelWidth/ {w=$2} /pixelHeight/ {h=$2} END {if (w != "" && h != "") print w, h}'
}

read -r master_width master_height < <(dimensions "$master")

if [[ -z "${master_width:-}" || -z "${master_height:-}" ]]; then
  echo "make-branding-assets.sh: cannot read the size of $master" >&2
  exit 1
fi

# A square master is what makes one set of sizes usable as both a 1:1 icon and an .icns; a
# non-square file here is almost always the wrong artwork (social.png is the usual mix-up).
if [[ "$master_width" != "$master_height" && "${OD_BRANDING_ALLOW_NON_SQUARE:-}" != "1" ]]; then
  echo "make-branding-assets.sh: $master is ${master_width}x${master_height}, not square." >&2
  echo "  Pass the square master, or set OD_BRANDING_ALLOW_NON_SQUARE=1 to override." >&2
  exit 1
fi

social="$repo_root/images/social.png"
if [[ -f "$social" ]]; then
  read -r social_width social_height < <(dimensions "$social")
  if [[ -n "${social_width:-}" && -n "${social_height:-}" ]]; then
    # social.png is both the splash artwork (drawn 640x320, Stretch=Uniform) and the DMG window
    # background, so it has to stay 2:1 or the splash letterboxes and the DMG icon inset drifts.
    if ! awk -v w="$social_width" -v h="$social_height" 'BEGIN { r = w / h; exit !(r >= 1.98 && r <= 2.02) }'; then
      echo "make-branding-assets.sh: warning: images/social.png is ${social_width}x${social_height}, not 2:1;" >&2
      echo "  the splash (Startup/SplashScreenForm.xaml) and the DMG layout in build-dmg.sh assume 2:1." >&2
    fi
  fi
fi

work_dir="$(mktemp -d -t opendevelop-branding.XXXXXX)"
trap 'rm -rf "$work_dir"' EXIT

# The ten files iconutil expects. 512@2x is 1024, the largest an .icns carries.
iconset="$work_dir/opendevelop.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$master" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  sips -z "$((size * 2))" "$((size * 2))" "$master" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done

mkdir -p "$(dirname "$icns_out")" "$(dirname "$ico_out")"
iconutil -c icns "$iconset" -o "$icns_out"

# Windows renders these sizes in Explorer, the taskbar, Alt-Tab and file properties.
MASTER="$master" ICO_OUT="$ico_out" python3 - <<'PY'
import os
from PIL import Image

image = Image.open(os.environ["MASTER"]).convert("RGBA")
sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
image.save(os.environ["ICO_OUT"], format="ICO", sizes=sizes)
PY

echo "make-branding-assets.sh: regenerated from $master (${master_width}x${master_height})"
ls -la "$icns_out" "$ico_out"
file "$icns_out" "$ico_out"
