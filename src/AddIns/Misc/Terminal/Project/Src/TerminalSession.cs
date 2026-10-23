using System.Runtime.InteropServices;
using System.Text;
using Porta.Pty;
using G = LibGhostty.GhosttyNativeLibrary;

namespace OpenDevelop.Terminal
{
	/// <summary>
	/// One shell in one PTY, fed through a libghostty-vt terminal. The PTY reader runs on a
	/// background task; every touch of the native terminal/render state happens under <see cref="gate"/>.
	/// </summary>
	sealed class TerminalSession : IDisposable
	{
		static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

		readonly object gate = new object();
		// Serialises PTY writes: the terminal's own replies (written from inside TerminalWrite, under `gate`, on the read
		// thread) and user input (UI thread, deliberately not under `gate`) share one stream. Never taken while holding it.
		readonly object writeLock = new object();
		readonly G native;
		readonly IPtyConnection pty;
		readonly IntPtr terminal;
		readonly IntPtr renderState;
		readonly IntPtr rowIterator;
		readonly IntPtr rowCells;
		readonly G.WritePtyCallback writePtyCallback;
		readonly G.TitleChangedCallback titleChangedCallback;
		readonly G.TitleChangedCallback bellCallback; // the bell callback has the same shape as the title one: void (terminal, userdata)
		int bellCount;
		IntPtr keyEncoder, keyEvent;
		IntPtr mouseEncoder, mouseEvent;
		byte[] modeTail = Array.Empty<byte>();
		// Completed when the PTY output has been read to its end: the exit notice must not overtake the last output.
		readonly TaskCompletionSource readFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bool disposed;

		TerminalSession(G native, IPtyConnection pty, int columns, int rows)
		{
			this.native = native;
			this.pty = pty;
			Columns = columns;
			Rows = rows;
			Check(native.TerminalNew(new G.TerminalOptions { Columns = (ushort)columns, Rows = (ushort)rows, MaxScrollback = ScrollbackBytes(TerminalSettings.ScrollbackLines, columns) }, out terminal), "TerminalNew");
			Check(native.RenderStateNew(out renderState), "RenderStateNew");
			Check(native.RowIteratorNew(out rowIterator), "RowIteratorNew");
			Check(native.RowCellsNew(out rowCells), "RowCellsNew");
			// Replies the terminal generates itself (device attributes, cursor position reports...).
			writePtyCallback = (_, _, data, length) => {
				var bytes = new byte[(int)length];
				Marshal.Copy(data, bytes, 0, bytes.Length);
				WriteBytes(bytes);
			};
			titleChangedCallback = (_, _) => {
				if (native.TerminalGetTitle(terminal, out var title) == G.Success && title.Pointer != IntPtr.Zero)
					Title = Marshal.PtrToStringUTF8(title.Pointer, (int)title.Length);
				// No Changed here: this runs inside TerminalWrite under `gate`, and ReadLoop raises Changed once the lock is released.
			};
			bellCallback = (_, _) => Interlocked.Increment(ref bellCount);
			native.TerminalSet(terminal, TerminalOptionBell, G.GetCallbackPointer(bellCallback));
			native.TerminalSet(terminal, G.TerminalOptionWritePty, G.GetCallbackPointer(writePtyCallback));
			native.TerminalSet(terminal, G.TerminalOptionTitleChanged, G.GetCallbackPointer(titleChangedCallback));
		}

		/// <summary>libghostty limits scrollback in bytes (rounded up to whole pages), not lines: about 16 bytes per cell, 0 for none.</summary>
		static UIntPtr ScrollbackBytes(int lines, int columns) => (UIntPtr)((ulong)Math.Max(0, lines) * (ulong)Math.Max(columns, 80) * 16UL);

		public int Columns { get; private set; }
		public int Rows { get; private set; }
		public string Title { get; private set; } = "";
		public string ShellName { get; private set; } = "";
		public string ShellPath { get; private set; } = "";
		/// <summary>The folder this shell was started in.</summary>
		public string InitialDirectory { get; private set; } = "";

		/// <summary>
		/// Where the shell is now (asked of the operating system, so it works whatever the shell does), or the start folder
		/// when that cannot be read (an ended process, or Windows).
		/// </summary>
		public string CurrentDirectory => (HasExited ? null : TerminalProcessInfo.GetWorkingDirectory(Pid)) ?? InitialDirectory;
		public int Pid => pty.Pid;
		/// <summary>The program asked for bracketed paste (DECSET 2004).</summary>
		public bool BracketedPaste { get; private set; }
		public bool HasExited { get; private set; }
		public int ExitCode { get; private set; }

		/// <summary>Raised on a background thread whenever the screen may have changed.</summary>
		public event Action Changed;

		/// <param name="colors">Taken by the caller on the UI thread (theme resources are not readable here).</param>
		public static async Task<TerminalSession> StartAsync(string shell, string workingDirectory, int columns, int rows, TerminalColors colors)
		{
			var native = new G(FindNativeLibrary());
			var environment = new Dictionary<string, string>();
			foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
				environment[(string)e.Key] = (string)e.Value;
			environment["TERM"] = "xterm-256color";
			environment["COLORTERM"] = "truecolor";
			environment["TERM_PROGRAM"] = "OpenDevelop";
			if (!OperatingSystem.IsWindows() && !environment.ContainsKey("LANG"))
				environment["LANG"] = "en_US.UTF-8";

			var options = new PtyOptions {
				Name = "OpenDevelop",
				Cols = columns,
				Rows = rows,
				Cwd = workingDirectory,
				App = shell,
				CommandLine = OperatingSystem.IsWindows() ? Array.Empty<string>() : new[] { "-l" },
				Environment = environment,
			};
			IPtyConnection pty;
			try {
				pty = await PtyProvider.SpawnAsync(options, CancellationToken.None).ConfigureAwait(false);
			} catch {
				native.Dispose();
				throw;
			}
			TerminalSession session;
			try {
				session = new TerminalSession(native, pty, columns, rows) { ShellName = Path.GetFileName(shell), ShellPath = shell, InitialDirectory = workingDirectory ?? "" };
			} catch {
				try {
					pty.Kill();
				} catch (Exception ex) when (ex is InvalidOperationException or IOException) {
				}
				(pty as IDisposable)?.Dispose();
				native.Dispose();
				throw;
			}
			pty.ProcessExited += (_, e) => {
				session.ExitCode = e.ExitCode;
				session.HasExited = true;
				session.Changed?.Invoke();
				_ = session.AnnounceExitAsync(e.ExitCode);
			};
			session.ApplyColors(colors);
			_ = Task.Run(session.ReadLoop);
			return session;
		}

		public static string DefaultShell()
		{
			return TerminalShells.Default;
		}

		static TerminalSession()
		{
			// Porta.Pty installs its own resolver for conpty.dll on Windows. Registering a second
			// resolver for the same assembly makes PseudoConsole's static constructor fail.
			if (OperatingSystem.IsWindows())
				return;
			// Porta.Pty imports its helper with DllImportSearchPath.AssemblyDirectory, i.e. next to
			// the add-in DLL, but NuGet leaves it under runtimes/<rid>/native/.
			NativeLibrary.SetDllImportResolver(typeof(PtyProvider).Assembly, (name, assembly, searchPath) => {
				if (!name.Contains("porta_pty", StringComparison.Ordinal))
					return IntPtr.Zero;
				string file = OperatingSystem.IsMacOS() ? "libporta_pty.dylib" : "libporta_pty.so";
				string candidate = Path.Combine(AddInDirectory, "runtimes", RuntimeId, "native", file);
				return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
			});
		}

		static string AddInDirectory => Path.GetDirectoryName(typeof(TerminalSession).Assembly.Location);

		static string RuntimeId => OperatingSystem.IsWindows() ? "win-x64"
			: (OperatingSystem.IsMacOS() ? "osx-" : "linux-") + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");

		static string FindNativeLibrary()
		{
			string rid = RuntimeId;
			string file = OperatingSystem.IsWindows() ? "ghostty-vt.dll" : OperatingSystem.IsMacOS() ? "libghostty-vt.dylib" : "libghostty-vt.so";
			string addinDir = AddInDirectory;
			foreach (var nativeDir in new[] { Path.Combine(addinDir, "runtimes", rid, "native"), addinDir }) {
				var candidate = Path.Combine(nativeDir, file);
				if (File.Exists(candidate)) {
					return candidate;
				}
			}
			throw new FileNotFoundException("libghostty-vt was not deployed next to the Terminal add-in", file);
		}

		async Task ReadLoop()
		{
			try {
				await ReadUntilEndAsync().ConfigureAwait(false);
			} finally {
				readFinished.TrySetResult();
			}
		}

		async Task ReadUntilEndAsync()
		{
			var buffer = new byte[16 * 1024];
			while (!disposed) {
				int n;
				try {
					n = await pty.ReaderStream.ReadAsync(buffer).ConfigureAwait(false);
				} catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
					break;
				}
				if (n <= 0)
					break;
				lock (gate) {
					if (disposed)
						return;
					TrackModes(buffer, n);
					unsafe {
						fixed (byte* p = buffer)
							native.TerminalWrite(terminal, (IntPtr)p, (UIntPtr)n);
					}
				}
				Changed?.Invoke();
			}
			Changed?.Invoke();
		}

		/// <summary>
		/// Encodes one key press the way the terminal currently wants it (application cursor keys,
		/// kitty/modifyOtherKeys modes...), using libghostty's encoder bound to this terminal's modes.
		/// </summary>
		/// <param name="key">A <c>G.Key*</c> physical key.</param>
		/// <param name="mods">Ghostty modifier bits: shift 1, ctrl 2, alt 4.</param>
		/// <param name="text">The character the key types without modifiers, or null for non-text keys.</param>
		public byte[] EncodeKey(int key, ushort mods, string text, uint unshiftedCodepoint)
		{
			lock (gate) {
				if (disposed)
					return null;
				if (keyEncoder == IntPtr.Zero) {
					Check(native.KeyEncoderNew(out keyEncoder), "KeyEncoderNew");
					Check(native.KeyEventNew(out keyEvent), "KeyEventNew");
				}
				native.KeyEncoderSetFromTerminal(keyEncoder, terminal);
				native.KeyEventSetAction(keyEvent, G.KeyActionPress);
				native.KeyEventSetKey(keyEvent, key);
				native.KeyEventSetMods(keyEvent, mods);
				native.KeyEventSetConsumedMods(keyEvent, 0);
				native.KeyEventSetUnshiftedCodepoint(keyEvent, unshiftedCodepoint);
				IntPtr utf8 = IntPtr.Zero;
				int utf8Length = 0;
				if (!string.IsNullOrEmpty(text)) {
					utf8Length = Utf8.GetByteCount(text);
					utf8 = Marshal.StringToCoTaskMemUTF8(text);
				}
				var buffer = Marshal.AllocHGlobal(64);
				try {
					native.KeyEventSetUtf8(keyEvent, utf8, (UIntPtr)utf8Length);
					if (native.KeyEncoderEncode(keyEncoder, keyEvent, buffer, (UIntPtr)64, out var written) != G.Success)
						return null;
					var bytes = new byte[(int)written];
					Marshal.Copy(buffer, bytes, 0, bytes.Length);
					return bytes;
				} finally {
					Marshal.FreeHGlobal(buffer);
					if (utf8 != IntPtr.Zero)
						Marshal.FreeCoTaskMem(utf8);
				}
			}
		}

		static readonly byte[] BracketedOn = Utf8.GetBytes("\u001b[?2004h"), BracketedOff = Utf8.GetBytes("\u001b[?2004l");

		/// <summary>libghostty exposes no mode query for bracketed paste, so watch the output for DECSET/DECRST 2004.</summary>
		void TrackModes(byte[] buffer, int count)
		{
			var window = new byte[modeTail.Length + count];
			modeTail.CopyTo(window, 0);
			Array.Copy(buffer, 0, window, modeTail.Length, count);
			int on = window.AsSpan().LastIndexOf(BracketedOn), off = window.AsSpan().LastIndexOf(BracketedOff);
			if (on >= 0 || off >= 0)
				BracketedPaste = on > off;
			modeTail = window.AsSpan(Math.Max(0, window.Length - (BracketedOn.Length - 1))).ToArray();
		}

		// GHOSTTY_TERMINAL_OPT_BELL: called for a BEL in the output, not for the BEL that ends an OSC sequence (checked in a spike).
		const int TerminalOptionBell = 2;

		/// <summary>How many times a program has rung the bell (BEL) in this terminal.</summary>
		public int BellCount => Volatile.Read(ref bellCount);

		// GHOSTTY_TERMINAL_OPT_COLOR_PALETTE: the C API takes 256 RGB triples (found by testing: setting entry 1
		// to 123456 made SGR 31 resolve to 123456). The managed binding only names the foreground/background options.
		const int TerminalOptionColorPalette = 14;

		/// <summary>Sets the default foreground/background and the 256-colour palette; cells already on screen pick them up on the next snapshot.</summary>
		public void ApplyColors(TerminalColors colors)
		{
			var palette = Marshal.AllocHGlobal(256 * 3);
			var foreground = Marshal.AllocHGlobal(3);
			var background = Marshal.AllocHGlobal(3);
			try {
				Marshal.Copy(colors.PaletteBytes(), 0, palette, 256 * 3);
				Marshal.Copy(TerminalColors.Rgb(colors.Foreground), 0, foreground, 3);
				Marshal.Copy(TerminalColors.Rgb(colors.Background), 0, background, 3);
				lock (gate) {
					if (disposed)
						return;
					native.TerminalSet(terminal, TerminalOptionColorPalette, palette);
					native.TerminalSet(terminal, G.TerminalOptionColorForeground, foreground);
					native.TerminalSet(terminal, G.TerminalOptionColorBackground, background);
				}
			} finally {
				Marshal.FreeHGlobal(palette);
				Marshal.FreeHGlobal(foreground);
				Marshal.FreeHGlobal(background);
			}
			Changed?.Invoke();
		}

		/// <summary>Whether the program wants mouse reports (then the view must not use the mouse itself).</summary>
		public bool MouseTrackingEnabled {
			get {
				lock (gate)
					return !disposed && native.TerminalGetMouseTracking(terminal, out var tracking) == G.Success && tracking != 0;
			}
		}

		/// <summary>The program is on the alternate screen (vim, less, htop).</summary>
		public bool IsAlternateScreen {
			get {
				lock (gate)
					return !disposed && native.TerminalGetActiveScreen(terminal, out var screen) == G.Success && screen == G.TerminalScreenAlternate;
			}
		}

		/// <summary>
		/// Encodes a mouse event for a program that asked for mouse reports (xterm/SGR/urxvt, whichever the
		/// program enabled); null when the terminal's current mode wants nothing for this event.
		/// </summary>
		/// <param name="action"><c>G.MouseEventAction*</c>.</param>
		/// <param name="button"><c>G.MouseButton*</c>, or <c>G.MouseButtonNone</c> for plain motion.</param>
		/// <param name="mods">Ghostty modifier bits: shift 1, ctrl 2, alt 4.</param>
		/// <param name="x">Pointer position in pixels from the view's left edge.</param>
		/// <param name="y">Pointer position in pixels from the view's top edge.</param>
		public byte[] EncodeMouse(int action, int button, ushort mods, float x, float y, int cellWidth, int cellHeight, bool anyButtonPressed)
		{
			lock (gate) {
				if (disposed)
					return null;
				if (mouseEncoder == IntPtr.Zero) {
					Check(native.MouseEncoderNew(out mouseEncoder), "MouseEncoderNew");
					Check(native.MouseEventNew(out mouseEvent), "MouseEventNew");
				}
				native.MouseEncoderSetFromTerminal(mouseEncoder, terminal);
				var size = new G.NativeMouseEncoderSize {
					Size = (UIntPtr)Marshal.SizeOf<G.NativeMouseEncoderSize>(),
					ScreenWidth = (uint)(Columns * cellWidth), ScreenHeight = (uint)(Rows * cellHeight),
					CellWidth = (uint)cellWidth, CellHeight = (uint)cellHeight,
				};
				native.MouseEncoderSetOption(mouseEncoder, G.MouseEncoderOptionSize, ref size);
				native.MouseEncoderSetByteOption(mouseEncoder, G.MouseEncoderOptionAnyButtonPressed, anyButtonPressed ? (byte)1 : (byte)0);
				native.MouseEventSetAction(mouseEvent, action);
				if (button == G.MouseButtonNone)
					native.MouseEventClearButton(mouseEvent);
				else
					native.MouseEventSetButton(mouseEvent, button);
				native.MouseEventSetMods(mouseEvent, mods);
				native.MouseEventSetPosition(mouseEvent, new G.NativeMousePosition { X = x, Y = y });
				var buffer = Marshal.AllocHGlobal(64);
				try {
					if (native.MouseEncoderEncode(mouseEncoder, mouseEvent, buffer, (UIntPtr)64, out var written) != G.Success || (int)written == 0)
						return null;
					var bytes = new byte[(int)written];
					Marshal.Copy(buffer, bytes, 0, bytes.Length);
					return bytes;
				} finally {
					Marshal.FreeHGlobal(buffer);
				}
			}
		}

		/// <summary>
		/// Every occurrence of <paramref name="query"/> in the screen and its scrollback, oldest first (one row at a time: a
		/// match cannot span a wrapped line). It pages the viewport through the scrollback under the lock and puts the viewport back.
		/// </summary>
		public IReadOnlyList<TerminalMatch> Search(string query, bool matchCase)
		{
			var matches = new List<TerminalMatch>();
			if (string.IsNullOrEmpty(query))
				return matches;
			var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
			lock (gate) {
				if (disposed)
					return matches;
				native.TerminalGetScrollbar(terminal, out var original);
				bool atBottom = original.Offset + original.Length >= original.Total;
				native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportTop });
				ulong next = 0; // the first absolute row not scanned yet: the last page may overlap the one before
				for (int pages = 0; pages < 100_000; pages++) {
					var snapshot = GetSnapshot();
					ulong top = snapshot.ScrollbackOffset;
					for (int row = 0; row < snapshot.Rows; row++) {
						if (top + (ulong)row < next)
							continue;
						var text = snapshot.GetRowText(row, out var columns);
						for (int index = text.IndexOf(query, comparison); index >= 0; index = text.IndexOf(query, index + query.Length, comparison))
							matches.Add(new TerminalMatch(top + (ulong)row, columns[index], columns[index + query.Length - 1], text.Substring(index, query.Length)));
					}
					next = top + (ulong)snapshot.Rows;
					if (top + snapshot.ScrollbackLength >= snapshot.ScrollbackTotal)
						break;
					native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportDelta, Value = new G.TerminalScrollViewportValue { Delta = (IntPtr)snapshot.Rows } });
				}
				if (atBottom)
					native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportBottom });
				else {
					native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportTop });
					native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportDelta, Value = new G.TerminalScrollViewportValue { Delta = (IntPtr)(long)original.Offset } });
				}
			}
			return matches;
		}

		/// <summary>Scrolls so that the given absolute row (see <see cref="TerminalMatch"/>) is in the middle of the viewport, or as near as the scrollback allows.</summary>
		public void ScrollToRow(ulong absoluteRow)
		{
			lock (gate) {
				if (disposed)
					return;
				native.TerminalGetScrollbar(terminal, out var scrollbar);
				ulong maxTop = scrollbar.Total > scrollbar.Length ? scrollbar.Total - scrollbar.Length : 0;
				ulong want = Math.Min(absoluteRow > (ulong)(Rows / 2) ? absoluteRow - (ulong)(Rows / 2) : 0, maxTop);
				long delta = (long)want - (long)scrollbar.Offset;
				if (delta != 0)
					native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportDelta, Value = new G.TerminalScrollViewportValue { Delta = (IntPtr)delta } });
			}
			Changed?.Invoke();
		}

		public void ScrollBy(int lines)
		{
			lock (gate) {
				if (disposed)
					return;
				native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportDelta, Value = new G.TerminalScrollViewportValue { Delta = (IntPtr)lines } });
			}
			Changed?.Invoke();
		}

		public void ScrollToBottom()
		{
			lock (gate) {
				if (disposed)
					return;
				native.ScrollViewport(terminal, new G.TerminalScrollViewport { Tag = G.ScrollViewportBottom });
			}
			Changed?.Invoke();
		}

		/// <summary>Pastes text the way a terminal should: newlines as CR, and wrapped in the bracketed-paste markers when the program asked for them (with ESC stripped so the text cannot end the paste early).</summary>
		public void Paste(string text)
		{
			text = text.Replace("\r\n", "\r").Replace('\n', '\r');
			ScrollToBottom();
			if (BracketedPaste)
				Send("\u001b[200~" + text.Replace("\u001b", "") + "\u001b[201~");
			else
				Send(text);
		}

		public void Send(string text) => WriteBytes(Utf8.GetBytes(text));

		public void WriteBytes(byte[] bytes)
		{
			if (disposed || HasExited)
				return;
			try {
				lock (writeLock) {
					pty.WriterStream.Write(bytes, 0, bytes.Length);
					pty.WriterStream.Flush();
				}
			} catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
			}
		}

		public void Resize(int columns, int rows, int cellWidth, int cellHeight)
		{
			columns = Math.Max(2, columns);
			rows = Math.Max(1, rows);
			lock (gate) {
				if (disposed || (columns == Columns && rows == Rows))
					return;
				Columns = columns;
				Rows = rows;
				native.TerminalResize(terminal, (ushort)columns, (ushort)rows, (uint)Math.Max(1, cellWidth), (uint)Math.Max(1, cellHeight));
			}
			try {
				pty.Resize(columns, rows);
			} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
			}
		}

		async Task AnnounceExitAsync(int exitCode)
		{
			// Normally the output ends right after the process does; if something else still holds the PTY open, do not wait for it forever.
			await Task.WhenAny(readFinished.Task, Task.Delay(1000)).ConfigureAwait(false);
			WriteLocal($"\r\n[Process exited with code {exitCode}]\r\n");
			Changed?.Invoke();
		}

		/// <summary>Writes text straight into the terminal's screen (not to the shell): messages from the terminal itself.</summary>
		void WriteLocal(string text)
		{
			lock (gate) {
				if (disposed)
					return;
				var bytes = Utf8.GetBytes(text);
				unsafe {
					fixed (byte* p = bytes)
						native.TerminalWrite(terminal, (IntPtr)p, (UIntPtr)bytes.Length);
				}
			}
		}

		public void Clear()
		{
			// ESC c would reset the shell prompt state too; clear screen + scrollback and home the cursor,
			// then let the shell redraw its prompt on the next Enter.
			lock (gate) {
				var seq = Utf8.GetBytes("\u001b[2J\u001b[3J\u001b[H");
				unsafe {
					fixed (byte* p = seq)
						native.TerminalWrite(terminal, (IntPtr)p, (UIntPtr)seq.Length);
				}
			}
			Send("\u000c"); // Ctrl+L: redraw the prompt.
			Changed?.Invoke();
		}

		public void Kill()
		{
			try {
				pty.Kill();
			} catch (Exception ex) when (ex is InvalidOperationException or IOException) {
			}
		}

		public TerminalSnapshot GetSnapshot()
		{
			lock (gate) {
				if (disposed)
					return null;
				Check(native.RenderStateUpdate(renderState, terminal), "RenderStateUpdate");
				native.RenderStateGetU16(renderState, G.RenderStateDataCols, out var cols);
				native.RenderStateGetU16(renderState, G.RenderStateDataRows, out var rows);
				var cells = new TerminalCell[cols * rows];
				var continues = new bool[rows];
				var scratch = Marshal.AllocHGlobal(16 * sizeof(int));
				try {
					var iterator = rowIterator;
					Check(native.RenderStateGetHandle(renderState, G.RenderStateDataRowIterator, ref iterator), "row iterator");
					var rowCellsHandle = rowCells;
					int y = 0;
					while (y < rows && native.RowIteratorNext(iterator)) {
						if (native.RowGetRaw(iterator, out var rawRow) == G.Success && native.RowGetWrapContinuation(rawRow, out var wrap) == G.Success)
							continues[y] = wrap != 0;
						Check(native.RowGetCells(iterator, ref rowCellsHandle), "RowGetCells");
						for (ushort x = 0; x < cols; x++) {
							native.RowCellsSelect(rowCellsHandle, x);
							cells[y * cols + x] = ReadCell(rowCellsHandle, scratch);
						}
						y++;
					}
				} finally {
					Marshal.FreeHGlobal(scratch);
				}
				native.RenderStateGetColor(renderState, G.RenderStateDataColorForeground, out var fg);
				native.RenderStateGetColor(renderState, G.RenderStateDataColorBackground, out var bg);
				native.RenderStateGetInt(renderState, G.RenderStateDataCursorVisualStyle, out var cursorStyle);
				native.RenderStateGetByte(renderState, G.RenderStateDataCursorVisible, out var visible);
				native.RenderStateGetByte(renderState, G.RenderStateDataCursorViewportHasValue, out var hasCursor);
				native.RenderStateGetU16(renderState, G.RenderStateDataCursorViewportX, out var cx);
				native.RenderStateGetU16(renderState, G.RenderStateDataCursorViewportY, out var cy);
				native.TerminalGetScrollbar(terminal, out var scrollbar);
				return new TerminalSnapshot {
					ScrollbackTotal = scrollbar.Total, ScrollbackOffset = scrollbar.Offset, ScrollbackLength = scrollbar.Length,
					Columns = cols, Rows = rows, Cells = cells, RowContinues = continues,
					DefaultForeground = Pack(fg), DefaultBackground = Pack(bg),
					CursorVisible = visible != 0 && hasCursor != 0, CursorStyle = Enum.IsDefined(typeof(TerminalCursorStyle), cursorStyle) ? (TerminalCursorStyle)cursorStyle : TerminalCursorStyle.Block, CursorColumn = cx, CursorRow = cy,
				};
			}
		}

		TerminalCell ReadCell(IntPtr cell, IntPtr scratch)
		{
			var shape = CellFlags.None;
			if (native.RowCellsGetRaw(cell, out var raw) == G.Success && native.CellGetWide(raw, out var wide) == G.Success) {
				if (wide >= 2)
					return new TerminalCell(null, 0, 0, CellFlags.Spacer); // Right half of a wide character.
				if (wide == 1)
					shape = CellFlags.Wide;
			}

			string text = null;
			native.RowCellsGetU32(cell, G.RenderStateRowCellsDataGraphemesLength, out var length);
			if (length > 0 && length <= 16) {
				native.RowCellsGetBuffer(cell, G.RenderStateRowCellsDataGraphemesBuffer, scratch);
				var codePoints = new int[length];
				Marshal.Copy(scratch, codePoints, 0, (int)length);
				var sb = new StringBuilder();
				foreach (var cp in codePoints)
					sb.Append(char.ConvertFromUtf32(cp));
				text = sb.ToString();
			}

			uint fg = native.RowCellsGetColor(cell, G.RenderStateRowCellsDataForegroundColor, out var f) == G.Success ? Pack(f) : 0;
			uint bg = native.RowCellsGetColor(cell, G.RenderStateRowCellsDataBackgroundColor, out var b) == G.Success ? Pack(b) : 0;
			var flags = shape;
			if (native.RowCellsGetStyle(cell, out var style) == G.Success) {
				if (style.Bold != 0) flags |= CellFlags.Bold;
				if (style.Italic != 0) flags |= CellFlags.Italic;
				if (style.Inverse != 0) flags |= CellFlags.Inverse;
				if (style.Underline != 0) flags |= CellFlags.Underline;
				if (style.Faint != 0) flags |= CellFlags.Faint;
				if (style.Strikethrough != 0) flags |= CellFlags.Strikethrough;
				if (style.Invisible != 0) flags |= CellFlags.Invisible;
			}
			return new TerminalCell(text, fg, bg, flags);
		}

		static uint Pack(G.NativeColor c) => TerminalCell.ColorSet | (uint)(c.Red << 16 | c.Green << 8 | c.Blue);

		static void Check(int result, string what)
		{
			if (result != G.Success)
				throw new InvalidOperationException($"libghostty {what} failed: {result}");
		}

		public void Dispose()
		{
			lock (gate) {
				if (disposed)
					return;
				disposed = true;
			}
			Kill();
			lock (gate) {
				if (keyEncoder != IntPtr.Zero) {
					native.KeyEventFree(keyEvent);
					native.KeyEncoderFree(keyEncoder);
				}
				if (mouseEncoder != IntPtr.Zero) {
					native.MouseEventFree(mouseEvent);
					native.MouseEncoderFree(mouseEncoder);
				}
				native.RowCellsFree(rowCells);
				native.RowIteratorFree(rowIterator);
				native.RenderStateFree(renderState);
				native.TerminalFree(terminal);
			}
			(pty as IDisposable)?.Dispose();
			native.Dispose();
		}
	}
}
