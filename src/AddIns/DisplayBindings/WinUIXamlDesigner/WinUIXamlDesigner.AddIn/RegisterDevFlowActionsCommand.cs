using System;
using System.Reflection;
using ICSharpCode.Core;
namespace ICSharpCode.WinUIXamlDesigner;
public sealed class RegisterDevFlowActionsCommand : AbstractCommand
{
	public override void Run()
	{
		// ProGPU first, then the out-of-process Uno host: the registry tries factories in
		// reverse registration order, so the Uno host becomes the default surface and
		// ProGPU stays as the fallback when its child binary is not deployed.
		var proGpu = Type.GetType(
			"ICSharpCode.WinUIXamlDesigner.ProGPUHost.ProGpuRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.ProGPUHost",
			throwOnError: false);
		proGpu?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

		var uno = Type.GetType(
			"ICSharpCode.WinUIXamlDesigner.UnoDesignHost.UnoDesignRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.UnoDesignHost",
			throwOnError: false);
		uno?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
	}
}
