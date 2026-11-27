using System.Threading.Tasks;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop.Commands
{
	/// <summary>Tools > Workspace Trust: the state of the open workspace and every trusted folder, with revoke.</summary>
	public class WorkspaceTrustCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var trust = SD.GetService<IWorkspaceTrustService>();
			if (trust == null) {
				MessageService.ShowMessage(WorkspaceTrust.Text("WorkspaceTrust.NotAvailable", "Workspace Trust is not available."));
				return;
			}

			var window = new WorkspaceTrustWindow(trust) { Owner = SD.Workbench.MainWindow };
			window.ShowDialog();
			if (window.TrustedCurrentWorkspace)
				ReloadSolution();
		}

		static void ReloadSolution()
		{
			var solution = SD.ProjectService.CurrentSolution;
			if (solution != null)
				_ = ReloadSolutionAsync(solution.FileName);
		}

		// The asynchronous open, so trusting a workspace does not freeze the UI for the whole reload;
		// failures are reported by the project service itself.
		static async Task ReloadSolutionAsync(ICSharpCode.Core.FileName fileName)
		{
			try {
				await SD.ProjectService.OpenSolutionOrProjectAsync(fileName);
			} catch (System.Exception ex) {
				LoggingService.Warn("Could not reopen " + fileName + " after a trust change.", ex);
			}
		}
	}
}
