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
	/// <summary>The Microsoft WinUI default. There is no fallback from the Surface host to another host: when it cannot be used the designer
	/// reports why (see <see cref="WinUIRuntimeSelection.HostsToRegister"/>).</summary>
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

		// Each factory accepts only documents of its own runtime family (Uno projects the Uno host, Microsoft WinUI projects the Surface or the
		// Microsoft host), so a process-wide preference can never route an Uno project to a Microsoft host. Which hosts are registered, and in
		// what order, is WinUIRuntimeSelection.HostsToRegister: the default (surface) registers no Microsoft host, so a Surface host that
		// cannot be used is reported, never silently replaced.
		foreach (var host in WinUIRuntimeSelection.HostsToRegister(selection.ToLowerInvariant())) {
			Register(host switch {
				"surface" => SurfaceBootstrap,
				"microsoft" => MicrosoftWinUIBootstrap,
				_ => UnoBootstrap
			});
		}
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
			new RuntimeOption(Microsoft, "Microsoft WinUI host (legacy)")
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
				+ "If the Surface host cannot be used (its files are not installed, or it fails to start), the designer reports the error; "
				+ "OpenDevelop never switches to the Microsoft host by itself. Choose the Microsoft host here to use it. "
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
