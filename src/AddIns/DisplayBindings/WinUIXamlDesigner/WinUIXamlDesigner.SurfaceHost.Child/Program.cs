using ICSharpCode.SharpDevelop.Designer.Remote;

namespace ICSharpCode.WinUIXamlDesigner.SurfaceHost.Child;

static class Program
{
	static int Main(string[] args)
	{
		string? surfaceExe = Argument(args, "--surface-exe");
		string? hostDirectory = Argument(args, "--host-dir");
		string? runRoot = Argument(args, "--run-root");
		if (string.IsNullOrWhiteSpace(surfaceExe) == string.IsNullOrWhiteSpace(hostDirectory)
			|| (!string.IsNullOrWhiteSpace(hostDirectory) && string.IsNullOrWhiteSpace(runRoot))) {
			Console.Error.WriteLine($"{SurfaceDesignRpc.Runtime}: pass either --surface-exe, or --host-dir together with --run-root.");
			return 2;
		}
		var rpc = new SurfaceDesignRpc(surfaceExe, Argument(args, "--surface-args") ?? "", hostDirectory, runRoot);
		// DesignerChildHost owns the transport, ready line and exit-code policy; a vanished parent ends the
		// wait so no surface process outlives the IDE.
		return DesignerChildHost.Run(args, SurfaceDesignRpc.Runtime, rpc.Register, rpc.WaitForShutdown,
			onParentDisconnected: rpc.RequestShutdown, afterShutdown: rpc.CloseAll);
	}

	static string? Argument(string[] args, string name)
	{
		for (int i = 0; i < args.Length - 1; i++)
			if (string.Equals(args[i], name, StringComparison.Ordinal))
				return args[i + 1];
		return null;
	}
}
