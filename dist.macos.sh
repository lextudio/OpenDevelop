#!/usr/bin/env bash
#
# dist.macos.sh — thin wrapper. All packaging logic lives in the cross-platform dist.ps1 so the
# flow stays reviewable next to launch.ps1 and can later be shared with Windows.
#
# Usage: ./dist.macos.sh [--skip-publish] [--debug]
#   --skip-publish  reuse existing publish output (faster iteration on bundle/dmg)
#   --debug         package the Debug configuration instead of Release.#
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

pwsh_bin="$(command -v pwsh 2>/dev/null || true)"
if [[ -z "${pwsh_bin}" ]]; then
  for c in /opt/homebrew/bin/pwsh /usr/local/bin/pwsh; do
    if [[ -x "${c}" ]]; then pwsh_bin="${c}"; break; fi
  done
fi
if [[ -z "${pwsh_bin}" ]]; then
  echo "dist.macos.sh: cannot find pwsh (PowerShell). Install it with: brew install --cask powershell" >&2
  exit 1
fi

args=()
for a in "$@"; do
  case "${a}" in
    # Explicit map: PowerShell does NOT bind "-skip-publish" to "-SkipPublish" (dashes
    # must go), and --debug is a PowerShell COMMON parameter name, so the ps1 exposes
    # -Configuration instead. Unrecognized flags fall through to PS's strict binding.
    --skip-publish) args+=("-SkipPublish") ;;
    --debug)        args+=("-Configuration" "Debug") ;;
    --*)            args+=("-${a#--}") ;;
    *)              args+=("${a}") ;;
  esac
done

# Ensure MSBuild subprocesses (GitVersion, etc.) can find the dotnet host.
dotnet_bin="$(command -v dotnet 2>/dev/null || true)"
if [[ -z "${dotnet_bin}" ]]; then
  for c in /opt/homebrew/bin/dotnet /usr/local/share/dotnet/dotnet; do
    if [[ -x "${c}" ]]; then dotnet_bin="${c}"; break; fi
  done
fi
if [[ -n "${dotnet_bin}" ]]; then
  dotnet_dir="$(cd "$(dirname "${dotnet_bin}")" && pwd)"
  export PATH="${dotnet_dir}:${PATH}"
  export DOTNET_ROOT="${dotnet_dir}"
fi

# Ensure the native Win32-compat shim can actually link.
#
# LibreWPF's SDK compiles ProGPU.Wpf.Sdk.Win32Compat.c with `cc -dynamiclib` after
# CopyFilesToOutputDirectory (ProGPU.Wpf.Sdk.targets, _ProGpuWpfSdkBuildWin32CompatShim), so a
# publish needs a working C toolchain even though nothing here is a C project. When the installed
# Command Line Tools' linker is older than the macOS SDK it selects, that link fails with:
#
#   ld: tapi error: malformed file
#   .../MacOSX<N>.sdk/usr/lib/libSystem.B.tbd:4:20: error: unknown architecture
#                      arm64e.x1-macos, arm64e.x1-maccatalyst ]
#
# because the newer SDK's stub libraries name architectures the older ld cannot parse. Nothing in
# that message points at an SDK/toolchain mismatch, and it reaches the console wrapped in an
# MSBuild MSB3073 from a "cc" Exec, so it reads like a broken project rather than a broken machine.
#
# Probe rather than hardcode: a working default is left completely alone, and SDKROOT is pinned
# only to a candidate that has been *verified* to link. An explicit SDKROOT from the caller always
# wins. If nothing links, warn and continue rather than exiting - the shim target is skipped
# entirely when its outputs are already present, so a build that would have succeeded must not be
# blocked by this check.
_probe_cc_link() {
  local sdk="$1" probe_dir rc
  probe_dir="$(mktemp -d)"
  printf 'int probe(void){return 0;}\n' >"${probe_dir}/probe.c"
  if [[ -n "${sdk}" ]]; then
    cc -isysroot "${sdk}" -dynamiclib -o "${probe_dir}/probe.dylib" "${probe_dir}/probe.c" >/dev/null 2>&1
  else
    cc -dynamiclib -o "${probe_dir}/probe.dylib" "${probe_dir}/probe.c" >/dev/null 2>&1
  fi
  rc=$?
  rm -rf "${probe_dir}"
  return ${rc}
}

if [[ -z "${SDKROOT:-}" ]] && command -v cc >/dev/null 2>&1 && ! _probe_cc_link ""; then
  echo "dist.macos.sh: the default C toolchain cannot link a dylib; probing for a usable macOS SDK..." >&2
  # xcrun's choice first (it honours xcode-select), then Xcode's own SDK, then the Command Line
  # Tools SDKs newest-first. Versioned names sort correctly enough for this; the probe is the real
  # arbiter either way.
  _sdk_candidates=("$(xcrun --show-sdk-path 2>/dev/null || true)")
  _sdk_candidates+=(/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk)
  while IFS= read -r _sdk; do
    [[ -n "${_sdk}" ]] && _sdk_candidates+=("${_sdk}")
  done < <(ls -1d /Library/Developer/CommandLineTools/SDKs/MacOSX*.sdk 2>/dev/null | sort -Vr || true)

  for _sdk in ${_sdk_candidates[@]+"${_sdk_candidates[@]}"}; do
    [[ -n "${_sdk}" && -d "${_sdk}" ]] || continue
    if _probe_cc_link "${_sdk}"; then
      export SDKROOT="${_sdk}"
      echo "dist.macos.sh: pinned SDKROOT=${SDKROOT}" >&2
      break
    fi
  done

  if [[ -z "${SDKROOT:-}" ]]; then
    echo "dist.macos.sh: WARNING - no macOS SDK on this machine can link a dylib. If the build" >&2
    echo "dist.macos.sh: fails in ProGPU.Wpf.Sdk.Win32Compat with 'tapi error: malformed file'," >&2
    echo "dist.macos.sh: update the Command Line Tools (xcode-select --install) or point them at" >&2
    echo "dist.macos.sh: a full Xcode (sudo xcode-select --switch /Applications/Xcode.app)." >&2
  fi
fi

# macOS's default bash (3.2) treats "${arr[@]}" on an EMPTY array as an unbound-variable
# error under `set -u` - guard explicitly instead of relying on `${arr[@]:-}`-style
# workarounds that read oddly for an array (same trick as rebuild-all.sh).
exec "${pwsh_bin}" -NoProfile -File "${repo_root}/dist.ps1" ${args[@]+"${args[@]}"}
