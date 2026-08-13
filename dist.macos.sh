#!/usr/bin/env bash
# Build a framework-dependent macOS .dmg for local testing.
# Mirrors the CI steps in .github/workflows/package.yml.
# Usage: ./dist.macos.sh [--skip-publish] [--debug]
#   --skip-publish  reuse existing publish output (faster iteration on bundle/dmg)
#   --debug         package the Debug configuration instead of Release.

set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
host_dir="$script_dir/src/Main/SharpDevelop/SharpDevelop.csproj"
mvp_solution="$script_dir/OpenDevelop.Mvp.slnx"

dotnet_candidates=(
  "/usr/local/share/dotnet/dotnet"
  "/opt/homebrew/bin/dotnet"
)
dotnet=""
for c in "${dotnet_candidates[@]}"; do
  if [[ -x "${c}" ]]; then
    dotnet="${c}"
    break
  fi
done
if [[ -z "${dotnet}" ]]; then
  dotnet="$(command -v dotnet 2>/dev/null || true)"
fi
if [[ -z "${dotnet}" || ! -x "${dotnet}" ]]; then
  echo "dist.macos.sh: cannot find dotnet (checked ${dotnet_candidates[*]} and PATH)" >&2
  exit 1
fi
dotnet="$(readlink -f "${dotnet}")"

skip_publish=0
config="Release"
for arg in "$@"; do
  [[ "$arg" == "--skip-publish" ]] && skip_publish=1
  [[ "$arg" == "--debug" ]] && config="Debug"
done

# Ensure clean state for ICSharpCode.Core.Presentation — its .g.resources
# (WPF theme resource blob) can otherwise stale-cross from a previous build
# and produce a 12-byte corrupt file that crashes at boot with
# EndOfStreamException in FindResource / LoadThemedDictionary.
core_pres_obj="${script_dir}/src/Main/ICSharpCode.Core.Presentation"
rm -rf "${core_pres_obj}/obj/${config}" "${core_pres_obj}/bin/${config}"

# Clear the shared intermediate output so publish cannot reuse artifacts left
# by a previous build. This distribution intentionally remains
# framework-dependent and uses the installed .NET runtime; the SDK-generated
# apphost is only the native entry point and does not bundle that runtime.
host_obj="${script_dir}/src/Main/SharpDevelop/obj/${config}"

if [[ "$skip_publish" -eq 0 ]]; then
  echo "==> Cleaning intermediate outputs…"
  rm -rf "$host_obj/net10.0-windows"
  echo "==> Publishing framework-dependent app (${config})…"
  publish_dir="$script_dir/src/Main/SharpDevelop/bin/${config}/net10.0-windows/publish"
  rm -rf "$publish_dir"
  "${dotnet}" publish "$host_dir" -c "${config}" --self-contained false \
    -p:OpenDevelopDistributionBuild=true \
    -p:PublishDir="$publish_dir"

  if [[ ! -d "$publish_dir" ]]; then
    echo "dist.macos.sh: host publish directory not found: $publish_dir" >&2
    exit 1
  fi

  # NuGet conflict resolution omits LibreWinForms from the standard publish
  # closure. Patch the final manifest and copy its matching runtime files.
  nuget_packages="$("${dotnet}" nuget locals global-packages --list | sed -n 's/^global-packages: //p')"
  if [[ -z "$nuget_packages" ]]; then
    echo "dist.macos.sh: cannot determine the NuGet global-packages directory" >&2
    exit 1
  fi
  python3 "$script_dir/build/patch-librewinforms-deps.py" \
    "$publish_dir/OpenDevelop.deps.json" "$nuget_packages"

  # Some projects write to OpenDevelopHostPublishDir while computing their
  # distribution closure. Give that build a disposable copy so the verified
  # host deployment above remains immutable.
  host_publish_snapshot="$(mktemp -d "${TMPDIR:-/tmp}/opendevelop-host-publish.XXXXXX")"
  cp -Rp "$publish_dir"/. "$host_publish_snapshot/"

  echo "==> Cleaning stale AddIn outputs…"
  find "$script_dir/AddIns" -type f \( -name '*.dll' -o -name '*.dylib' -o -name '*.so' -o -name '*.pdb' \) -delete

  echo "==> Building distribution AddIns without shared runtime copies…"
  "${dotnet}" build "$mvp_solution" -c "${config}" --no-restore \
    -p:OpenDevelopDistributionBuild=true \
    -p:OpenDevelopHostPublishDir="$host_publish_snapshot" \
    -p:ProGpuWpfCopyPackageRuntimeAssets=false
  rm -rf "$host_publish_snapshot"

  # The solution traversal may copy reference assemblies over the original
  # PublishDir through cached project state. Restore the authoritative package
  # runtime payload only after every build has completed.
  python3 "$script_dir/build/patch-librewinforms-deps.py" \
    "$publish_dir/OpenDevelop.deps.json" "$nuget_packages"
else
  echo "==> Skipping publish (--skip-publish)"
fi

echo "==> Building framework-dependent .app bundle (${config})…"
DIST_CONFIG="${config}" "$script_dir/build/macos/build-application-bundle.sh"

echo "==> Smoke-testing packaged app…"
smoke_log="$(mktemp "${TMPDIR:-/tmp}/opendevelop-package-smoke.XXXXXX")"
"$script_dir/OpenDevelop.app/Contents/MacOS/OpenDevelop" >"$smoke_log" 2>&1 &
smoke_pid=$!
for _ in {1..10}; do
  sleep 1
  if ! kill -0 "$smoke_pid" 2>/dev/null; then
    wait "$smoke_pid" || smoke_status=$?
    echo "dist.macos.sh: packaged app exited during startup (status ${smoke_status:-0})" >&2
    sed -n '1,160p' "$smoke_log" >&2
    rm -f "$smoke_log"
    exit 1
  fi
done
kill -TERM "$smoke_pid"
wait "$smoke_pid" 2>/dev/null || true
rm -f "$smoke_log"
echo "Packaged app startup smoke test passed"

echo "==> Building .dmg…"
"$script_dir/build/macos/build-dmg.sh" OpenDevelop.app OpenDevelop-macos.dmg

echo ""
echo "Done: $(pwd)/OpenDevelop-macos.dmg"
