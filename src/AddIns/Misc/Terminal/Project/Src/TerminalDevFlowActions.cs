using System.Text.Json;
using ICSharpCode.SharpDevelop;
using LeXtudio.DevFlow.Agent.Core;
using G = LibGhostty.GhosttyNativeLibrary;
using Microsoft.Maui.DevFlow.Agent.Core;

namespace OpenDevelop.Terminal
{
	[DevFlowUIThread]
	public static class TerminalDevFlowActions
	{
		static TerminalPaneModel Pad()
		{
			if (TerminalPaneModel.Instance == null)
				SD.Workbench.GetPad(typeof(TerminalPad))?.BringPadToFront();
			return TerminalPaneModel.Instance;
		}

		[DevFlowAction("od.terminal.new", Description = "Open the Terminal pad and start a new shell (optional absolute shell path); returns the session index")]
		public static async Task<string> New(string shell = "")
		{
			var pad = Pad();
			if (pad == null)
				return JsonSerializer.Serialize(new { success = false, error = "Terminal pad is not available" });
			if (!string.IsNullOrEmpty(shell) && !File.Exists(shell))
				return JsonSerializer.Serialize(new { success = false, error = "No such shell: " + shell });
			try {
				return Describe(pad, await pad.NewSessionAsync(string.IsNullOrEmpty(shell) ? null : shell));
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = "Could not start the shell: " + ex.Message });
			}
		}

		[DevFlowAction("od.terminal.shells", Description = "The shells the New Terminal drop-down offers (full paths) and the default one; a non-empty makeDefault (a path from that list) first stores it as the default shell")]
		public static string Shells(string makeDefault = "")
		{
			var shells = TerminalShells.Detect();
			if (!string.IsNullOrEmpty(makeDefault)) {
				if (!shells.Contains(makeDefault, StringComparer.OrdinalIgnoreCase))
					return JsonSerializer.Serialize(new { success = false, error = "Not a detected shell: " + makeDefault });
				TerminalShells.Default = makeDefault;
			}
			return JsonSerializer.Serialize(new { success = true, shells, @default = TerminalShells.Default });
		}

		[DevFlowAction("od.terminal.split", Description = "Split the active terminal: a new shell (same shell as the active one) beside it in the same group; returns the new pane")]
		public static async Task<string> Split()
		{
			var pad = Pad();
			if (pad == null)
				return JsonSerializer.Serialize(new { success = false, error = "Terminal pad is not available" });
			try {
				return Describe(pad, await pad.SplitActiveAsync());
			} catch (Exception ex) {
				return JsonSerializer.Serialize(new { success = false, error = "Could not start the shell: " + ex.Message });
			}
		}

		[DevFlowAction("od.terminal.rename", Description = "Name the active terminal (empty restores the shell's own title), as the Rename Terminal command does after its input box; returns the new list labels")]
		public static string Rename(string name)
		{
			var pad = Pad();
			if (pad?.ActivePane == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			pad.RenameActive(name);
			return JsonSerializer.Serialize(new { success = true, labels = pad.Labels() });
		}

		[DevFlowAction("od.terminal.activate", Description = "Make the terminal at the given index (as listed by od.terminal.list) the active one")]
		public static string Activate(int index)
		{
			var pad = Pad();
			if (pad == null || index < 0 || index >= pad.Panes.Count())
				return JsonSerializer.Serialize(new { success = false, error = "No such terminal" });
			pad.ActivateIndex(index);
			return JsonSerializer.Serialize(new { success = true, active = pad.ActiveIndex });
		}

		static string Describe(TerminalPaneModel pad, TerminalPane pane)
			=> JsonSerializer.Serialize(new { success = true, index = pad.Panes.ToList().IndexOf(pane), group = pad.GroupIndexOf(pane), pid = pane.Session.Pid, shell = pane.Session.ShellName, columns = pane.Session.Columns, rows = pane.Session.Rows });

		[DevFlowAction("od.terminal.send", Description = "Write raw text to the active terminal's PTY (use \\r for Enter)")]
		public static string Send(string text)
		{
			var session = Pad()?.Active;
			if (session == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			session.Send(text ?? "");
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.terminal.key", Description = "Press a key in the active terminal exactly as the view's key handler does: a WPF Key name (Up, F5, A, Enter...) plus optional comma-separated modifiers (ctrl, alt, shift); returns whether the terminal took the key")]
		public static string PressKey(string key, string modifiers = "")
		{
			var pad = Pad();
			if (pad?.Active == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			if (!Enum.TryParse<System.Windows.Input.Key>(key, true, out var parsed))
				return JsonSerializer.Serialize(new { success = false, error = "Unknown key " + key });
			if (!TryParseModifiers(modifiers, out var mods, out var modifierError))
				return JsonSerializer.Serialize(new { success = false, error = modifierError });
			return JsonSerializer.Serialize(new { success = true, handled = pad.View?.HandleKey(parsed, mods) ?? false });
		}

		[DevFlowAction("od.terminal.scroll", Description = "Scroll the active terminal's viewport by lines (negative = back into the scrollback, positive = towards the prompt); scrolling to the bottom happens on any key")]
		public static string Scroll(int lines)
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			view.Scroll(lines);
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.terminal.select", Description = "Select the visible cells from (startColumn,startRow) to (endColumn,endRow), inclusive, as a mouse drag would; returns the selected text")]
		public static string Select(int startColumn, int startRow, int endColumn, int endRow)
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			view.SelectRange(startColumn, startRow, endColumn, endRow);
			return JsonSerializer.Serialize(new { success = true, text = view.SelectedText });
		}

		[DevFlowAction("od.terminal.copy", Description = "Copy the selection to the clipboard (the Copy button); returns the copied text, empty when nothing is selected")]
		public static string Copy()
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			return JsonSerializer.Serialize(new { success = true, text = view.CopySelection() });
		}

		[DevFlowAction("od.terminal.paste", Description = "Paste the given text into the active terminal the way Paste does (bracketed when the program asked for it), without touching the clipboard")]
		public static string Paste(string text)
		{
			var session = Pad()?.Active;
			if (session == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			session.Paste(text ?? "");
			return JsonSerializer.Serialize(new { success = true });
		}

		[DevFlowAction("od.terminal.settings", Description = "Read the terminal settings, optionally storing a new font size (points) and scrollback line count first, as saving the Terminal options page does; open terminals re-measure, scrollback applies to terminals started afterwards")]
		public static string Settings(double fontSize = 0, int scrollbackLines = -1)
		{
			if (fontSize > 0)
				TerminalSettings.FontSize = fontSize;
			if (scrollbackLines >= 0)
				TerminalSettings.ScrollbackLines = scrollbackLines;
			if (fontSize > 0 || scrollbackLines >= 0)
				TerminalSettings.RaiseChanged();
			return JsonSerializer.Serialize(new { success = true, fontSize = TerminalSettings.FontSize, scrollbackLines = TerminalSettings.ScrollbackLines });
		}

		[DevFlowAction("od.terminal.mouse", Description = "Send a mouse event to the active terminal at a cell, through the same path real mouse input takes: action press|release|motion, button left|right|middle|none; the program only receives it if it enabled mouse reporting")]
		public static string Mouse(string action, string button, int column, int row, string modifiers = "")
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			int ghosttyAction = action.ToLowerInvariant() switch { "press" => G.MouseEventActionPress, "release" => G.MouseEventActionRelease, "motion" => G.MouseEventActionMotion, _ => -1 };
			int ghosttyButton = button.ToLowerInvariant() switch { "left" => G.MouseButtonLeft, "right" => G.MouseButtonRight, "middle" => G.MouseButtonMiddle, "none" => G.MouseButtonNone, _ => -1 };
			if (ghosttyAction < 0 || ghosttyButton < 0)
				return JsonSerializer.Serialize(new { success = false, error = "Unknown action or button" });
			if (!TryParseModifiers(modifiers, out var mods, out var modifierError))
				return JsonSerializer.Serialize(new { success = false, error = modifierError });
			return JsonSerializer.Serialize(new { success = true, sent = view.ReportMouse(ghosttyAction, ghosttyButton, view.CellCenter(column, row), mods) });
		}

		[DevFlowAction("od.terminal.wheel", Description = "Turn the mouse wheel over a cell of the active terminal (positive delta = up, 120 per notch): reported to a mouse-aware program, arrow keys on the alternate screen, else scrollback")]
		public static string Wheel(int delta, int column = 0, int row = 0)
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			return JsonSerializer.Serialize(new { success = view.HandleWheel(delta, view.CellCenter(column, row), System.Windows.Input.ModifierKeys.None) });
		}

		[DevFlowAction("od.terminal.click", Description = "Left-click a cell of the active terminal clickCount times (2 = word, 3 = line selection) and release; returns the selected text")]
		public static string Click(int column, int row, int clickCount = 1)
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			var at = view.CellCenter(column, row);
			view.HandleLeftDown(at, clickCount, System.Windows.Input.ModifierKeys.None);
			view.HandleLeftUp(at, System.Windows.Input.ModifierKeys.None);
			return JsonSerializer.Serialize(new { success = true, text = view.SelectedText });
		}

		/// <summary>Comma-separated ctrl/alt/shift (WPF ModifierKeys names, "ctrl" as an alias for Control); an unknown name is an error, not ignored.</summary>
		static bool TryParseModifiers(string modifiers, out System.Windows.Input.ModifierKeys mods, out string error)
		{
			mods = System.Windows.Input.ModifierKeys.None;
			error = null;
			foreach (var m in (modifiers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
				if (!Enum.TryParse<System.Windows.Input.ModifierKeys>(m.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ? "Control" : m, true, out var one)) {
					error = "Unknown modifier " + m;
					return false;
				}
				mods |= one;
			}
			return true;
		}

		[DevFlowAction("od.terminal.text", Description = "Visible screen text of the active terminal, plus size, cursor and title")]
		public static string Text()
		{
			var pad = Pad();
			var session = pad?.Active;
			var snap = session?.GetSnapshot();
			if (snap == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			return JsonSerializer.Serialize(new {
				success = true, columns = snap.Columns, rows = snap.Rows, title = session.Title,
				cursor = new { column = snap.CursorColumn, row = snap.CursorRow },
				exited = session.HasExited, exitCode = session.ExitCode,
				scrollback = new { total = snap.ScrollbackTotal, offset = snap.ScrollbackOffset, length = snap.ScrollbackLength },
				bracketedPaste = session.BracketedPaste,
				mouseTracking = session.MouseTrackingEnabled,
				alternateScreen = session.IsAlternateScreen,
				text = snap.GetText(),
			});
		}

		[DevFlowAction("od.terminal.list", Description = "List terminal sessions (index, pid, shell, title, active)")]
		public static string List()
		{
			var pad = Pad();
			if (pad == null)
				return JsonSerializer.Serialize(new { success = false, error = "Terminal pad is not available" });
			return JsonSerializer.Serialize(new {
				success = true,
				sessions = pad.Panes.Select((p, i) => new { index = i, group = pad.GroupIndexOf(p), pid = p.Session.Pid, shell = p.Session.ShellName, title = p.Session.Title, columns = p.Session.Columns, rows = p.Session.Rows, exited = p.Session.HasExited, active = p == pad.ActivePane }).ToArray(),
				labels = pad.Labels(),
			});
		}

		[DevFlowAction("od.terminal.kill", Description = "Kill the active terminal (a new one is started if it was the last)")]
		public static string Kill()
		{
			var pad = Pad();
			pad?.KillActive();
			return JsonSerializer.Serialize(new { success = pad != null });
		}
	}
}
