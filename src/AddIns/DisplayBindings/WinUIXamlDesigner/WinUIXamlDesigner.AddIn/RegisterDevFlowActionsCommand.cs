using System;
using System.Reflection;
using ICSharpCode.Core;
namespace ICSharpCode.WinUIXamlDesigner;
public sealed class RegisterDevFlowActionsCommand : AbstractCommand
{
	/// <summary>
	/// Selects which WinUI runtime host backs the designer: <c>uno</c> (out-of-process Uno
	/// child), <c>progpu</c> (in-process ProGPU compiled-WinUI pipeline), or unset for the
	/// default order. Without it the registry simply prefers whichever factory registered last
	/// and silently falls back, which makes "which backend did this test actually exercise?"
	/// depend on whether the Uno child binary happens to be deployed - fine at runtime, useless
	/// for a test that means to pin one backend. Follows the repo's existing OD_TEST_MODE /
	/// UNO_DESIGN_DPI environment-override convention.
	/// </summary>
	public const string RuntimeSelectionVariable = "OD_WINUI_RUNTIME";

	const string ProGpuBootstrap =
		"ICSharpCode.WinUIXamlDesigner.ProGPUHost.ProGpuRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.ProGPUHost";
	const string UnoBootstrap =
		"ICSharpCode.WinUIXamlDesigner.UnoDesignHost.UnoDesignRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.UnoDesignHost";

	public override void Run()
	{
		var selection = Environment.GetEnvironmentVariable(RuntimeSelectionVariable)?.Trim();

		// The registry tries factories in reverse registration order, so whatever is registered
		// LAST wins and anything registered before it becomes the fallback.
		if (string.Equals(selection, "progpu", StringComparison.OrdinalIgnoreCase)) {
			// ProGPU only: no Uno factory registered at all, so a missing/blocked ProGPU host
			// surfaces as a real failure instead of silently falling through to Uno.
			Register(ProGpuBootstrap);
			return;
		}

		if (string.Equals(selection, "uno", StringComparison.OrdinalIgnoreCase)) {
			Register(UnoBootstrap);
			return;
		}

		if (!string.IsNullOrEmpty(selection)) {
			LoggingService.Warn(
				$"Ignoring unrecognized {RuntimeSelectionVariable}='{selection}' (expected 'uno' or 'progpu'); using the default runtime order.");
		}

		// Default: ProGPU first, then the out-of-process Uno host - the registry tries factories
		// in reverse registration order, so the Uno host becomes the default surface and ProGPU
		// stays as the fallback when its child binary is not deployed.
		Register(ProGpuBootstrap);
		Register(UnoBootstrap);
	}

	static void Register(string bootstrapTypeName)
	{
		var type = Type.GetType(bootstrapTypeName, throwOnError: false);
		if (type == null) {
			LoggingService.Warn($"WinUI designer runtime bootstrap not found: {bootstrapTypeName}");
			return;
		}
		type.GetMethod("Register", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
	}
}
