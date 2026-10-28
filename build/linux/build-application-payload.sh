#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"; config="${DIST_CONFIG:-Release}"; rid="${DIST_LINUX_RID:?DIST_LINUX_RID is required}"
case "$rid" in linux-x64) other=linux-arm64;; linux-arm64) other=linux-x64;; *) echo "unsupported Linux RID: $rid" >&2; exit 1;; esac
base="$root/src/Main/SharpDevelop/bin/$config/net10.0-windows"; src="$base/publish"; payload="$root/OpenDevelop-$rid"
[[ -d "$src" && -d "$root/AddIns" ]] || { echo 'host publish or AddIns output is missing' >&2; exit 1; }
rm -rf "$payload"; mkdir -p "$payload"
rsync -a --exclude '/AddIns/***' --exclude '/data/***' --exclude '/runtimes/win*/***' --exclude '/runtimes/osx*/***' --exclude "/runtimes/$other/***" --exclude '**/*.a' "$src/" "$payload/"
rsync -a "$root/data/" "$payload/data/"
rsync -a --exclude '**/*.pdb' --exclude '**/ref/***' --exclude '**/runtimes/win*/***' --exclude '**/runtimes/osx*/***' --exclude "**/runtimes/$other/***" --exclude '**/*.dylib' --exclude '**/*.a' "$root/AddIns/" "$payload/AddIns/"
# The SDK/resolver are product files: add-ins created by an installed IDE resolve them from the
# application root, not from the source checkout. Match the macOS bundle layout.
resolver="$root/src/SDK/OpenDevelop.Addin.SdkResolver/bin/$config/net10.0"
[[ -f "$resolver/OpenDevelop.Addin.SdkResolver.dll" ]] || { echo 'AddIn SDK resolver output is missing' >&2; exit 1; }
mkdir -p "$payload/Sdks/OpenDevelop.Addin.Sdk/Sdk" "$payload/SdkResolvers/OpenDevelop.Addin.SdkResolver"
rsync -a "$root/src/SDK/OpenDevelop.Addin.Sdk/Sdk/" "$payload/Sdks/OpenDevelop.Addin.Sdk/Sdk/"
rsync -a --include '*.dll' --exclude '*' "$resolver/" "$payload/SdkResolvers/OpenDevelop.Addin.SdkResolver/"
printf '<SdkResolver><Path>OpenDevelop.Addin.SdkResolver.dll</Path></SdkResolver>\n' > "$payload/SdkResolvers/OpenDevelop.Addin.SdkResolver/OpenDevelop.Addin.SdkResolver.xml"
for name in kernel32 user32 gdi32 dwmapi uxtheme shell32 gdiplus comdlg32 PresentationNative_cor3; do [[ -f "$base/$name.dll" ]] && cp -p "$base/$name.dll" "$payload/$name.dll"; done
cat > "$payload/OpenDevelop" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet "$here/OpenDevelop.dll" "$@"
EOF
chmod +x "$payload/OpenDevelop"
