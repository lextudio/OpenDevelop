using System.Windows.Forms;

using ICSharpCode.Core;

namespace ICSharpCode.ILSpyAddIn.Commands
{
	/// <summary>
	/// File &gt; Open &gt; Assembly: loads the selected assembly into the real, hosted ILSpy
	/// AssemblyTreeModel and shows its pads (Assemblies/Search/Analyzer/Decompiled Code)
	/// alongside OpenDevelop's own.
	/// </summary>
	public sealed class OpenAssemblyCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			using (var dialog = new OpenFileDialog()) {
				dialog.AddExtension = true;
				dialog.Filter = "Assemblies (*.dll;*.exe)|*.dll;*.exe|All files (*.*)|*.*";
				dialog.FilterIndex = 0;
				dialog.Multiselect = false;
				dialog.CheckFileExists = true;

				if (dialog.ShowDialog() == DialogResult.OK) {
					IlSpyWorkspaceHost.OpenAssembly(dialog.FileName);
				}
			}
		}
	}
}
