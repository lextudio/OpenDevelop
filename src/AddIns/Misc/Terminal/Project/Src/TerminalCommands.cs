using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;

namespace OpenDevelop.Terminal
{
	abstract class TerminalCommand : AbstractMenuCommand
	{
		protected TerminalPaneModel Pad => Owner as TerminalPaneModel ?? TerminalPaneModel.Instance;
	}

	sealed class NewTerminalCommand : TerminalCommand
	{
		public override void Run() => Pad?.NewSessionAsync().FireAndForget();
	}

	sealed class TerminalSettingsCommand : AbstractMenuCommand
	{
		public override void Run()
			=> ICSharpCode.SharpDevelop.Commands.OptionsCommand.ShowTreeOptions("Options", AddInTree.GetTreeNode("/SharpDevelop/Dialogs/OptionsDialog"));
	}

	/// <summary>Edit menu and editor context menu: run the selection (or the caret's line) in the active terminal.</summary>
	sealed class SendToTerminalCommand : AbstractMenuCommand
	{
		public override void Run() => TerminalEditorBridge.SendActiveEditorTextAsync().FireAndForget();
	}

	sealed class NextTerminalCommand : AbstractMenuCommand
	{
		public override void Run() => TerminalPaneModel.Instance?.ActivateRelative(1);
	}

	sealed class PreviousTerminalCommand : AbstractMenuCommand
	{
		public override void Run() => TerminalPaneModel.Instance?.ActivateRelative(-1);
	}

	sealed class RenameTerminalCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			var pad = TerminalPaneModel.Instance;
			if (pad?.ActivePane == null)
				return;
			var name = MessageService.ShowInputBox("Rename Terminal", "Name for this terminal (empty restores the shell's title):", TerminalPaneModel.Name(pad.ActivePane));
			if (name != null)
				pad.RenameActive(name);
		}
	}

	sealed class FindTerminalCommand : TerminalCommand
	{
		public override void Run() => Pad?.ShowFind();
	}

	sealed class SplitTerminalCommand : TerminalCommand
	{
		public override void Run() => Pad?.SplitActiveAsync().FireAndForget();
	}

	sealed class SplitTerminalVerticalCommand : TerminalCommand
	{
		public override void Run() => Pad?.SplitActiveAsync(TerminalSplitOrientation.Vertical).FireAndForget();
	}

	sealed class KillTerminalCommand : TerminalCommand
	{
		public override void Run() => Pad?.KillActive();
	}

	sealed class ClearTerminalCommand : TerminalCommand
	{
		public override void Run() => Pad?.ClearActive();
	}

	sealed class CopyTerminalCommand : TerminalCommand
	{
		public override void Run() => Pad?.CopySelection();
	}

	sealed class PasteTerminalCommand : TerminalCommand
	{
		public override void Run() => Pad?.Paste();
	}

	/// <summary>View &gt; Terminal (Ctrl+`): shows the pad and puts the keyboard in the active terminal.</summary>
	sealed class ShowTerminalCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			SD.Workbench.GetPad(typeof(TerminalPad))?.BringPadToFront();
			TerminalPaneModel.Instance?.View?.Focus();
		}
	}

	/// <summary>View &gt; New Terminal (Ctrl+Shift+`).</summary>
	sealed class NewTerminalFromMenuCommand : AbstractMenuCommand
	{
		public override void Run()
		{
			SD.Workbench.GetPad(typeof(TerminalPad))?.BringPadToFront();
			TerminalPaneModel.Instance?.NewSessionAsync().FireAndForget();
		}
	}

	/// <summary>Lets the shell open terminals in a folder (Project Browser "Open in Terminal").</summary>
	sealed class TerminalHost : ITerminalHost
	{
		public async Task OpenTerminalAsync(string workingDirectory)
		{
			// Creating the pad normally starts a first terminal so it is never empty; here the requested one is that terminal.
			TerminalPad.SkipInitialSession = TerminalPaneModel.Instance == null;
			try {
				SD.Workbench.GetPad(typeof(TerminalPad))?.BringPadToFront();
			} finally {
				TerminalPad.SkipInitialSession = false;
			}
			var pad = TerminalPaneModel.Instance;
			if (pad == null)
				throw new InvalidOperationException("The Terminal pad could not be created.");
			await pad.NewSessionAsync(null, workingDirectory);
		}

		public async Task OpenTerminalAsync(TerminalLaunchRequest request)
		{
			ArgumentNullException.ThrowIfNull(request);
			if (string.IsNullOrWhiteSpace(request.Executable))
				throw new ArgumentException("A terminal launch request requires an executable.", nameof(request));
			TerminalPad.SkipInitialSession = TerminalPaneModel.Instance == null;
			try {
				SD.Workbench.GetPad(typeof(TerminalPad))?.BringPadToFront();
			} finally {
				TerminalPad.SkipInitialSession = false;
			}
			var pad = TerminalPaneModel.Instance;
			if (pad == null)
				throw new InvalidOperationException("The Terminal pad could not be created.");
			if (!string.IsNullOrWhiteSpace(request.SessionKey) && pad.ActivateSession(request.SessionKey))
				return;
			await pad.NewSessionAsync(request.Executable, request.WorkingDirectory, request.Arguments, request.Environment, request.Title, request.SessionKey);
		}
	}

	public sealed class RegisterTerminalServicesCommand : AbstractCommand
	{
		public override void Run() => SD.Services.AddService(typeof(ITerminalHost), new TerminalHost());
	}

	public sealed class RegisterDevFlowActionsCommand : DevFlowActionRegistrationCommand
	{
	}
}
