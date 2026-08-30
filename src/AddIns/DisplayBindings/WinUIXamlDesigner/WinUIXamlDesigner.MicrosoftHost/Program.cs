using ICSharpCode.SharpDevelop.Designer.Remote;
using ICSharpCode.WinUIXamlDesigner.UnoHost;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.Graphics;

namespace ICSharpCode.WinUIXamlDesigner.MicrosoftHost;

// The Microsoft WinUI 3 child host. Only the BOOTSTRAP lives here - the designer implementation
// (DesignHost.cs) and the whole 19-method DDP surface (DesignRpc.cs) are source-linked from the
// Uno host and compile unchanged, because both target the same Microsoft.UI.Xaml API.
//
// The bootstrap is what genuinely differs. Uno installs its own headless pump and can therefore
// call Application.Start from a callback, but WinUI's Application.Start OWNS the calling thread:
// it does not return until the app exits. So the DDP wait loop has to move to a worker while the
// UI thread stays inside Application.Start - the same inversion the WPF surface host performs.
static class Program
{
	[STAThread]
	static int Main(string[] args)
	{
		// Must be the only statement here that runs before Run - see HostBootstrap's note on JIT
		// timing. The client launches this child inside the designed app's dependency graph, so
		// the host's own assemblies have to resolve from its own directory.
		HostBootstrap.InstallOwnDependencyResolver();
		return Run(args);
	}

	static int Run(string[] args)
	{
		// The app's own assemblies must be loaded before any XAML is parsed: XamlReader resolves
		// local: types by scanning loaded assemblies. Without it almost no real page opens.
		HostBootstrap.PreloadProjectAssemblies(HostBootstrap.ParseArgument(args, "--appbin"));

		var exitCode = 0;
		Application.Start(_ => new HostApplication(args, code => exitCode = code));
		return exitCode;
	}
}

// Implementing IXamlMetadataProvider is what makes XamlReader able to resolve anything beyond the
// framework's small built-in core - see ReflectionXamlMetadata.cs. WinUI looks the provider up on
// Application.Current, so it has to live on this class.
sealed class HostApplication(string[] args, Action<int> reportExitCode) : Application, IXamlMetadataProvider
{
	readonly ReflectionXamlMetadataProvider metadata = new();

	public IXamlType GetXamlType(Type type) => metadata.GetXamlType(type);
	public IXamlType GetXamlType(string fullName) => metadata.GetXamlType(fullName);
	public XmlnsDefinition[] GetXmlnsDefinitions() => metadata.GetXmlnsDefinitions();


	// Everything here is deliberately in OnLaunched rather than the constructor: Application
	// members are not usable until the framework has finished initializing the app object, and
	// touching Resources from the constructor throws COMException 0x8000FFFF (E_UNEXPECTED) out of
	// Application.get_Resources - which surfaces only as a bare WinRT stowed exception (process
	// exit 0xC000027B, no stderr, no managed stack) and looks nothing like its cause.
	protected override void OnLaunched(LaunchActivatedEventArgs launchArgs)
	{
		// NOTE: XamlControlsResources (the type real WinUI apps merge for the Fluent v2 palette) is
		// deliberately NOT constructed here - unpackaged, it throws COMException 0x8000FFFF reading
		// the WindowsAppSDK package's own resources.pri, before producing a single resource. The
		// Uno host never hits this because its Skia backend doesn't go through that native path.
		// FrameworkDefaultResources installs the same Fluent v2 tokens from plain, hand-authored
		// XAML instead (vendored from microsoft-ui-xaml, no compiled-resource dependency) - see its
		// own header and DefaultThemeResources/README.md for the full story. Must run before any
		// document can open, since StaticResource references to these tokens resolve at parse time.
		FrameworkDefaultResources.Install(Resources);
		HeadlessDispatcher.Attach();
		DesignRpc.LogPrefix = "WinUIXamlDesigner.MicrosoftHost";
		InstallOffscreenVisualHost();

		// The DDP wait loop must not run here - OnLaunched has to return to the message pump, and
		// that pump is what every dispatched designer operation depends on.
		_ = Task.Run(() => {
			var code = DesignerChildHost.Run(args, "WinUIXamlDesigner.MicrosoftHost",
				DesignRpc.RegisterRpcMethods,
				HeadlessDispatcher.Run,
				onParentDisconnected: HeadlessDispatcher.RequestExit,
				afterShutdown: () => HeadlessDispatcher.Post(() => Current.Exit()));
			reportExitCode(code);
		});
	}

	/// <summary>
	/// Gives the designer a live visual tree to work in.
	///
	/// DesignHost measures, arranges and RenderTargetBitmap-renders the design root directly. Uno's
	/// headless Skia dispatcher is happy to do that for an element that belongs to no window; real
	/// WinUI 3 is not - RenderAsync on an unparented element never completes, so session/open hangs
	/// until the client gives up. The fix is a genuine window that is simply never seen: it is
	/// activated (so its content is live and composition runs) and then moved far offscreen.
	/// </summary>
	void InstallOffscreenVisualHost()
	{
		// A Grid, not a single-child container: a shared host serves several documents in one
		// process, and every one of their roots has to stay in the tree simultaneously. Overlapping
		// them is fine - nothing is ever displayed, and each root is measured and arranged
		// explicitly by its own DesignHost.
		var surface = new Grid();
		var window = new Window { Content = surface };
		window.Activate();

		// Activation is what makes the tree live, and an activated window is by definition visible,
		// so move it out of every monitor's reach rather than trying to hide it. Sizing it to the
		// design surface is unnecessary - the root is measured/arranged explicitly by DesignHost.
		var id = Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
		AppWindow.GetFromWindowId(id).Move(new PointInt32(-32000, -32000));

		DesignHost.HostVisualRoot = (previous, next) => {
			if (previous != null) surface.Children.Remove(previous);
			if (next != null) surface.Children.Add(next);
		};

		// NOTE on layout: this host deliberately does NOT install DesignHost.HostVisualLayout.
		// Every attempt to take layout into our own hands here made rendering worse:
		//   - Sizing this Grid (and/or the root) to the design size and calling UpdateLayout does
		//     fix element positions, but the rendered bitmap comes back badly stretched -
		//     confirmed on this child's own exported PNG, so it is the render, not the client.
		//     RenderTargetBitmap rasterizes an element's CONTENT extent, and its sized overload
		//     scales that content to fill the requested box, so a root arranged taller than its
		//     children is stretched to fit.
		//   - Detaching the root to arrange it unparented (the way the Uno host's headless tree
		//     works) stops it being laid out at all here: the tree comes back with zero sizes.
		// The window's own layout pass is therefore left to do its job, and DesignHost reads the
		// element tree only after the render, once that pass has committed real offsets.
	}
}
