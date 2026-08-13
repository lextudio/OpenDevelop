#!/usr/bin/env python3
"""
Patches a .deps.json to add the real LibreWinForms.System.Windows.Forms/.WindowsFormsIntegration
runtime assets that NuGet/RAR's conflict resolution drops from deps.json generation, because it
picks the ref-pack-provided "Microsoft.WindowsDesktop.App" shared-framework component as the
winner for System.Windows.Forms/WindowsFormsIntegration - a component that doesn't exist at all
off Windows, causing FileNotFoundException at the ProGpuWpfSdkPortableBootstrap module initializer
(the first thing that touches WindowsFormsHost) long before AddInTree even loads.

The DLLs themselves are already copied into the output directory correctly by
ProGPU.Wpf.Sdk.targets' own _ProGpuWpfSdkCopyPortableWinFormsCompatRuntimeAssets target - only
the deps.json bookkeeping that the CoreCLR host uses to decide what's *allowed* to load is
missing/wrong. See Directory.Build.targets' _ReplaceWindowsDesktopRefPackWinFormsFacades for the
matching *compile-time* half of this fix.
"""
import glob
import json
import os
import shutil
import sys


def find_package_version(nuget_package_root, package_id_lower):
    pattern = os.path.join(nuget_package_root, package_id_lower, "*")
    candidates = [d for d in glob.glob(pattern) if os.path.isdir(d)]
    if not candidates:
        return None
    # Prefer the newest by mtime - there should only ever be one version installed anyway.
    candidates.sort(key=os.path.getmtime, reverse=True)
    return os.path.basename(candidates[0])


def main():
    deps_path, nuget_package_root = sys.argv[1], sys.argv[2]

    sysforms_pkg_id = "LibreWinForms.System.Windows.Forms"
    winint_pkg_id = "LibreWinForms.WindowsFormsIntegration"
    progpudrawing_pkg_id = "ProGPU.System.Drawing.Common"
    transport_pkg_id = "LibreWPF.Transport"

    sysforms_version = find_package_version(nuget_package_root, sysforms_pkg_id.lower())
    if sysforms_version is None:
        print(f"patch-librewinforms-deps.py: {sysforms_pkg_id} not found under {nuget_package_root}, skipping")
        return

    # WindowsFormsIntegration is optional - not every project that pulls in System.Windows.Forms
    # also uses WindowsFormsHost. Only add its entry when the package is actually installed.
    winint_version = find_package_version(nuget_package_root, winint_pkg_id.lower())
    progpudrawing_version = find_package_version(nuget_package_root, progpudrawing_pkg_id.lower())
    transport_version = find_package_version(nuget_package_root, transport_pkg_id.lower())

    with open(deps_path, "r", encoding="utf-8") as f:
        deps = json.load(f)

    sysforms_key = f"{sysforms_pkg_id}/{sysforms_version}"
    winint_key = f"{winint_pkg_id}/{winint_version}" if winint_version else None
    progpudrawing_key = f"{progpudrawing_pkg_id}/{progpudrawing_version}" if progpudrawing_version else None

    for tfm, libs in deps.get("targets", {}).items():
        if sysforms_key not in libs:
            libs[sysforms_key] = {}
        libs[sysforms_key].setdefault("runtime", {})["lib/net10.0/System.Windows.Forms.dll"] = {}

        if winint_key is not None:
            # RAR/GenerateDepsFile conflict resolution can drop this package from deps.json's
            # targets/libraries bookkeeping entirely (even though it resolves fine in
            # project.assets.json and its DLL is copied to the output dir) once another project in
            # the graph pins a package version LibreWinForms.WindowsFormsIntegration transitively
            # depends on (e.g. ProGPU.System.Drawing.Common) - the CoreCLR host then refuses to
            # load the DLL at all ("cannot find the file specified") because deps.json says it
            # isn't allowed to. Ensure the entry unconditionally rather than only patching an
            # existing one.
            if winint_key not in libs:
                libs[winint_key] = {}
            libs[winint_key].setdefault("runtime", {})["lib/net10.0/WindowsFormsIntegration.dll"] = {}

        if progpudrawing_key is not None:
            # Same conflict-resolution bug, different symptom: ProGPU.System.Drawing.Common's
            # RID-specific target entry survives in deps.json but with only a "dependencies"
            # object and no "runtime" object, so the CoreCLR host won't load its DLL even though
            # the file is physically present and copied to the output dir (WindowsFormsHost's own
            # module initializer needs it at ProGpuWpfSdkPortableBootstrap.Initialize() time).
            libs.setdefault(progpudrawing_key, {}).setdefault("runtime", {})[
                "lib/net10.0/System.Drawing.Common.dll"
            ] = {}

    libraries = deps.setdefault("libraries", {})
    if sysforms_key not in libraries:
        libraries[sysforms_key] = {
            "type": "package",
            "serviceable": True,
            "sha512": "",
        }
    if winint_key is not None and winint_key not in libraries:
        libraries[winint_key] = {
            "type": "package",
            "serviceable": True,
            "sha512": "",
        }
    if progpudrawing_key is not None and progpudrawing_key not in libraries:
        libraries[progpudrawing_key] = {
            "type": "package",
            "serviceable": True,
            "sha512": "",
        }

    with open(deps_path, "w", encoding="utf-8") as f:
        json.dump(deps, f, indent=2)
        f.write("\n")

    # Keep the physical deployment beside the dependency manifest in sync with
    # the entries above. Framework conflict resolution can remove these package
    # files from both RuntimeCopyLocalItems and a RID-less PublishDir.
    output_dir = os.path.dirname(deps_path)
    runtime_assets = [
        (sysforms_pkg_id.lower(), sysforms_version, "System.Windows.Forms.dll"),
        (winint_pkg_id.lower(), winint_version, "WindowsFormsIntegration.dll"),
        (progpudrawing_pkg_id.lower(), progpudrawing_version, "System.Drawing.Common.dll"),
    ]
    for package_id, version, filename in runtime_assets:
        if version is None:
            continue
        source = os.path.join(nuget_package_root, package_id, version, "lib", "net10.0", filename)
        if os.path.isfile(source):
            shutil.copy2(source, os.path.join(output_dir, filename))

    # LibreWPF.Transport has parallel ref/ and lib/ trees with identical file
    # names. A later solution build can copy reference assemblies over an
    # already-published host. Restore the complete executable transport payload
    # from lib/ after all builds have finished.
    if transport_version is not None:
        transport_runtime = os.path.join(
            nuget_package_root, transport_pkg_id.lower(), transport_version, "lib", "net10.0"
        )
        for source in glob.glob(os.path.join(transport_runtime, "*.dll")):
            shutil.copy2(source, os.path.join(output_dir, os.path.basename(source)))


    print(f"patch-librewinforms-deps.py: patched {deps_path} ({sysforms_key}"
          + (f", {winint_key}" if winint_key else "")
          + (f", {progpudrawing_key}" if progpudrawing_key else "") + ")")


if __name__ == "__main__":
    main()
