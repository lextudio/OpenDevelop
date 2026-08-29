using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Threading;
using ICSharpCode.SharpDevelop.Designer.Remote;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamJsonRpc;

namespace ICSharpCode.WinUIXamlDesigner.UnoHost
{
	class Program
	{
		static int Main(string[] args)
		{
			InstallOwnDependencyResolver();
			return Run(args);
		}

		/// <summary>
		/// The JIT resolves this method's assembly references (StreamJsonRpc) lazily on its
		/// first call - after the resolver hook below is installed, unlike Main itself, whose
		/// body would be JITted before a single line could run.
		/// </summary>
		static void InstallOwnDependencyResolver()
		{
			// AppContext.BaseDirectory points at the deps.json's location when running inside
			// the designed project's dependency graph - the host's own deployment is where the
			// host assembly itself lives.
			var ownDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
			AssemblyLoadContext.Default.Resolving += (_, name) =>
			{
				var candidate = Path.Combine(ownDir, name.Name + ".dll");
				return File.Exists(candidate)
					? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
					: null;
			};
		}

		static int Run(string[] args)
		{
			var appBin = ParseArgument(args, "--appbin");

			HeadlessDispatcher.Install();

			PreloadProjectAssemblies(appBin);

			return DesignerChildHost.Run(args, "UnoDesignHost", DesignRpc.RegisterRpcMethods,
				HeadlessDispatcher.Run, DesignRpc.Shutdown,
				afterConnect: () => Application.Start(args2 => _ = new HostApp()));
		}

		static string ParseArgument(string[] args, string name)
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
		/// (which scans the loaded assemblies) can find the project's custom controls,
		/// converters and library types. Runs after the dependency resolver hook so the loads
		/// resolve through the project's deps; assemblies that fail to load are skipped.
		/// </summary>
		static void PreloadProjectAssemblies(string appBin)
		{
			if (string.IsNullOrEmpty(appBin) || !Directory.Exists(appBin))
			{
				return;
			}
			foreach (var dll in Directory.EnumerateFiles(appBin, "*.dll"))
			{
				try
				{
					AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
				}
				catch
				{
					// Not a loadable managed assembly (native lib, incompatible build) - skip.
				}
			}
		}
	}

	class HostApp : Application
	{
		public HostApp()
		{
			Resources.MergedDictionaries.Add(new XamlControlsResources());
		}
	}
}
