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
			return JsonSerializer.Serialize(new {
				success = true, fontSize = TerminalSettings.FontSize, scrollbackLines = TerminalSettings.ScrollbackLines,
				// The stored IDE theme name ("Light"/"Dark"/"Blue"), so a test that switches the theme can put it back.
				ideTheme = SD.PropertyService.Get("SharpDevelop.IdeTheme", "Light"),
			});
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

		[DevFlowAction("od.terminal.wheel", Description = "Turn the mouse wheel over a cell of the active terminal (positive delta = up, 120 per notch; optional modifiers such as ctrl, which zooms the font): reported to a mouse-aware program, arrow keys on the alternate screen, else scrollback")]
		public static string Wheel(int delta, int column = 0, int row = 0, string modifiers = "")
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			if (!TryParseModifiers(modifiers, out var mods, out var modifierError))
				return JsonSerializer.Serialize(new { success = false, error = modifierError });
			return JsonSerializer.Serialize(new { success = view.HandleWheel(delta, view.CellCenter(column, row), mods) });
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

		[DevFlowAction("od.terminal.links", Description = "The links on the visible screen of the active terminal - URLs and files that exist, with optional line and column - with the cells they cover and the full path (or URL) they open")]
		public static string Links()
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			return JsonSerializer.Serialize(new {
				success = true,
				links = view.FindLinks().Select(l => new {
					kind = l.Candidate.Kind.ToString().ToLowerInvariant(), text = l.Candidate.Path, target = l.Target,
					line = l.Candidate.Line, column = l.Candidate.Column, row = l.Row, startColumn = l.StartColumn, endColumn = l.EndColumn,
				}).ToArray(),
			});
		}

		[DevFlowAction("od.terminal.open-link", Description = "Follow the link under a cell of the active terminal as Ctrl/Cmd+click does: a file opens in the editor at its line and column; a URL is only reported, never launched, here")]
		public static string OpenLink(int column, int row)
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			var hit = view.OpenLinkAt(column, row, launchUrls: false);
			return hit == null
				? JsonSerializer.Serialize(new { success = true, opened = false })
				: JsonSerializer.Serialize(new { success = true, opened = true, kind = hit.Candidate.Kind.ToString().ToLowerInvariant(), target = hit.Target, line = hit.Candidate.Line, column = hit.Candidate.Column });
		}

		[DevFlowAction("od.terminal.find", Description = "Search the active terminal's screen and scrollback (a non-empty query runs a new search, an empty one reuses the last results) and move to a match: step first|next|previous (wraps, scrolls the match to the middle of the viewport), none (search only: the viewport is left where it was) or clear (drop the highlights); returns the match count, the current index and the current match")]
		public static string Find(string query = "", bool matchCase = false, string step = "first")
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			if (step == "clear") {
				view.ClearFind();
				return JsonSerializer.Serialize(new { success = true, count = 0 });
			}
			int? delta = step switch { "first" => 0, "next" => 1, "previous" => -1, "none" => null, _ => -2 };
			if (delta == -2)
				return JsonSerializer.Serialize(new { success = false, error = "Unknown step " + step });
			if (!string.IsNullOrEmpty(query))
				view.RunFind(query, matchCase);
			var match = delta.HasValue ? view.MoveToMatch(delta.Value) : view.CurrentMatch;
			return JsonSerializer.Serialize(new {
				success = true, count = view.MatchCount, current = view.CurrentMatchIndex,
				match = match == null ? null : new { absoluteRow = match.Value.AbsoluteRow, startColumn = match.Value.StartColumn, endColumn = match.Value.EndColumn, text = match.Value.Text },
			});
		}

		[DevFlowAction("od.terminal.find-bar", Description = "Drive the find bar: a non-empty query opens it with that text and searches at once, typed (with the bar open) is typed character by character like the keyboard (the search follows after a 250 ms pause, once), hide=true closes it; returns whether it is open, the status it shows and how many searches the bar has run (\"1 of 2\", \"No results\")")]
		public static string FindBar(string query = "", bool hide = false, string typed = "")
		{
			var pad = Pad();
			if (pad?.ActivePane == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			if (hide)
				pad.HideFind();
			else if (!string.IsNullOrEmpty(query))
				pad.ShowFind(query);
			if (!string.IsNullOrEmpty(typed))
				pad.TypeInFindBar(typed);
			return JsonSerializer.Serialize(new { success = true, open = pad.FindBarOpen, status = pad.FindBarStatus, searches = pad.FindBarSearchCount });
		}

		[DevFlowAction("od.terminal.cwd", Description = "Where the active terminal's shell is now (read from the operating system) and where it was started")]
		public static string WorkingDirectory()
		{
			var session = Pad()?.Active;
			if (session == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			return JsonSerializer.Serialize(new { success = true, current = session.CurrentDirectory, initial = session.InitialDirectory });
		}

		[DevFlowAction("od.terminal.context-menu", Description = "The right-click menu of the active terminal for a click at a cell: its entries (header, enabled); a non-empty invoke runs the entry with that header, as clicking it would; returns the entries and the selected text afterwards")]
		public static string ContextMenu(int column = 0, int row = 0, string invoke = "")
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			var at = view.CellCenter(column, row);
			bool? invoked = string.IsNullOrEmpty(invoke) ? null : view.InvokeContextMenuItem(at, invoke);
			return JsonSerializer.Serialize(new {
				success = true, invoked,
				items = view.ContextMenuItems(at).Select(i => new { header = i.Header, enabled = i.Enabled }).ToArray(),
				selection = view.SelectedText,
			});
		}

		[DevFlowAction("od.terminal.drop", Description = "Drop files (paths separated by '|') or text on the active terminal as a drag and drop would: files paste their quoted paths and a trailing space, text pastes as it is; returns whether anything was pasted")]
		public static string Drop(string paths = "", string text = "")
		{
			var view = Pad()?.View;
			if (view == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			var data = !string.IsNullOrEmpty(paths)
				? new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, paths.Split('|', StringSplitOptions.RemoveEmptyEntries))
				: new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, text);
			return JsonSerializer.Serialize(new { success = true, pasted = view.HandleDrop(data) });
		}

		[DevFlowAction("od.terminal.send-from-editor", Description = "Run Send to Terminal on the active editor: its selection, or the caret's line when nothing is selected, goes to the active terminal and Enter is pressed. selectStart >= 0 first selects selectLength characters from that offset; returns the text sent (null when there is no editor or the text is blank)")]
		public static async Task<string> SendFromEditor(int selectStart = -1, int selectLength = 0)
		{
			if (selectStart >= 0)
				ICSharpCode.SharpDevelop.SD.GetActiveViewContentService<ICSharpCode.SharpDevelop.Editor.ITextEditor>()?.Select(selectStart, selectLength);
			var sent = await TerminalEditorBridge.SendActiveEditorTextAsync();
			return JsonSerializer.Serialize(new { success = true, sent });
		}

		[DevFlowAction("od.terminal.cell", Description = "One visible cell of the active terminal: its text and the colours it is drawn with (#rrggbb, the default colours when the cell has none)")]
		public static string Cell(int column, int row)
		{
			var snap = Pad()?.Active?.GetSnapshot();
			if (snap == null)
				return JsonSerializer.Serialize(new { success = false, error = "No active terminal" });
			if (column < 0 || column >= snap.Columns || row < 0 || row >= snap.Rows)
				return JsonSerializer.Serialize(new { success = false, error = "Cell is outside the screen" });
			var cell = snap[column, row];
			return JsonSerializer.Serialize(new {
				success = true, text = cell.Text,
				foreground = Hex(cell.Foreground != 0 ? cell.Foreground : snap.DefaultForeground),
				background = Hex(cell.Background != 0 ? cell.Background : snap.DefaultBackground),
				bold = (cell.Flags & CellFlags.Bold) != 0, inverse = (cell.Flags & CellFlags.Inverse) != 0,
			});
		}

		static string Hex(uint color) => "#" + (color & 0xffffff).ToString("x6");

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
				background = Hex(snap.DefaultBackground), foreground = Hex(snap.DefaultForeground),
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
