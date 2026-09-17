using System;
using System.IO;
using System.Text.Json;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>
/// Registers the out-of-process Uno host as the design surface runtime. Declining (null)
/// when the child binary is not deployed lets the registry fall through to ProGPU.
/// </summary>
public static class UnoDesignRuntimeHostBootstrap
{
	public static void Register() => WinUIXamlRuntimeHostRegistry.Register(Create);

	public static bool ChildAvailable => UnoDesignClient.LocateChildDll() != null;
	public static string ChildPath => UnoDesignClient.LocateChildDll() ?? "";

	static IWinUIXamlRuntimeHost Create(XamlFrameworkContext framework, string documentFileName) =>
		framework?.Runtime == XamlRuntimeKind.Uno && ChildAvailable
			? new UnoDesignRuntimeHost(framework, documentFileName) : null;
}

/// <summary>
/// Registers the Microsoft WinUI 3 child for Windows App SDK projects.  Its protocol and WPF
/// presentation shell are shared with the Uno child; selecting it here is what keeps a native
/// WinUI document from silently being rendered by the Uno compatibility host.
/// </summary>
public static class MicrosoftWinUIDesignRuntimeHostBootstrap
{
	public static void Register() => WinUIXamlRuntimeHostRegistry.Register(Create);

	/// <summary>Default (current-runtime) child path, retained for callers that do not have a
	/// document yet. Actual designer creation uses <see cref="LocateChildDll"/> so it can match
	/// the designed app's runtimeconfig.</summary>
	public static string? ChildPath => LocateChildDll(null, null);

	static IWinUIXamlRuntimeHost? Create(XamlFrameworkContext framework, string documentFileName)
	{
		return framework?.Runtime == XamlRuntimeKind.MicrosoftWinUI && LocateChildDll(documentFileName, null) != null
			? new UnoDesignRuntimeHost(framework, documentFileName, null, "WinUI design host",
				targetArchitecture => LocateChildDll(documentFileName, targetArchitecture))
			: null;
	}

	/// <summary>
	/// Selects the child compiled for the CLR major named by the built application's
	/// runtimeconfig.  Passing an app's deps/runtimeconfig to a host compiled for a newer CLR
	/// major fails before Main; preloading the app without the graph can instead enter native WinUI
	/// with incompatible generated XAML metadata.  Both cases are avoided by keeping host slices
	/// side by side.
	/// </summary>
	/// <param name="targetArchitecture">The architecture of the "dotnet" that will actually launch
	/// this child (see UnoDesignRuntimeHost.CanHostRunOnAppArchitecture/DotnetHostPath) - null when
	/// it matches this IDE process's own. This host is one of the few AddIn components NOT built
	/// AnyCPU (see doc/technotes/addin-sdk.md): it hosts the real Windows App SDK/WinUI3 runtime,
	/// whose native WinRT/COM interop is architecture-specific, so dist.ps1 ships a win-x64 AND a
	/// win-arm64 build side by side. Selecting by THIS PROCESS's architecture instead of the
	/// launching dotnet's is exactly backwards once those two can differ (a cross-architecture
	/// preview): it loads a win-x64 host DLL through an ARM64 "dotnet exec", which fails before
	/// Main with "the assembly architecture is not compatible with the current process
	/// architecture" - a plain FileLoadException, since node reuse/RID selection happens at the
	/// native host level, not in managed code that could report anything more specific.</param>
	static string? LocateChildDll(string? documentFileName, System.Runtime.InteropServices.Architecture? targetArchitecture)
	{
		var directory = Path.GetDirectoryName(typeof(UnoDesignRuntimeHost).Assembly.Location);
		if (string.IsNullOrEmpty(directory)) return null;
		var root = Path.Combine(directory, "MicrosoftHost");
		var runtime = RuntimeDirectoryFor(documentFileName);
		var appSdk = WindowsAppSdkVersionFor(documentFileName);
		var architecture = ArchitectureFolder(targetArchitecture);
		// A platform-neutral distribution carries one child per architecture under
		// <CLR major>\<rid>\; prefer the one matching the launching dotnet before the flat fallbacks.
		if (architecture != null) {
			if (!string.IsNullOrEmpty(appSdk)) {
				var compatibleArch = Path.Combine(root, runtime + "-windowsappsdk" + appSdk, architecture,
					"WinUIXamlDesigner.MicrosoftHost.dll");
				if (File.Exists(compatibleArch)) return compatibleArch;
			}
			var archCandidate = Path.Combine(root, runtime, architecture, "WinUIXamlDesigner.MicrosoftHost.dll");
			if (File.Exists(archCandidate)) return archCandidate;
		}
		if (!string.IsNullOrEmpty(appSdk)) {
			var compatible = Path.Combine(root, runtime + "-windowsappsdk" + appSdk,
				"WinUIXamlDesigner.MicrosoftHost.dll");
			if (File.Exists(compatible)) return compatible;
		}
		var candidate = Path.Combine(root, runtime, "WinUIXamlDesigner.MicrosoftHost.dll");
		if (File.Exists(candidate)) return candidate;

		// Existing deployments had one net10 child directly under MicrosoftHost. Preserve that
		// layout as a backwards-compatible fallback for an incremental add-in update.
		candidate = Path.Combine(root, "WinUIXamlDesigner.MicrosoftHost.dll");
		return File.Exists(candidate) ? candidate : null;
	}

	/// <summary>RID folder for the given architecture ("win-x64"/"win-arm64"/"win-x86"), or this IDE
	/// process's own when null - the correct default for every call site except the one that has
	/// already resolved a different-architecture "dotnet" to actually launch the child under.</summary>
	static string? ArchitectureFolder(System.Runtime.InteropServices.Architecture? architecture)
	{
		return (architecture ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) switch {
			System.Runtime.InteropServices.Architecture.X64 => "win-x64",
			System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
			System.Runtime.InteropServices.Architecture.X86 => "win-x86",
			_ => null
		};
	}

	static string RuntimeDirectoryFor(string? documentFileName)
	{
		if (!string.IsNullOrEmpty(documentFileName)) {
			try {
				var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(documentFileName));
				var output = project?.OutputAssemblyFullPath;
				var runtimeConfig = string.IsNullOrEmpty(output) ? null : Path.ChangeExtension(output, ".runtimeconfig.json");
				if (!string.IsNullOrEmpty(runtimeConfig) && File.Exists(runtimeConfig)) {
					using var json = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
					if (json.RootElement.TryGetProperty("runtimeOptions", out var options)
						&& options.TryGetProperty("tfm", out var tfm)) {
						var value = tfm.GetString();
						if (value?.StartsWith("net9.", StringComparison.OrdinalIgnoreCase) == true) return "net9.0";
					}
				}
			} catch { /* A missing/stale project evaluation falls back to the IDE's net10 child. */ }
		}
		return "net10.0";
	}

	/// <summary>
	/// Reads the designed output's dependency graph instead of guessing from its native DLL file
	/// versions.  Windows App SDK's bootstrap chooses the dynamic dependency matching the host's
	/// compile-time package reference; a 2.4 child therefore cannot safely load a Gallery built
	/// against 2.1 even when both target the same CLR.  A version-specific child is preferred when
	/// deployed, with the ordinary CLR-only child retained as a compatible fallback.
	/// </summary>
	static string? WindowsAppSdkVersionFor(string? documentFileName)
	{
		if (string.IsNullOrEmpty(documentFileName)) return null;
		try {
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(documentFileName));
			var output = project?.OutputAssemblyFullPath;
			var deps = string.IsNullOrEmpty(output) ? null : Path.ChangeExtension(output, ".deps.json");
			if (string.IsNullOrEmpty(deps) || !File.Exists(deps)) return null;
			using var json = JsonDocument.Parse(File.ReadAllText(deps));
			if (!json.RootElement.TryGetProperty("libraries", out var libraries)) return null;
			foreach (var library in libraries.EnumerateObject()) {
				const string prefix = "Microsoft.WindowsAppSDK/";
				if (library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					return library.Name.Substring(prefix.Length);
			}
		} catch { /* Stale output merely falls back to the current add-in child. */ }
		return null;
	}
}
