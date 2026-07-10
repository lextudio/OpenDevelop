#!/usr/bin/env bash
set -euo pipefail

librewpf_root="/Users/lextm/uno-tools/librewpf"
dotnet="${librewpf_root}/.dotnet/dotnet"
package_output="${librewpf_root}/artifacts/packages/Release/NonShipping"
dev_version="11.0.0-dev"

export DOTNET_ROOT="$(dirname "${dotnet}")"
export DOTNET_HOST_PATH="${dotnet}"
sdk_dir="$(find "${DOTNET_ROOT}/sdk" -mindepth 1 -maxdepth 1 -type d | sort | tail -n 1)"
export MSBuildSDKsPath="${sdk_dir}/Sdks"
export MSBuildExtensionsPath="${sdk_dir}"
export MSBUILDADDITIONALSDKRESOLVERSFOLDER_NET="${sdk_dir}/SdkResolvers"
export MSBUILD_NUGET_PATH="${sdk_dir}"
export MSBuildEnableWorkloadResolver=false

version_props="-p:VersionPrefix=11.0.0 -p:Version=${dev_version} -p:AssemblyVersion=11.0.0.0 -p:FileVersion=11.0.0.0 -p:PackageVersion=${dev_version}"

wpf_src="${librewpf_root}/src/Microsoft.DotNet.Wpf/src"
interop_src="${librewpf_root}/external/ProGPU/src/ProGPU.Wpf.Interop"
progpu_src="${librewpf_root}/src/ProGPU.Wpf"
progpu_ext_src="${librewpf_root}/external/ProGPU/src"
transport_pack="${librewpf_root}/packaging/Microsoft.DotNet.Wpf.GitHub/Microsoft.DotNet.Wpf.GitHub.ArchNeutral.csproj"

# OpenDevelop's NuGet.config pulls these in as their OWN packages (see
# ProGPU.Wpf.Sdk.targets' "Package" reference-mode PackageReference list), NOT bundled inside
# LibreWPF.ProGPU.nupkg -- ProGPU.Wpf.csproj's ProjectReferences to them build the assemblies as a
# side effect of build_clean below, but they still need their own pack + cache-clear or OpenDevelop
# keeps resolving whatever stale copy was packed by a previous full CI run.
progpu_subsidiary_projects=(
  "${progpu_ext_src}/ProGPU.Backend/ProGPU.Backend.csproj"
  "${progpu_ext_src}/ProGPU.DirectX/ProGPU.DirectX.csproj"
  "${progpu_ext_src}/ProGPU.Scene/ProGPU.Scene.csproj"
  "${progpu_ext_src}/ProGPU.Vector/ProGPU.Vector.csproj"
  "${progpu_ext_src}/ProGPU.Text/ProGPU.Text.csproj"
  "${progpu_ext_src}/ProGPU.Compute/ProGPU.Compute.csproj"
  "${progpu_ext_src}/ProGPU.Transpiler/ProGPU.Transpiler.csproj"
)
progpu_subsidiary_nuget_ids=(progpu.backend progpu.directx progpu.scene progpu.vector progpu.text progpu.compute progpu.transpiler)

build_clean() {
  local proj="$1"
  local dir
  dir="$(dirname "$proj")"
  rm -rf "$dir/bin/Release" "$dir/obj/Release"
  "${dotnet}" build "$proj" -c Release -v:minimal ${version_props}
}

packpkg() {
  # --no-build/--no-restore: every project below was just built by build_clean above, so packing
  # without these flags would silently re-run Build (and an implicit restore) a second time per
  # project -- that duplicate work, not the per-project build loop itself, was the actual slow part.
  "${dotnet}" pack "$1" -c Release -o "${package_output}" -v:minimal --no-build --no-restore ${version_props}
}

packpkg_full() {
  # The Transport ArchNeutral packaging project needs its Restore and Build to run in the SAME
  # `dotnet pack` invocation: its target reads $(PkgMicrosoft_Private_Winforms), a property NuGet's
  # GeneratePathProperty only populates into props evaluated fresh after restore. Passing --no-build
  # here (like packpkg does for the other packages) skips that re-evaluation and the property comes
  # back empty. This project has no real source to compile, so skipping --no-build costs nothing.
  "${dotnet}" pack "$1" -c Release -o "${package_output}" -v:minimal ${version_props}
}

if [[ "${1:-}" == "--fast" ]]; then
  for proj in "${progpu_subsidiary_projects[@]}"; do
    dir="$(dirname "$proj")"
    rm -rf "$dir/bin/Release" "$dir/obj/Release"
  done
  build_clean "${interop_src}/ProGPU.Wpf.Interop.csproj"
  build_clean "${progpu_src}/ProGPU.Wpf.csproj"
  rm -f "${package_output}/LibreWPF.Interop.${dev_version}.nupkg" \
        "${package_output}/LibreWPF.ProGPU.${dev_version}.nupkg"
  packpkg "${interop_src}/ProGPU.Wpf.Interop.csproj"
  packpkg "${progpu_src}/ProGPU.Wpf.csproj"
  rm -rf ~/.nuget/packages/librewpf.interop ~/.nuget/packages/librewpf.progpu
  for i in "${!progpu_subsidiary_projects[@]}"; do
    proj="${progpu_subsidiary_projects[$i]}"
    name="$(basename "$proj" .csproj)"
    rm -f "${package_output}/${name}.${dev_version}.nupkg"
    packpkg "${proj}"
    rm -rf ~/.nuget/packages/"${progpu_subsidiary_nuget_ids[$i]}"
  done
else
  for proj in "${progpu_subsidiary_projects[@]}"; do
    dir="$(dirname "$proj")"
    rm -rf "$dir/bin/Release" "$dir/obj/Release"
  done
  build_clean "${interop_src}/ProGPU.Wpf.Interop.csproj"
  build_clean "${wpf_src}/PresentationBuildTasks/PresentationBuildTasks.csproj"
  build_clean "${wpf_src}/WindowsBase/WindowsBase.csproj"
  build_clean "${wpf_src}/System.Xaml/System.Xaml.csproj"
  build_clean "${wpf_src}/UIAutomation/UIAutomationTypes/UIAutomationTypes.csproj"
  build_clean "${wpf_src}/UIAutomation/UIAutomationProvider/UIAutomationProvider.csproj"
  build_clean "${wpf_src}/System.Windows.Input.Manipulations/System.Windows.Input.Manipulations.csproj"
  build_clean "${wpf_src}/System.Windows.Primitives/System.Windows.Primitives.csproj"
  build_clean "${wpf_src}/PresentationCore/PresentationCore.csproj"
  build_clean "${wpf_src}/ReachFramework/ReachFramework.csproj"
  build_clean "${wpf_src}/PresentationUI/PresentationUI.csproj"
  build_clean "${wpf_src}/PresentationFramework/PresentationFramework.csproj"
  build_clean "${wpf_src}/Themes/PresentationFramework.Aero/PresentationFramework.Aero.csproj"
  build_clean "${wpf_src}/Themes/PresentationFramework.Aero2/PresentationFramework.Aero2.csproj"
  build_clean "${wpf_src}/Themes/PresentationFramework.AeroLite/PresentationFramework.AeroLite.csproj"
  build_clean "${wpf_src}/Themes/PresentationFramework.Classic/PresentationFramework.Classic.csproj"
  build_clean "${wpf_src}/Themes/PresentationFramework.Fluent/PresentationFramework.Fluent.csproj"
  build_clean "${wpf_src}/Themes/PresentationFramework.Luna/PresentationFramework.Luna.csproj"
  build_clean "${wpf_src}/Themes/PresentationFramework.Royale/PresentationFramework.Royale.csproj"
  build_clean "${wpf_src}/System.Windows.Controls.Ribbon/System.Windows.Controls.Ribbon.csproj"
  build_clean "${progpu_src}/ProGPU.Wpf.csproj"

  rm -f "${package_output}/LibreWPF.Interop.${dev_version}.nupkg" \
        "${package_output}/LibreWPF.ProGPU.${dev_version}.nupkg" \
        "${package_output}/LibreWPF.Transport.${dev_version}.nupkg"
  packpkg "${interop_src}/ProGPU.Wpf.Interop.csproj"
  packpkg "${progpu_src}/ProGPU.Wpf.csproj"
  # Transport packs already-staged content from ArtifactsPackagingDir (populated as a side effect
  # of the projects above building), not its own compiled source -- it's fine with --no-build too.
  packpkg_full "${transport_pack}"

  rm -rf ~/.nuget/packages/librewpf.interop ~/.nuget/packages/librewpf.progpu ~/.nuget/packages/librewpf.transport
  for i in "${!progpu_subsidiary_projects[@]}"; do
    proj="${progpu_subsidiary_projects[$i]}"
    name="$(basename "$proj" .csproj)"
    rm -f "${package_output}/${name}.${dev_version}.nupkg"
    packpkg "${proj}"
    rm -rf ~/.nuget/packages/"${progpu_subsidiary_nuget_ids[$i]}"
  done
fi

"${dotnet}" restore /Users/lextm/uno-tools/OpenDevelop/OpenDevelop.Mvp.slnx --force --no-cache
