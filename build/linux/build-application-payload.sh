#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"; config="${DIST_CONFIG:-Release}"; rid="${DIST_LINUX_RID:?DIST_LINUX_RID is required}"
case "$rid" in linux-x64) other=linux-arm64;; linux-arm64) other=linux-x64;; *) echo "unsupported Linux RID: $rid" >&2; exit 1;; esac
base="$root/src/Main/SharpDevelop/bin/$config/net10.0-windows"; src="$base/publish"; payload="$root/OpenDevelop-$rid"
[[ -d "$src" && -d "$root/AddIns" ]] || { echo 'host publish or AddIns output is missing' >&2; exit 1; }
rm -rf "$payload"; mkdir -p "$payload"
# Microsoft.Build.NuGetSdkResolver is written into the host publish folder by the app's own first
# launch, naming that machine's SDK by absolute path; copied into the payload, every SDK-style
# project load fails on any other machine with "A manifest file exists, but the path to the SDK
# Resolver DLL file could not be found" (issue #21). The app rewrites it from the local SDK.
# CliclickSharp is the macOS input helper the DevFlow agent ships: the RID-less publish carries it
# and every addin folder carries its own copy (51 files, 172 MB raw, 78 MB of the released ZIP). .lib/.exp
# are link-time inputs of ProGPU.Native.Sdk; deps.json lists them, but CoreCLR never loads one,
# which is why the macOS bundle already deletes their .a equivalents. linux-arm is the 32-bit tree
# this payload cannot launch. The patterns without a '/' match a file name at any depth, so they
# also cover the copies inside AddIns/ and inside each out-of-process child host.
rsync -a --exclude '/AddIns/***' --exclude '/data/***' --exclude '/SdkResolvers/Microsoft.Build.NuGetSdkResolver/***' --exclude '/runtimes/win*/***' --exclude '/runtimes/osx*/***' --exclude "/runtimes/$other/***" --exclude '/runtimes/linux-arm/***' --exclude 'CliclickSharp*' --exclude '*.lib' --exclude '*.exp' --exclude '*.a' "$src/" "$payload/"
rsync -a "$root/data/" "$payload/data/"
rsync -a --exclude '*.pdb' --exclude '**/ref/***' --exclude '**/runtimes/win*/***' --exclude '**/runtimes/osx*/***' --exclude "**/runtimes/$other/***" --exclude '**/runtimes/linux-arm/***' --exclude 'CliclickSharp*' --exclude '*.lib' --exclude '*.exp' --exclude '*.dylib' --exclude '*.a' "$root/AddIns/" "$payload/AddIns/"
# The SDK/resolver are product files: add-ins created by an installed IDE resolve them from the
# application root, not from the source checkout. Match the macOS bundle layout.
resolver="$root/src/SDK/OpenDevelop.Addin.SdkResolver/bin/$config/net10.0"
[[ -f "$resolver/OpenDevelop.Addin.SdkResolver.dll" ]] || { echo 'AddIn SDK resolver output is missing' >&2; exit 1; }
mkdir -p "$payload/Sdks/OpenDevelop.Addin.Sdk/Sdk" "$payload/SdkResolvers/OpenDevelop.Addin.SdkResolver"
rsync -a "$root/src/SDK/OpenDevelop.Addin.Sdk/Sdk/" "$payload/Sdks/OpenDevelop.Addin.Sdk/Sdk/"
rsync -a --include '*.dll' --exclude '*' "$resolver/" "$payload/SdkResolvers/OpenDevelop.Addin.SdkResolver/"
printf '<SdkResolver><Path>OpenDevelop.Addin.SdkResolver.dll</Path></SdkResolver>\n' > "$payload/SdkResolvers/OpenDevelop.Addin.SdkResolver/OpenDevelop.Addin.SdkResolver.xml"
# Whatever the mirror could not exclude (an older payload layout) must not survive either.
rm -rf "$payload/SdkResolvers/Microsoft.Build.NuGetSdkResolver"
# LibreWPF.Sdk compiles its Win32-compatibility shim on Linux and writes it to TargetDir (not to the
# framework-dependent PublishDir) as <name>.dll.so and lib<name>.dll.so, the file names the runtime
# probes for a P/Invoke of "<name>.dll". AvalonDock calls kernel32 directly, so without them the
# docking manager's Loaded handler throws DllNotFoundException and the main window never renders.
for name in kernel32 user32 gdi32 dwmapi uxtheme shell32 gdiplus comdlg32 PresentationNative_cor3; do
  for shim in "$name.dll.so" "lib$name.dll.so"; do
    [[ -f "$base/$shim" ]] || { echo "required LibreWPF shim not found: $base/$shim" >&2; exit 1; }
    cp -p "$base/$shim" "$payload/$shim"
  done
done
# Silk.NET finds GLFW under runtimes/<rid>/native only by mapping the machine's distro RID
# (ubuntu.24.04-x64, ...) to linux-x64 through the runtime's RID graph. Stock Microsoft runtimes
# carry no distro RIDs, so on them it never probes runtimes/linux-x64/native, GLFW fails to load,
# no window can be created and the app exits 0 without output. Its application-base probe still
# works, so keep the small GLFW library there as well.
glfw="$payload/runtimes/$rid/native/libglfw.so.3"
[[ -f "$glfw" ]] || { echo "GLFW native library is missing: $glfw" >&2; exit 1; }
cp -p "$glfw" "$payload/libglfw.so.3"
cat > "$payload/OpenDevelop" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet "$here/OpenDevelop.dll" "$@"
EOF
chmod +x "$payload/OpenDevelop"
