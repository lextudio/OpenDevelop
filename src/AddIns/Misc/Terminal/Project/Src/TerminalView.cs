using System.Globalization;
using System.Text;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using G = LibGhostty.GhosttyNativeLibrary;

namespace OpenDevelop.Terminal
{
	/// <summary>Draws a <see cref="TerminalSnapshot"/> cell by cell and turns keys into PTY input.</summary>
	sealed class TerminalView : FrameworkElement
	{
		Typeface typeface = MakeTypeface(FontWeights.Normal);
		Typeface boldTypeface = MakeTypeface(FontWeights.Bold);

		static Typeface MakeTypeface(FontWeight weight)
			=> new Typeface(new FontFamily(TerminalSettings.FontFamily), FontStyles.Normal, weight, FontStretches.Normal);
		double fontSize = TerminalSettings.FontSize;
		readonly Dictionary<uint, Brush> brushes = new Dictionary<uint, Brush>();
		readonly DispatcherTimer refreshTimer;
		TerminalSession session;
		TerminalSnapshot snapshot;
		volatile bool dirty;
		double cellWidth, cellHeight;
		(int column, int row)? selectionStart, selectionEnd;
		bool selecting;
		static readonly Brush SelectionBrush = MakeSelectionBrush();

		static Brush MakeSelectionBrush()
		{
			var brush = new SolidColorBrush(Color.FromArgb(110, 70, 130, 230));
			brush.Freeze();
			return brush;
		}

		public TerminalView()
		{
			Focusable = true;
			AllowDrop = true;
			FocusVisualStyle = null;
			ClipToBounds = true;
			MeasureCell();
			TerminalSettings.Changed += OnSettingsChanged;
			// Coalesce redraws: the PTY thread only sets `dirty`; the UI thread repaints at most every 16 ms.
			refreshTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
			themeSignature = TerminalTheme.Current().Signature;
			refreshTimer.Tick += (_, _) => {
				CheckBell();
				Refresh();
				if (++themeTicks >= 60) { // about once a second: the IDE theme can change at any time
					themeTicks = 0;
					CheckTheme();
				}
			};
			refreshTimer.Start();
		}

		/// <summary>The user pressed the find shortcut (Cmd+F, Ctrl+Shift+F) in this view.</summary>
		public event Action FindRequested;

		/// <summary>The shell has ended and the user pressed a key: the owner should close this pane.</summary>
		public event Action CloseRequested;

		/// <summary>Raised on the UI thread after the view repainted from a fresh snapshot (title and size may have changed).</summary>
		public event Action Updated;

		int seenBells;
		DateTime flashUntil;
		static readonly Brush FlashBrush = Frozen(Color.FromArgb(90, 255, 255, 255));

		/// <summary>How many times the view has flashed for the bell (only counted while the visual bell is on).</summary>
		public int FlashCount { get; private set; }

		/// <summary>A bell rang since the last tick: flash the view briefly, if the setting asks for it.</summary>
		void CheckBell()
		{
			if (session == null)
				return;
			int bells = session.BellCount;
			if (bells != seenBells) {
				seenBells = bells;
				if (TerminalSettings.VisualBell) {
					FlashCount++;
					flashUntil = DateTime.UtcNow.AddMilliseconds(180);
					dirty = true;
				}
			}
			if (flashUntil != default && DateTime.UtcNow >= flashUntil) {
				flashUntil = default;
				dirty = true; // repaint without the flash
			}
		}

		TerminalLinkHit hoveredLink;
		IReadOnlyList<TerminalMatch> matches = Array.Empty<TerminalMatch>();
		int currentMatch = -1;
		static readonly Brush MatchBrush = Frozen(Color.FromArgb(120, 255, 210, 0));
		static readonly Brush CurrentMatchBrush = Frozen(Color.FromArgb(170, 255, 120, 0));

		static Brush Frozen(Color color)
		{
			var brush = new SolidColorBrush(color);
			brush.Freeze();
			return brush;
		}

		public int MatchCount => matches.Count;
		public int CurrentMatchIndex => currentMatch;
		public TerminalMatch? CurrentMatch => currentMatch >= 0 && currentMatch < matches.Count ? matches[currentMatch] : null;

		/// <summary>Searches the screen and scrollback; matches are highlighted where visible. Returns how many there are.</summary>
		public int RunFind(string query, bool matchCase)
		{
			matches = session == null ? Array.Empty<TerminalMatch>() : session.Search(query, matchCase);
			currentMatch = -1;
			dirty = true;
			InvalidateVisual();
			return matches.Count;
		}

		/// <summary>Selects the first match (<paramref name="step"/> 0) or the next (+1) / previous (-1) one, wrapping around, and scrolls it into view.</summary>
		public TerminalMatch? MoveToMatch(int step)
		{
			if (matches.Count == 0 || session == null)
				return null;
			currentMatch = step == 0 ? 0
				: currentMatch < 0 ? (step > 0 ? 0 : matches.Count - 1)
				: ((currentMatch + step) % matches.Count + matches.Count) % matches.Count;
			ClearSelection();
			session.ScrollToRow(matches[currentMatch].AbsoluteRow);
			dirty = true;
			return matches[currentMatch];
		}

		public void ClearFind()
		{
			matches = Array.Empty<TerminalMatch>();
			currentMatch = -1;
			InvalidateVisual();
		}

		/// <summary>The link under a cell (file that exists, or http(s) URL), or null.</summary>
		public TerminalLinkHit FindLinkAt(int column, int row)
		{
			var snap = snapshot;
			if (snap == null || session == null || row < 0 || row >= snap.Rows || column < 0 || column >= snap.Columns)
				return null;
			var text = snap.GetRowText(row, out var columns);
			foreach (var candidate in TerminalLinks.Find(text)) {
				int start = columns[candidate.Start], end = columns[candidate.Start + candidate.Length - 1];
				if (column < start || column > end)
					continue;
				var target = TerminalLinkOpener.Resolve(candidate, session.CurrentDirectory);
				if (target != null)
					return new TerminalLinkHit(candidate, target, row, start, end);
			}
			return null;
		}

		/// <summary>Every link on the visible screen, in reading order.</summary>
		public IReadOnlyList<TerminalLinkHit> FindLinks()
		{
			var snap = snapshot;
			var hits = new List<TerminalLinkHit>();
			if (snap == null || session == null)
				return hits;
			for (int row = 0; row < snap.Rows; row++) {
				var text = snap.GetRowText(row, out var columns);
				foreach (var candidate in TerminalLinks.Find(text)) {
					var target = TerminalLinkOpener.Resolve(candidate, session.CurrentDirectory);
					if (target != null)
						hits.Add(new TerminalLinkHit(candidate, target, row, columns[candidate.Start], columns[candidate.Start + candidate.Length - 1]));
				}
			}
			return hits;
		}

		/// <summary>Follows the link under a cell as Ctrl/Cmd+click does; returns it, or null when there is none.</summary>
		public TerminalLinkHit OpenLinkAt(int column, int row, bool launchUrls)
		{
			var hit = FindLinkAt(column, row);
			if (hit != null)
				TerminalLinkOpener.Open(hit, launchUrls);
			return hit;
		}

		/// <summary>
		/// Something dropped on the terminal: files become their quoted paths separated by spaces (with a trailing space, ready
		/// for more typing), text is pasted as it is. Returns whether anything was pasted.
		/// </summary>
		public bool HandleDrop(IDataObject data)
		{
			if (session == null || session.HasExited)
				return false;
			string text = null;
			if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
				text = string.Join(" ", files.Select(f => TerminalQuote.Path(f, session.ShellPath))) + " ";
			else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string dropped && dropped.Length > 0)
				text = dropped;
			if (text == null)
				return false;
			ClearSelection();
			session.Paste(text);
			return true;
		}

		protected override void OnDragOver(DragEventArgs e)
		{
			base.OnDragOver(e);
			e.Effects = session != null && !session.HasExited && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
				? DragDropEffects.Copy : DragDropEffects.None;
			e.Handled = true;
		}

		protected override void OnDrop(DragEventArgs e)
		{
			base.OnDrop(e);
			if (HandleDrop(e.Data)) {
				Focus();
				e.Handled = true;
			}
		}

		/// <summary>The right-click menu for a click at <paramref name="position"/>: Copy (when something is selected), Paste, Select All, Clear, Find, and Open Link when the click is on one.</summary>
		public IReadOnlyList<(string Header, bool Enabled, Action Run)> ContextMenuItems(Point position)
		{
			var cell = CellAt(position);
			var link = FindLinkAt(cell.column, cell.row);
			var items = new List<(string, bool, Action)> {
				("Copy", HasSelection, () => CopySelection()),
				("Paste", true, PasteFromClipboard),
				("Select All", snapshot != null, () => { if (snapshot != null) SelectRange(0, 0, snapshot.Columns - 1, snapshot.Rows - 1); }),
				("Clear", true, () => session?.Clear()),
				("Find...", true, () => FindRequested?.Invoke()),
			};
			if (link != null)
				items.Add((link.Candidate.Kind == TerminalLinkKind.Url ? "Open Link" : "Open File", true, () => TerminalLinkOpener.Open(link, launchUrls: true)));
			return items;
		}

		/// <summary>Runs the context-menu entry with this header (as a click on it would); false when there is no such enabled entry.</summary>
		public bool InvokeContextMenuItem(Point position, string header)
		{
			foreach (var item in ContextMenuItems(position)) {
				if (item.Header == header && item.Enabled) {
					item.Run();
					return true;
				}
			}
			return false;
		}

		protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
		{
			base.OnMouseRightButtonUp(e);
			// A program that tracks the mouse owns the right button too (Shift gives it back to us, as for selection).
			if (session == null || (session.MouseTrackingEnabled && (Keyboard.Modifiers & ModifierKeys.Shift) == 0))
				return;
			var menu = new ContextMenu { PlacementTarget = this };
			var items = ContextMenuItems(e.GetPosition(this));
			for (int i = 0; i < items.Count; i++) {
				if (i == 1 || i == 3 || i == 5)
					menu.Items.Add(new Separator());
				var item = items[i];
				var entry = new MenuItem { Header = item.Header, IsEnabled = item.Enabled };
				entry.Click += (_, _) => item.Run();
				menu.Items.Add(entry);
			}
			menu.Closed += (_, _) => { if (ReferenceEquals(openMenu, menu)) openMenu = null; };
			openMenu = menu;
			menu.IsOpen = true;
			e.Handled = true;
		}

		ContextMenu openMenu;

		/// <summary>The headers of the right-click menu while it is open (empty when it is not): what a pointer test can observe of a popup.</summary>
		public IReadOnlyList<string> OpenContextMenuHeaders()
		{
			var menu = openMenu;
			if (menu == null || !menu.IsOpen)
				return Array.Empty<string>();
			var headers = new List<string>();
			foreach (var entry in menu.Items)
				if (entry is MenuItem item)
					headers.Add(item.Header as string);
			return headers;
		}

		public void CloseContextMenu()
		{
			if (openMenu != null)
				openMenu.IsOpen = false;
		}

		static bool IsLinkModifier(ModifierKeys modifiers) => (modifiers & (ModifierKeys.Control | ModifierKeys.Windows)) != 0;

		void UpdateHoveredLink(Point position)
		{
			TerminalLinkHit hit = null;
			if (IsLinkModifier(Keyboard.Modifiers) && session != null && !session.MouseTrackingEnabled) {
				var cell = CellAt(position);
				hit = FindLinkAt(cell.column, cell.row);
			}
			if (Equals(hit, hoveredLink))
				return;
			hoveredLink = hit;
			Cursor = hit == null ? null : Cursors.Hand;
			InvalidateVisual();
		}

		protected override void OnMouseLeave(MouseEventArgs e)
		{
			base.OnMouseLeave(e);
			UpdateHoveredLink(new Point(-1, -1));
		}

		string themeSignature;
		int themeTicks;

		void CheckTheme()
		{
			var colors = TerminalTheme.Current();
			if (colors.Signature == themeSignature)
				return;
			themeSignature = colors.Signature;
			session?.ApplyColors(colors);
			dirty = true;
		}

		void OnSettingsChanged()
		{
			fontSize = TerminalSettings.FontSize;
			typeface = MakeTypeface(FontWeights.Normal);
			boldTypeface = MakeTypeface(FontWeights.Bold);
			MeasureCell();
			FitSession();
			dirty = true;
		}

		public void Dispose()
		{
			TerminalSettings.Changed -= OnSettingsChanged;
			refreshTimer.Stop();
			Session = null;
		}

		public TerminalSession Session {
			get => session;
			set {
				if (session != null)
					session.Changed -= OnSessionChanged;
				session = value;
				snapshot = null;
				if (session != null) {
					session.Changed += OnSessionChanged;
					FitSession();
				}
				dirty = true;
				Refresh();
			}
		}

		public double CellWidth => cellWidth;
		public double CellHeight => cellHeight;
		/// <summary>False until the first layout pass: a PTY must not start at the 2x1 the fit maths would give then.</summary>
		public bool IsLaidOut => ActualWidth >= cellWidth * 2 && ActualHeight >= cellHeight;
		public int ColumnsFit => Math.Max(2, (int)(ActualWidth / cellWidth));
		public int RowsFit => Math.Max(1, (int)(ActualHeight / cellHeight));

		void OnSessionChanged() => dirty = true;

		void Refresh()
		{
			if (!dirty)
				return;
			dirty = false;
			snapshot = session?.GetSnapshot();
			if (hoveredLink != null) {
				hoveredLink = null; // the text under the pointer may have changed; the next mouse move finds the link again
				Cursor = null;
			}
			InvalidateVisual();
			Updated?.Invoke();
		}

		void MeasureCell()
		{
			var ft = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, Brushes.White, 1.0);
			cellWidth = Math.Max(1, Math.Round(ft.WidthIncludingTrailingWhitespace, 2));
			cellHeight = Math.Max(1, Math.Ceiling(ft.Height));
		}

		protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
		{
			base.OnRenderSizeChanged(sizeInfo);
			FitSession();
		}

		void FitSession()
		{
			if (session == null || ActualWidth < cellWidth || ActualHeight < cellHeight)
				return;
			int columns = session.Columns, rows = session.Rows;
			session.Resize(ColumnsFit, RowsFit, (int)cellWidth, (int)cellHeight);
			if (session.Columns != columns || session.Rows != rows)
				ClearSelection(); // cell coordinates of the old grid mean something else (or nothing) in the new one
			dirty = true;
		}

		protected override Size MeasureOverride(Size availableSize)
			=> new Size(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width,
			            double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

		Brush BrushFor(uint color)
		{
			if (!brushes.TryGetValue(color, out var brush)) {
				brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
				brush.Freeze();
				brushes[color] = brush;
			}
			return brush;
		}

		protected override void OnRender(DrawingContext dc)
		{
			var snap = snapshot;
			var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
			uint defaultBg = snap?.DefaultBackground ?? (TerminalCell.ColorSet | 0x1e1e1e);
			uint defaultFg = snap?.DefaultForeground ?? (TerminalCell.ColorSet | 0xcccccc);
			dc.DrawRectangle(BrushFor(defaultBg), null, new Rect(0, 0, ActualWidth, ActualHeight));
			if (snap == null)
				return;

			for (int y = 0; y < snap.Rows; y++) {
				int x = 0;
				while (x < snap.Columns) {
					var first = snap[x, y];
					if ((first.Flags & CellFlags.Spacer) != 0) {
						x++;
						continue;
					}
					(uint fg, uint bg) = ResolveColors(first, defaultFg, defaultBg);
					bool wide = (first.Flags & CellFlags.Wide) != 0;
					// A wide character is drawn on its own so its two cells stay aligned whatever the font's advance is.
					int end = wide ? Math.Min(snap.Columns, x + 2) : x + 1;
					while (!wide && end < snap.Columns && SameStyle(first, snap[end, y]) && (snap[end, y].Flags & (CellFlags.Wide | CellFlags.Spacer)) == 0)
						end++;
					double left = x * cellWidth, top = y * cellHeight;
					if (bg != defaultBg)
						dc.DrawRectangle(BrushFor(bg), null, new Rect(left, top, (end - x) * cellWidth, cellHeight));
					var text = new StringBuilder(end - x);
					bool any = false;
					for (int i = x; i < (wide ? x + 1 : end); i++) {
						var t = snap[i, y].Text;
						if (t == null) { text.Append(' '); } else { text.Append(t); any = true; }
					}
					if (any && (first.Flags & CellFlags.Invisible) == 0) {
						var ft = new FormattedText(text.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
							(first.Flags & CellFlags.Bold) != 0 ? boldTypeface : typeface, fontSize, BrushFor(fg), pixelsPerDip);
						if ((first.Flags & CellFlags.Underline) != 0)
							ft.SetTextDecorations(TextDecorations.Underline);
						dc.DrawText(ft, new Point(left, top));
					}
					x = end;
				}
			}

			if (matches.Count > 0) {
				ulong top = snap.ScrollbackOffset;
				for (int i = 0; i < matches.Count; i++) {
					var m = matches[i];
					if (m.AbsoluteRow < top || m.AbsoluteRow >= top + (ulong)snap.Rows)
						continue;
					int row = (int)(m.AbsoluteRow - top);
					int left = Math.Clamp(m.StartColumn, 0, snap.Columns - 1), right = Math.Clamp(m.EndColumn, 0, snap.Columns - 1);
					if (right >= left)
						dc.DrawRectangle(i == currentMatch ? CurrentMatchBrush : MatchBrush, null, new Rect(left * cellWidth, row * cellHeight, (right - left + 1) * cellWidth, cellHeight));
				}
			}

			if (flashUntil != default && DateTime.UtcNow < flashUntil)
				dc.DrawRectangle(FlashBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));

			if (hoveredLink != null && hoveredLink.Row < snap.Rows) {
				dc.DrawRectangle(BrushFor(defaultFg), null,
					new Rect(hoveredLink.StartColumn * cellWidth, (hoveredLink.Row + 1) * cellHeight - 1.5, (hoveredLink.EndColumn - hoveredLink.StartColumn + 1) * cellWidth, 1));
			}

			if (selectionStart is { } a && selectionEnd is { } b) {
				var (from, to) = Order(a, b);
				for (int y = Math.Max(0, from.row); y <= Math.Min(snap.Rows - 1, to.row); y++) {
					int left = Math.Clamp(y == from.row ? from.column : 0, 0, snap.Columns - 1);
					int right = Math.Clamp(y == to.row ? to.column : snap.Columns - 1, 0, snap.Columns - 1);
					if (right < left)
						continue;
					dc.DrawRectangle(SelectionBrush, null, new Rect(left * cellWidth, y * cellHeight, (right - left + 1) * cellWidth, cellHeight));
				}
			}

			if (snap.CursorVisible && snap.CursorColumn < snap.Columns && snap.CursorRow < snap.Rows) {
				double left = snap.CursorColumn * cellWidth, top = snap.CursorRow * cellHeight;
				var style = IsKeyboardFocused ? snap.CursorStyle : TerminalCursorStyle.BlockHollow; // an unfocused terminal shows a hollow box whatever the program asked for
				switch (style) {
					case TerminalCursorStyle.Bar:
						dc.DrawRectangle(BrushFor(defaultFg), null, new Rect(left, top, 2, cellHeight));
						break;
					case TerminalCursorStyle.Underline:
						dc.DrawRectangle(BrushFor(defaultFg), null, new Rect(left, top + cellHeight - 2, cellWidth, 2));
						break;
					case TerminalCursorStyle.Block:
						// A solid block with the character under it drawn in the background colour so it stays readable.
						dc.DrawRectangle(BrushFor(defaultFg), null, new Rect(left, top, cellWidth, cellHeight));
						var under = snap[snap.CursorColumn, snap.CursorRow].Text;
						if (!string.IsNullOrWhiteSpace(under)) {
							dc.DrawText(new FormattedText(under, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, fontSize, BrushFor(defaultBg), pixelsPerDip), new Point(left, top));
						}
						break;
					default:
						dc.DrawRectangle(null, new Pen(BrushFor(defaultFg), 1), new Rect(left + 0.5, top + 0.5, cellWidth - 1, cellHeight - 1));
						break;
				}
			}
		}

		static bool SameStyle(TerminalCell a, TerminalCell b) => a.Foreground == b.Foreground && a.Background == b.Background && a.Flags == b.Flags;

		static (uint fg, uint bg) ResolveColors(TerminalCell cell, uint defaultFg, uint defaultBg)
		{
			uint fg = cell.Foreground != 0 ? cell.Foreground : defaultFg;
			uint bg = cell.Background != 0 ? cell.Background : defaultBg;
			return (cell.Flags & CellFlags.Inverse) != 0 ? (bg, fg) : (fg, bg);
		}

		protected override void OnMouseDown(MouseButtonEventArgs e)
		{
			base.OnMouseDown(e);
			Focus();
		}

		(int column, int row) CellAt(Point p)
			=> (Math.Max(0, (int)(p.X / cellWidth)), Math.Max(0, (int)(p.Y / cellHeight)));

		static ((int column, int row) from, (int column, int row) to) Order((int column, int row) a, (int column, int row) b)
			=> (a.row < b.row || (a.row == b.row && a.column <= b.column)) ? (a, b) : (b, a);

		public bool HasSelection => selectionStart != null && selectionEnd != null && selectionStart != selectionEnd;

		public string SelectedText {
			get {
				if (!HasSelection || snapshot == null)
					return "";
				var (from, to) = Order(selectionStart.Value, selectionEnd.Value);
				return snapshot.GetText(from.column, from.row, to.column, to.row);
			}
		}

		public void SelectRange(int startColumn, int startRow, int endColumn, int endRow)
		{
			selectionStart = (startColumn, startRow);
			selectionEnd = (endColumn, endRow);
			InvalidateVisual();
		}

		public void ClearSelection()
		{
			if (selectionStart == null && selectionEnd == null)
				return;
			selectionStart = selectionEnd = null;
			InvalidateVisual();
		}

		/// <summary>Puts the selection on the clipboard; returns its text ("" and no clipboard change without a selection).</summary>
		public string CopySelection()
		{
			var text = SelectedText;
			if (text.Length > 0)
				Clipboard.SetText(text);
			return text;
		}

		public void PasteFromClipboard()
		{
			if (session != null && Clipboard.ContainsText()) {
				ClearSelection();
				session.Paste(Clipboard.GetText());
			}
		}

		int reportedButton = G.MouseButtonNone;
		int wheelRemainder;

		public Point CellCenter(int column, int row) => new Point((column + 0.5) * cellWidth, (row + 0.5) * cellHeight);

		static ushort GhosttyMods(ModifierKeys modifiers)
		{
			ushort mods = 0;
			if ((modifiers & ModifierKeys.Shift) != 0) mods |= 1;
			if ((modifiers & ModifierKeys.Control) != 0) mods |= 2;
			if ((modifiers & ModifierKeys.Alt) != 0) mods |= 4;
			return mods;
		}

		/// <summary>Sends a mouse event to the program (position in view pixels); false when the terminal's mode wants none.</summary>
		public bool ReportMouse(int action, int button, Point position, ModifierKeys modifiers)
		{
			if (session == null)
				return false;
			// The encoder works in whole-pixel cells; the view's cells are fractional DIPs. Find the cell first, then
			// express the position in the encoder's cell size so both agree on which cell was hit.
			int cw = Math.Max(1, (int)Math.Round(cellWidth)), ch = Math.Max(1, (int)Math.Round(cellHeight));
			double fx = Math.Max(0, position.X) / cellWidth, fy = Math.Max(0, position.Y) / cellHeight;
			var bytes = session.EncodeMouse(action, button, GhosttyMods(modifiers), (float)(fx * cw), (float)(fy * ch),
				cw, ch, reportedButton != G.MouseButtonNone);
			if (bytes == null || bytes.Length == 0)
				return false;
			session.WriteBytes(bytes);
			return true;
		}

		void WheelReport(bool up, Point position, ModifierKeys modifiers)
		{
			ReportMouse(G.MouseEventActionPress, up ? G.MouseButtonFour : G.MouseButtonFive, position, modifiers);
		}

		public void Scroll(int lines)
		{
			if (session == null)
				return;
			ClearSelection();
			session.ScrollBy(lines);
		}

		protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonDown(e);
			if (HandleLeftDown(e.GetPosition(this), e.ClickCount, Keyboard.Modifiers))
				e.Handled = true;
		}

		/// <summary>Left button pressed at <paramref name="position"/> (view pixels): report it to a mouse-aware program, else start a selection (double click: word, triple click: line).</summary>
		public bool HandleLeftDown(Point position, int clickCount, ModifierKeys modifiers)
		{
			if (session == null)
				return false;
			if (IsLinkModifier(modifiers) && !session.MouseTrackingEnabled) {
				var linkCell = CellAt(position);
				if (OpenLinkAt(linkCell.column, linkCell.row, launchUrls: true) != null)
					return true; // Ctrl/Cmd+click on a link follows it instead of selecting
			}
			if (session.MouseTrackingEnabled && (modifiers & ModifierKeys.Shift) == 0) { // Shift keeps selection available, as in xterm.
				reportedButton = G.MouseButtonLeft;
				ReportMouse(G.MouseEventActionPress, G.MouseButtonLeft, position, modifiers);
				CaptureMouse();
				return true;
			}
			var cell = CellAt(position);
			if (clickCount >= 3 && snapshot != null && cell.row < snapshot.Rows) {
				SelectRange(0, cell.row, snapshot.Columns - 1, cell.row);
				return false;
			}
			if (clickCount == 2 && snapshot != null && cell.row < snapshot.Rows) {
				int from = Math.Min(cell.column, snapshot.Columns - 1), to = from;
				bool Blank(int x) => string.IsNullOrWhiteSpace(snapshot[x, cell.row].Text);
				if (!Blank(from)) {
					while (from > 0 && !Blank(from - 1)) from--;
					while (to < snapshot.Columns - 1 && !Blank(to + 1)) to++;
					SelectRange(from, cell.row, to, cell.row);
				}
				return false;
			}
			selectionStart = selectionEnd = cell;
			selecting = true;
			CaptureMouse();
			InvalidateVisual();
			return false;
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			UpdateHoveredLink(e.GetPosition(this));
			if (session != null && session.MouseTrackingEnabled && (reportedButton != G.MouseButtonNone || Mouse.LeftButton != MouseButtonState.Pressed)) {
				ReportMouse(G.MouseEventActionMotion, reportedButton, e.GetPosition(this), Keyboard.Modifiers);
				return;
			}
			if (!selecting)
				return;
			var cell = CellAt(e.GetPosition(this));
			if (snapshot != null)
				cell = (Math.Min(cell.column, snapshot.Columns - 1), Math.Min(cell.row, snapshot.Rows - 1));
			selectionEnd = cell;
			InvalidateVisual();
		}

		protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
		{
			base.OnMouseLeftButtonUp(e);
			if (HandleLeftUp(e.GetPosition(this), Keyboard.Modifiers))
				e.Handled = true;
		}

		public bool HandleLeftUp(Point position, ModifierKeys modifiers)
		{
			if (reportedButton != G.MouseButtonNone) {
				ReportMouse(G.MouseEventActionRelease, reportedButton, position, modifiers);
				reportedButton = G.MouseButtonNone;
				ReleaseMouseCapture();
				return true;
			}
			if (!selecting)
				return false;
			selecting = false;
			ReleaseMouseCapture();
			if (!HasSelection)
				ClearSelection(); // a plain click
			return false;
		}

		protected override void OnMouseWheel(MouseWheelEventArgs e)
		{
			base.OnMouseWheel(e);
			if (HandleWheel(e.Delta, e.GetPosition(this), Keyboard.Modifiers))
				e.Handled = true;
		}

		/// <summary>Wheel movement (positive = up, 120 per notch): mouse report, arrow keys on the alternate screen, else scrollback.</summary>
		public bool HandleWheel(int delta, Point position, ModifierKeys modifiers)
		{
			if (session == null)
				return false;
			if ((modifiers & ModifierKeys.Control) != 0 && !session.MouseTrackingEnabled) {
				TerminalSettings.Zoom(delta > 0 ? 1 : -1); // Ctrl+wheel zooms the font, as in the editor
				return true;
			}
			int notches = Math.Max(1, Math.Abs(delta) / 120);
			if (session.MouseTrackingEnabled) {
				for (int i = 0; i < notches; i++)
					WheelReport(delta > 0, position, modifiers);
			} else if (session.IsAlternateScreen) {
				for (int i = 0; i < 3 * notches; i++) // xterm "alternate scroll": the wheel is the arrow keys on the alternate screen
					HandleKey(delta > 0 ? Key.Up : Key.Down, ModifierKeys.None);
			} else {
				// 3 lines per notch (120); precision devices send small deltas, so keep the remainder until it adds up to a line.
				wheelRemainder += delta;
				int lines = wheelRemainder / 40;
				wheelRemainder -= lines * 40;
				if (lines != 0)
					Scroll(-lines); // wheel up scrolls back
			}
			return true;
		}

		protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); dirty = true; }
		protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); dirty = true; }

		protected override void OnTextInput(TextCompositionEventArgs e)
		{
			base.OnTextInput(e);
			if (session != null && !string.IsNullOrEmpty(e.Text)) {
				session.ScrollToBottom();
				ClearSelection();
				session.Send(e.Text);
				e.Handled = true;
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers))
				e.Handled = true;
		}

		/// <summary>
		/// Sends a key to the shell if it is one the terminal owns: non-text keys always, text keys only
		/// with Ctrl/Alt (plain typing arrives through <see cref="OnTextInput"/>). Cmd/Windows combinations
		/// stay with the IDE.
		/// </summary>
		public bool HandleKey(Key key, ModifierKeys modifiers)
		{
			if (session == null)
				return false;
			bool command = (modifiers & ModifierKeys.Windows) != 0;
			bool chord = command || modifiers == (ModifierKeys.Control | ModifierKeys.Shift); // Cmd+C/V, Ctrl+Shift+C/V
			if (chord && key == Key.C) {
				CopySelection();
				return HasSelection || command;
			}
			if (chord && (key == Key.OemPlus || key == Key.Add)) {
				TerminalSettings.Zoom(1);
				return true;
			}
			if (chord && (key == Key.OemMinus || key == Key.Subtract)) {
				TerminalSettings.Zoom(-1);
				return true;
			}
			if (chord && (key == Key.D0 || key == Key.NumPad0)) {
				TerminalSettings.Zoom(0);
				return true;
			}
			if (chord && key == Key.F) {
				FindRequested?.Invoke();
				return true;
			}
			if (chord && key == Key.V) {
				PasteFromClipboard();
				return true;
			}
			if (command && modifiers == ModifierKeys.Windows && key == Key.A && OperatingSystem.IsMacOS()) { // Cmd+A selects the whole screen, as the menu's Select All does
				if (snapshot != null)
					SelectRange(0, 0, snapshot.Columns - 1, snapshot.Rows - 1);
				return true;
			}
			if (command && modifiers == ModifierKeys.Windows && key == Key.K && OperatingSystem.IsMacOS()) { // Cmd+K clears, as in VS Code on macOS
				session.Clear();
				ClearSelection();
				return true;
			}
			if (modifiers == ModifierKeys.Shift && (key == Key.PageUp || key == Key.PageDown) && !session.IsAlternateScreen) { // Shift+PageUp/Down page through the scrollback
				Scroll(key == Key.PageUp ? -Math.Max(1, session.Rows - 1) : Math.Max(1, session.Rows - 1));
				return true;
			}
			if (modifiers == ModifierKeys.Shift && (key == Key.Home || key == Key.End) && !session.IsAlternateScreen) { // Shift+Home/End jump to the top/bottom of the scrollback
				if (key == Key.Home)
					Scroll(-int.MaxValue);
				else {
					ClearSelection();
					session.ScrollToBottom();
				}
				return true;
			}
			if (session.HasExited) {
				// Nothing is listening any more: a key (other than a bare modifier) dismisses the finished terminal.
				if (key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System))
					CloseRequested?.Invoke();
				return true;
			}
			if (OperatingSystem.IsMacOS() && TerminalKeys.MacLineEditing(key == Key.System ? Key.None : key, modifiers) is { } macSequence) {
				session.ScrollToBottom();
				ClearSelection();
				session.Send(macSequence);
				return true;
			}
			if (command || !TerminalKeys.TryMap(key, out int ghosttyKey, out string text, out bool isText))
				return false;
			bool ctrlOrAlt = (modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
			if (isText && !ctrlOrAlt)
				return false;
			session.ScrollToBottom();
			ClearSelection();
			ushort mods = 0;
			if ((modifiers & ModifierKeys.Shift) != 0) mods |= 1;
			if ((modifiers & ModifierKeys.Control) != 0) mods |= 2;
			if ((modifiers & ModifierKeys.Alt) != 0) mods |= 4;
			if (isText && (modifiers & ModifierKeys.Shift) != 0)
				text = text.ToUpperInvariant();
			var bytes = session.EncodeKey(ghosttyKey, mods, text, text != null ? char.ToLowerInvariant(text[0]) : 0u);
			if (bytes == null || bytes.Length == 0)
				return false;
			// libghostty leaves Option as a composing key on macOS; shells expect Alt to be an ESC prefix.
			if (isText && (modifiers & ModifierKeys.Alt) != 0 && bytes[0] != 0x1b)
				bytes = new byte[] { 0x1b }.Concat(bytes).ToArray();
			session.WriteBytes(bytes);
			return true;
		}
	}
}
