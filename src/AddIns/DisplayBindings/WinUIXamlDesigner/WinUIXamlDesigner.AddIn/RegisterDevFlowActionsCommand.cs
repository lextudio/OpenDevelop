using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;
namespace ICSharpCode.WinUIXamlDesigner;
public sealed class RegisterDevFlowActionsCommand : AbstractCommand
{
	/// <summary>
	/// Selects which WinUI runtime host backs the designer: <c>uno</c> (out-of-process Uno
	/// child), <c>microsoft</c> (out-of-process Windows App SDK child), <c>surface</c> (the
	/// upstream WinUI Surface renderer behind a provisioned, version-matched host; the default for
	/// Microsoft WinUI projects, see SurfaceDesignRuntimeHostBootstrap), or unset for that default.
	/// (The in-process ProGPU host is gone: its XAML compiler packages were
	/// removed upstream, and <c>progpu</c> is now an unrecognized value.) Without it the registry
	/// simply prefers whichever factory registered last
	/// and silently falls back, which makes "which backend did this test actually exercise?"
	/// depend on whether the Uno child binary happens to be deployed - fine at runtime, useless
	/// for a test that means to pin one backend. Follows the repo's existing OD_TEST_MODE /
	/// UNO_DESIGN_DPI environment-override convention.
	/// </summary>
	public const string RuntimeSelectionVariable = "OD_WINUI_RUNTIME";

	/// <summary>The persisted counterpart of <see cref="RuntimeSelectionVariable"/> (a string property: <c>uno</c>, <c>microsoft</c> or
	/// <c>surface</c>), used when the variable is not set.</summary>
	public const string RuntimePreferenceProperty = "WinUIXamlDesigner.Runtime";
	/// <summary>The Microsoft WinUI default. Surface declines unavailable payloads and non-Microsoft
	/// projects, so the legacy Microsoft child remains the automatic compatibility fallback.</summary>
	public const string DefaultRuntimePreference = WinUIRuntimeSelection.DefaultRuntime;

	const string UnoBootstrap =
		"ICSharpCode.WinUIXamlDesigner.UnoDesignHost.UnoDesignRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.UnoDesignHost";
	const string MicrosoftWinUIBootstrap =
		"ICSharpCode.WinUIXamlDesigner.UnoDesignHost.MicrosoftWinUIDesignRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.UnoDesignHost";
	const string SurfaceBootstrap =
		"ICSharpCode.WinUIXamlDesigner.UnoDesignHost.SurfaceDesignRuntimeHostBootstrap, ICSharpCode.WinUIXamlDesigner.UnoDesignHost";

	public override void Run()
	{
		// The environment variable wins (tests pin a backend with it); otherwise the stored preference, then the default.
		// Same values, read once at start-up.
		var selection = WinUIRuntimeSelection.Resolve(
			Environment.GetEnvironmentVariable(RuntimeSelectionVariable),
			PropertyService.Get(RuntimePreferenceProperty, DefaultRuntimePreference),
			out var unrecognized);
		if (unrecognized != null) {
			LoggingService.Warn(
				$"Ignoring unrecognized {RuntimeSelectionVariable}='{unrecognized}' (expected 'microsoft', 'surface' or 'uno'); using '{selection}'.");
		}

		// The upstream WinUI Surface is the default renderer for Windows App SDK projects. Registered last so it is
		// tried first; it declines other runtime families and any machine where its payload is not deployed, and the
		// Microsoft child stays registered behind it.
		if (string.Equals(selection, "surface", StringComparison.OrdinalIgnoreCase)) {
			Register(UnoBootstrap);
			Register(MicrosoftWinUIBootstrap);
			Register(SurfaceBootstrap);
			return;
		}

		// The preference only orders compatible implementations. Both factories remain
		// registered so documents from another runtime family retain their correct host.
		if (string.Equals(selection, "uno", StringComparison.OrdinalIgnoreCase)) {
			Register(MicrosoftWinUIBootstrap);
			Register(UnoBootstrap);
			return;
		}

		// Always register every runtime family. Each factory declines projects outside its
		// evaluated runtime family, so a process-wide test/debug preference can never route an
		// Uno project to the Microsoft child (or a Microsoft WinUI project to Uno).
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

/// <summary>Tools &gt; Options &gt; Experimental Features. This selects only the Microsoft WinUI renderer;
/// Uno projects always route to their dedicated Uno host.</summary>
public sealed class WinUIXamlDesignerOptions : OptionPanel
{
	const string Surface = "surface";
	const string Microsoft = "microsoft";
	readonly ComboBox runtime = new() {
		MinWidth = 440,
		HorizontalAlignment = HorizontalAlignment.Left,
		DisplayMemberPath = "Label",
		SelectedValuePath = "Value"
	};

	public WinUIXamlDesignerOptions()
	{
		runtime.ItemsSource = new[] {
			new RuntimeOption(Surface, "Experimental Surface host (recommended)"),
			new RuntimeOption(Microsoft, "Microsoft WinUI host (compatibility fallback)")
		};
		var selected = PropertyService.Get(RegisterDevFlowActionsCommand.RuntimePreferenceProperty,
			RegisterDevFlowActionsCommand.DefaultRuntimePreference)?.Trim();
		runtime.SelectedValue = WinUIRuntimeSelection.ToOptionsChoice(selected);

		var panel = new StackPanel { Margin = new Thickness(8) };
		panel.Children.Add(new TextBlock {
			Text = "Experimental Microsoft WinUI renderer",
			Margin = new Thickness(0, 0, 0, 4)
		});
		panel.Children.Add(runtime);
		panel.Children.Add(new TextBlock {
			Text = "The Surface host is experimental and uses a version-matched WinAppVSCE previewer. "
				+ "If it is unavailable, OpenDevelop automatically falls back to the Microsoft host. "
				+ "Restart OpenDevelop after changing this setting. OD_WINUI_RUNTIME overrides it for tests and diagnostics.",
			TextWrapping = TextWrapping.Wrap,
			MaxWidth = 560,
			Margin = new Thickness(0, 10, 0, 0)
		});
		Content = panel;
	}

	public override bool SaveOptions()
	{
		// Leave the stored value alone unless the choice changed: the page cannot show "uno", and must not overwrite it.
		var stored = PropertyService.Get(RegisterDevFlowActionsCommand.RuntimePreferenceProperty,
			RegisterDevFlowActionsCommand.DefaultRuntimePreference);
		var toStore = WinUIRuntimeSelection.ValueToStore(stored, runtime.SelectedValue as string ?? Surface);
		if (toStore != null)
			PropertyService.Set(RegisterDevFlowActionsCommand.RuntimePreferenceProperty, toStore);
		return base.SaveOptions();
	}

	sealed record RuntimeOption(string Value, string Label);
}
