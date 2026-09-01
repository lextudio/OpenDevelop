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
	const string MicrosoftWinUIBootstrap =
		"ICSharpCode.WinUIXamlDesigner.UnoDesignHost.MicrosoftWinUIDesignRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.UnoDesignHost";

	public override void Run()
	{
		var selection = Environment.GetEnvironmentVariable(RuntimeSelectionVariable)?.Trim();

		if (!string.IsNullOrEmpty(selection) && !string.Equals(selection, "progpu", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(selection, "uno", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(selection, "microsoft", StringComparison.OrdinalIgnoreCase)) {
			LoggingService.Warn(
				$"Ignoring unrecognized {RuntimeSelectionVariable}='{selection}' (expected 'microsoft', 'uno' or 'progpu'); using the default runtime order.");
		}

		// The preference only orders compatible implementations. All three factories remain
		// registered so documents from another runtime family retain their correct host.
		if (string.Equals(selection, "progpu", StringComparison.OrdinalIgnoreCase)) {
			Register(UnoBootstrap);
			Register(MicrosoftWinUIBootstrap);
			Register(ProGpuBootstrap);
			return;
		}
		if (string.Equals(selection, "uno", StringComparison.OrdinalIgnoreCase)) {
			Register(ProGpuBootstrap);
			Register(MicrosoftWinUIBootstrap);
			Register(UnoBootstrap);
			return;
		}

		// Always register every runtime family. Each factory declines projects outside its
		// evaluated runtime family, so a process-wide test/debug preference can never route an
		// Uno project to the Microsoft child (or a Microsoft WinUI project to Uno).
		Register(ProGpuBootstrap);
		Register(UnoBootstrap);
		Register(MicrosoftWinUIBootstrap);
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
