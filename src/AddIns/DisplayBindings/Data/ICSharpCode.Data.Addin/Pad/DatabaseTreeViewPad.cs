using System;
using System.Collections.ObjectModel;

using ICSharpCode.Data.Core.Interfaces;
using ICSharpCode.SharpDevelop;
using ICSharpCode.ILSpy.ViewModels;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.Data.Addin.Pad
{
	/// <summary>
	/// Legacy AddInTree <c>&lt;Pad&gt;</c> shim (doc/technotes/ilspy.md "Legacy Pad migration",
	/// 2026-08-09) - the real implementation is now <see cref="DatabasesTreeViewPadViewModel"/>.
	/// Constructed once with a plain <c>new</c> and cached in a static field (the AddIn's
	/// assembly is never scanned by <c>OpenDevelopMefHost</c>), then registered with the real
	/// docking host via <c>IPaneModelHost.Add</c>. Must stay a real, constructible
	/// <see cref="AbstractPadContent"/> for the same
	/// <c>PadDescriptor.BringPadToFront()</c>/<c>CreatePad()</c> reason as every other shim in
	/// this migration - and because <c>DatabaseTreeViewCommands</c> still reaches the pad through
	/// <c>DatabasesTreeViewPad.Instance</c>.
	/// </summary>
	public class DatabasesTreeViewPad : AbstractPadContent
	{
		static DatabasesTreeViewPad _instance;
		static DatabasesTreeViewPadViewModel viewModel;

		public static DatabasesTreeViewPad Instance {
			get { return _instance; }
		}

		public ObservableCollection<IDatabase> Databases => viewModel?.Databases;

		public DatabasesTreeViewPad()
		{
			_instance = this;
			if (viewModel == null) {
				viewModel = new DatabasesTreeViewPadViewModel();
				(SD.Services.GetService(typeof(IPaneModelHost)) as IPaneModelHost)?.Add(viewModel);
			}
		}

		public override object Control => viewModel?.Content;
	}
}
