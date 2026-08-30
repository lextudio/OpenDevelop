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
			HostBootstrap.InstallOwnDependencyResolver();
			return Run(args);
		}


		static int Run(string[] args)
		{
			var appBin = HostBootstrap.ParseArgument(args, "--appbin");

			HeadlessDispatcher.Install();

			HostBootstrap.PreloadProjectAssemblies(appBin);

			return DesignerChildHost.Run(args, "UnoDesignHost", DesignRpc.RegisterRpcMethods,
				HeadlessDispatcher.Run, DesignRpc.Shutdown,
				afterConnect: () => Application.Start(args2 => _ = new HostApp()));
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
