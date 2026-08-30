// Child-process bootstrap helpers shared by BOTH WinUI hosts.
//
// Extracted from UnoHost/Program.cs for the same reason DesignRpc.cs was: none of it is
// Uno-specific. This is what lets a design child run INSIDE the designed application's dependency
// graph - the client launches it as `dotnet exec --runtimeconfig <app>.runtimeconfig.json
// --depsfile <app>.deps.json <host>.dll --port N --token T --appbin <app output dir>` - so
// XamlReader can resolve the app's own controls, converters and library types.
//
// Without this a designer opens almost nothing real: a corpus run over WinUI-Gallery rendered
// 9 of 187 pages, because nearly every page references a local: type from the app's own assembly.

using System;
using System.IO;
using System.Runtime.Loader;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	static class HostBootstrap
	{
		/// <summary>
		/// Must be called from Main's first line, and Main must do nothing else inline: the JIT
		/// resolves a method's assembly references (StreamJsonRpc) when that method is first
		/// called, so any code sharing Main's body would be JITted - and fail to resolve - before
		/// this hook could be installed.
		/// </summary>
		public static void InstallOwnDependencyResolver()
		{
			// AppContext.BaseDirectory points at the deps.json's location when running inside the
			// designed project's dependency graph - the host's own deployment is where the host
			// assembly itself lives.
			var ownDir = Path.GetDirectoryName(typeof(HostBootstrap).Assembly.Location)!;
			AssemblyLoadContext.Default.Resolving += (_, name) =>
			{
				var candidate = Path.Combine(ownDir, name.Name + ".dll");
				return File.Exists(candidate)
					? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
					: null;
			};
		}

		public static string? ParseArgument(string[] args, string name)
		{
			for (var i = 0; i < args.Length - 1; i++)
			{
				if (args[i] == name)
				{
					return args[i + 1];
				}
			}
			return null;
		}

		/// <summary>
		/// Preloads the designed project's output assemblies so XamlReader's type resolution
		/// (which scans the loaded assemblies) can find the project's custom controls, converters
		/// and library types. Runs after the dependency resolver hook so the loads resolve through
		/// the project's deps; assemblies that fail to load are skipped.
		/// </summary>
		public static void PreloadProjectAssemblies(string? appBin)
		{
			if (string.IsNullOrEmpty(appBin) || !Directory.Exists(appBin))
			{
				return;
			}
			var loaded = 0;
			var skipped = 0;
			string? firstFailure = null;
			foreach (var dll in Directory.EnumerateFiles(appBin, "*.dll"))
			{
				try
				{
					AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
					loaded++;
				}
				catch (Exception e)
				{
					// Not a loadable managed assembly (native lib, incompatible build) - skip.
					skipped++;
					firstFailure ??= $"{Path.GetFileName(dll)}: {e.GetBaseException().Message}";
				}
			}
			// Reported because a silent preload makes the most common designer failure - "the app's
			// own types do not resolve" - indistinguishable from a XAML authoring error.
			Console.Error.WriteLine($"design-host: preloaded {loaded} assemblies from {appBin} ({skipped} skipped)"
				+ (firstFailure is null ? "" : $"; first skip: {firstFailure}"));
		}
	}
}
