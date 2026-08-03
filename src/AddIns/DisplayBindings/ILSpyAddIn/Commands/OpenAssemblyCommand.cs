using Microsoft.Win32;

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
			var dialog = new OpenFileDialog {
				AddExtension = true,
				Filter = ".NET assemblies (*.dll;*.exe;*.winmd;*.wasm)|*.dll;*.exe;*.winmd;*.wasm" +
					"|NuGet Packages (*.nupkg)|*.nupkg" +
					"|Portable Program Database (*.pdb)|*.pdb" +
					"|All files (*.*)|*.*",
				FilterIndex = 0,
				Multiselect = false,
				CheckFileExists = true,
			};

			if (dialog.ShowDialog() == true) {
				_ = IlSpyWorkspaceHost.OpenAssemblyAsync(dialog.FileName);
			}
		}
	}
}
