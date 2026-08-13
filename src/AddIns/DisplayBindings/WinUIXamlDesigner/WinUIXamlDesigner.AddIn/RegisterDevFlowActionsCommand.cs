using System;
using System.Reflection;
using ICSharpCode.Core;
namespace ICSharpCode.WinUIXamlDesigner;
public sealed class RegisterDevFlowActionsCommand : AbstractCommand
{
	public override void Run()
	{
		var bootstrap = Type.GetType(
			"ICSharpCode.WinUIXamlDesigner.ProGPUHost.ProGpuRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.ProGPUHost",
			throwOnError: false);
		bootstrap?.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
	}
}
