using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Designer;
using ICSharpCode.SharpDevelop.LanguageServices.Xaml;
using ICSharpCode.SharpDevelop.Project.Sdk;
using ICSharpCode.WinUIXamlDesigner.SurfaceHost;

namespace ICSharpCode.WinUIXamlDesigner.UnoDesignHost;

/// <summary>What a child that needs more than the common command line wants appended, or why it cannot start.</summary>
sealed record DesignHostChildLaunch(string? Arguments, string? Error);

/// <summary>
/// Registers the Surface backend: the upstream WinUI Surface renderer (microsoft/WinAppVSCE previewer, packaged as
/// LeXtudio.WinUISurface) behind the same designer protocol as the other WinUI children. It is OPT-IN: the
/// registration command only registers it when <c>OD_WINUI_RUNTIME=surface</c>, and it declines (so the registry falls
/// through to the Microsoft child) for any project that is not a Windows App SDK one or when its payload is not deployed.
///
/// Per project it runs the provisioner, which builds (or reuses, from a cache) a host matched to the project's Windows
/// App SDK version and PRI-bearing packages; anything but a verified matched host is reported to the user and no
/// surface is started, so a bundled host is never silently substituted.
///
/// A Surface renders and has no document model: edits stay in the parent's source model, the element tree is empty
/// (no Document Outline), and <c>{x:Bind}</c> shows placeholders. See doc/technotes/winui-designer-landscape.md.
/// </summary>
public static class SurfaceDesignRuntimeHostBootstrap
{
	const string DisplayName = "WinUI Surface host";
	const string OutputChannel = "WinUI Designer";
	/// <summary>Stored boolean (no Options page yet): render x:Class pages from the project's last build. See <c>--compiled-pages</c>.</summary>
	public const string CompiledPagesProperty = "WinUIXamlDesigner.CompiledPages";

	public static void Register() => WinUIXamlRuntimeHostRegistry.Register(Create);

	/// <summary>The deployed child and payload (<c>SurfaceHost\</c> next to this assembly), or null when not deployed.</summary>
	static (string ChildDll, string HostSdk)? Locate()
	{
		var directory = Path.GetDirectoryName(typeof(SurfaceDesignRuntimeHostBootstrap).Assembly.Location);
		if (string.IsNullOrEmpty(directory)) return null;
		var root = Path.Combine(directory, "SurfaceHost");
		var child = Path.Combine(root, "WinUIXamlDesigner.SurfaceHost.Child.dll");
		var hostSdk = Path.Combine(root, "SurfacePayload", "HostSDK");
		return File.Exists(child) && File.Exists(Path.Combine(hostSdk, "engine.stamp")) ? (child, hostSdk) : null;
	}

	public static bool Available => Locate() != null;

	static IWinUIXamlRuntimeHost? Create(XamlFrameworkContext framework, string documentFileName)
	{
		if (framework?.Runtime != XamlRuntimeKind.MicrosoftWinUI || Locate() is not { } layout || !OperatingSystem.IsWindows())
			return null;
		return new UnoDesignRuntimeHost(framework, documentFileName, layout.ChildDll, DisplayName, null,
			(file, report, cancellationToken) => PrepareAsync(layout.HostSdk, file, report, cancellationToken));
	}

	static async Task<DesignHostChildLaunch> PrepareAsync(string hostSdk, string documentFileName, Action<string> report, CancellationToken cancellationToken)
	{
		var channel = DesignerOutput.Channel(OutputChannel);
		void Log(string line)
		{
			DesignerOutput.AppendLine(channel, "Surface: " + line);
			// The provisioner announces its phases ("PHASE: compiling matched host"); show them, since a first build takes minutes.
			var phase = line.IndexOf("PHASE:", StringComparison.Ordinal);
			if (phase >= 0)
				report(DisplayName + ": " + line[(phase + 6)..].Trim() + "…");
		}
		try {
			var project = SD.ProjectService.FindProjectContainingFile(FileName.Create(documentFileName));
			if (project == null)
				return new DesignHostChildLaunch(null, DisplayName + ": '" + documentFileName + "' belongs to no project.");

			// Short, per-user roots: a surface started from a deep path dies with "filename too long".
			var root = Environment.GetEnvironmentVariable("OD_SURFACE_ROOT")
				?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenDevelop", "surf");
			SurfaceLaunch.EnsureShortRoot(root);

			// The provisioner builds with a bare "dotnet", and a designer build may use only the SDK selected in
			// Tools > Options > .NET SDK (the IDE's startup SDK when nothing is selected). Never scan for another one.
			var dotnet = DotNetSdkService.ResolveSdkForProject(project)?.Sdk?.DotnetExecutablePath;
			if (string.IsNullOrEmpty(dotnet))
				return new DesignHostChildLaunch(null, DisplayName + " cannot start: no supported .NET SDK is selected. Select one in Tools > Options > .NET SDK.");
			report(DisplayName + ": provisioning a host matched to " + project.Name + " (the first build takes several minutes, later ones use the cache)…");
			var result = await SurfaceProvisionerRunner.RunAsync(
				new SurfaceProvisionRequest(hostSdk, project.FileName, Path.Combine(root, "cache"), Path.Combine(root, "stg"), dotnet),
				Log, cancellationToken);
			Log($"provisioner: {result.Status}, Windows App SDK {result.DiscoveredVersion ?? "unknown"}, {result.TotalSeconds:0.#}s" + (result.CacheHit ? " (cache hit)" : ""));
			if (!result.IsMatched || result.HostDirectory == null)
				return new DesignHostChildLaunch(null, DisplayName + " cannot start: no matched host for '" + project.Name + "' ("
					+ result.Status + (result.Error != null ? ": " + FirstLine(result.Error) : "") + "). See the '" + OutputChannel + "' output.");

			var arguments = "--host-dir " + Quote(result.HostDirectory) + " --run-root " + Quote(Path.Combine(root, "run"));
			var userDll = project.OutputAssemblyFullPath?.ToString();
			if (!string.IsNullOrEmpty(userDll) && File.Exists(userDll))
				arguments += " --surface-args " + Quote("--user-dll " + Quote(userDll));
			// Opt-in: real {x:Bind} values from the project's last build. OD_WINUI_COMPILED=1 or the stored property.
			if (Environment.GetEnvironmentVariable("OD_WINUI_COMPILED") == "1" || PropertyService.Get(CompiledPagesProperty, false)) {
				arguments += " --compiled-pages";
				Log("compiled pages are on: pages with x:Class render from the last build of " + project.Name
					+ " (the project must set WindowsAppSdkBootstrapInitialize=false and WindowsAppSdkUndockedRegFreeWinRTInitialize=false)");
			}
			return new DesignHostChildLaunch(arguments, null);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception e) {
			LoggingService.Warn("WinUI Surface host preparation failed", e);
			return new DesignHostChildLaunch(null, DisplayName + " cannot start: " + e.GetBaseException().Message);
		}
	}

	static string FirstLine(string text)
	{
		var index = text.IndexOfAny(new[] { '\r', '\n' });
		return index < 0 ? text : text[..index];
	}

	/// <summary>Quotes one command-line argument the way <c>DesignerHostLaunchSpec</c> does.</summary>
	static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
