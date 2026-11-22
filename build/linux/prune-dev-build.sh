#!/usr/bin/env bash
# Shrinks a local Debug/Release dev build to what a Linux host actually loads, and makes it launchable.
#
# The build is RID-less by design (CI rejects --runtime), so every add-in output receives the ProGPU /
# LibreWPF native payload for all nine RIDs: ~1.2 GB per add-in, ~30 GB for the tree. The Linux release
# strips the same files when it assembles its payload (build/linux/build-application-payload.sh); this
# applies those excludes in place to AddIns/ and every bin/ folder. Re-run it after each build: MSBuild
# copies the files back when they are missing.
#
# It also copies libglfw.so.3 to the host's application base: Silk.NET only probes runtimes/linux-x64
# on runtimes that carry distro RIDs, so a dev build otherwise dies at startup with
# "Could not load from any of the possible library names" (the release payload does the same copy).
#
# Usage: build/linux/prune-dev-build.sh [--dry-run]
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dry=0; [[ "${1:-}" == "--dry-run" ]] && dry=1

case "$(uname -m)" in
  x86_64) keep=linux-x64 ;;
  aarch64|arm64) keep=linux-arm64 ;;
  *) echo "unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

before=$(df --output=used -B1M / | tail -1)
targets=()
[[ -d "$root/AddIns" ]] && targets+=("$root/AddIns")
while IFS= read -r -d '' d; do targets+=("$d"); done < <(
  find "$root/src" "$root/tests" -type d -name bin -prune -print0 2>/dev/null)

for t in "${targets[@]}"; do
  # Foreign runtime trees (win*, osx*, the other Linux arch, 32-bit linux-arm, musl).
  while IFS= read -r -d '' d; do
    case "$(basename "$d")" in "$keep"|unix|any|linux) continue ;; esac
    if (( dry )); then echo "rm -r $d"; else rm -rf "$d"; fi
  done < <(find "$t" -type d -path '*/runtimes/*' -prune -print0)
  # Files the Linux release also drops: macOS input helper, link-time inputs, macOS dylibs.
  if (( dry )); then
    find "$t" \( -name 'CliclickSharp*' -o -name '*.lib' -o -name '*.exp' -o -name '*.dylib' -o -name '*.a' \) -print
  else
    find "$t" -depth \( -name 'CliclickSharp*' -o -name '*.lib' -o -name '*.exp' -o -name '*.dylib' -o -name '*.a' \) -exec rm -rf {} +
  fi
done

for host in "$root"/src/Main/SharpDevelop/bin/*/net11.0-windows; do
  glfw="$host/runtimes/$keep/native/libglfw.so.3"
  [[ -f "$glfw" ]] && (( ! dry )) && cp -p "$glfw" "$host/libglfw.so.3"
done

after=$(df --output=used -B1M / | tail -1)
(( dry )) || echo "prune-dev-build: freed $(( (before - after) / 1024 )) GB (kept runtimes/$keep)"
