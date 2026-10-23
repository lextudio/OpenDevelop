using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

namespace OpenDevelop.Terminal
{
	/// <summary>
	/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim; the behaviour lives in <see cref="TerminalPaneModel"/>.
	/// Not a MEF part (this add-in's assembly is not scanned), so it is created with a plain
	/// <c>new</c> on first use and registered with the docking host via <c>IPaneModelHost.Add</c>.
	/// </summary>
	public class TerminalPad : AbstractPadContent
	{
		static TerminalPaneModel model;

		/// <summary>Set while the pad is being created on behalf of a request that starts its own terminal.</summary>
		internal static bool SkipInitialSession { get; set; }

		public TerminalPad()
		{
			if (model == null) {
				model = new TerminalPaneModel();
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(model);
			}
			if (!SkipInitialSession)
				model.EnsureSession();
		}

		public override object Control => model?.Content;

		public override object InitiallyFocusedControl => model?.View;
	}
}
