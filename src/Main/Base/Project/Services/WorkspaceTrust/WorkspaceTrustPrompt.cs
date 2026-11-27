using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>Shows a modal choice and returns the index of the clicked button (-1 when closed); see <see cref="IMessageService.ShowCustomDialog"/>.</summary>
	public delegate int TrustDialog(string caption, string text, int acceptButtonIndex, int cancelButtonIndex, string[] buttons);

	/// <summary>The dialogs and decisions of Workspace Trust. See doc/technotes/workspace-trust.md.</summary>
	public static class WorkspaceTrustPrompt
	{
		static int DefaultDialog(string caption, string text, int acceptButtonIndex, int cancelButtonIndex, string[] buttons)
			=> MessageService.ShowCustomDialog(caption, text, acceptButtonIndex, cancelButtonIndex, buttons);

		/// <summary>
		/// Asks whether to trust <paramref name="root"/>. Returns true when it is trusted afterwards.
		/// A root that is too broad to trust (a filesystem root, the user profile) is not offered, nor is its parent.
		/// </summary>
		public static bool Ask(IWorkspaceTrustService trust, string root, TrustDialog dialog = null)
		{
			dialog ??= DefaultDialog;

			var trustable = new List<string>();
			if (trust.CanTrustRoot(root)) {
				trustable.Add(root);
				string parent = Path.GetDirectoryName(root);
				if (!string.IsNullOrEmpty(parent) && trust.CanTrustRoot(parent))
					trustable.Add(parent);
			}

			string text = string.Format(WorkspaceTrust.Text("WorkspaceTrust.Prompt.Text",
				"OpenDevelop is about to open a solution from:\n\n{0}\n\n"
				+ "Build tools, analyzers, designers, debuggers and test hosts can run code that lives in this folder. "
				+ "In Restricted Mode OpenDevelop is a text editor with basic syntax highlighting for this solution: "
				+ "no code completion or other semantic analysis, no designers (code view only), "
				+ "no build, run, debug or tests, no analyzers or source generators, and no terminals.\n\n"
				+ "Trust the folder only if you know where it came from."), root);

			var buttons = new List<string>();
			if (trustable.Count > 0)
				buttons.Add(WorkspaceTrust.Text("WorkspaceTrust.TrustFolder", "Trust Folder"));
			if (trustable.Count > 1)
				buttons.Add(WorkspaceTrust.Text("WorkspaceTrust.TrustParentFolder", "Trust Parent Folder"));
			buttons.Add(WorkspaceTrust.Text("WorkspaceTrust.RestrictedMode", "Restricted Mode"));
			int restricted = buttons.Count - 1;
			string caption = WorkspaceTrust.Text("WorkspaceTrust.Prompt.Caption", "Do you trust the authors of this folder?");

			int answer = dialog(caption, text, restricted, restricted, buttons.ToArray());
			if (answer < 0 || answer >= trustable.Count)
				return false;
			trust.Trust(trustable[answer]);
			return true;
		}

		/// <summary>Records the decision for a solution or project about to open and makes it the current workspace.</summary>
		public static void Establish(IWorkspaceTrustService trust, string solutionOrProjectFile, TrustDialog dialog = null)
		{
			if (trust == null)
				return;
			string root = trust.GetTrustRoot(solutionOrProjectFile);
			if (trust.IsEnabled && trust.GetState(root) == TrustState.Restricted)
				Ask(trust, root, dialog);
			trust.SetCurrentWorkspace(root);
			if (trust.IsEnabled && trust.CurrentState == TrustState.Restricted) {
				LoggingService.Info("Opening " + solutionOrProjectFile + " in Restricted Mode.");
				try {
					SD.StatusBar.SetMessage(WorkspaceTrust.Text("WorkspaceTrust.StatusMessage",
						"Restricted Mode: code view with basic syntax highlighting only; semantic analysis, designers, build, run, debug, tests and terminals are disabled. Tools > Workspace Trust changes this."));
				} catch (Exception) {
				}
			}
		}

		/// <summary>Revokes the given stored roots. Returns true when that made the open workspace Restricted.</summary>
		public static bool Revoke(IWorkspaceTrustService trust, IEnumerable<string> roots)
		{
			string workspace = trust.CurrentWorkspaceRoot;
			bool wasTrusted = workspace != null && trust.GetState(workspace) == TrustState.Trusted;
			foreach (string root in roots.ToList())
				trust.Revoke(root);
			return wasTrusted && trust.GetState(workspace) == TrustState.Restricted;
		}
	}
}
