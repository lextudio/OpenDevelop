namespace OpenDevelop.Terminal
{
	[Flags]
	enum CellFlags : ushort
	{
		None = 0,
		Bold = 1,
		Italic = 2,
		Inverse = 4,
		Underline = 8,
		Faint = 16,
		Strikethrough = 32,
		Invisible = 64,
		/// <summary>A double-width character; the next cell is its <see cref="Spacer"/>.</summary>
		Wide = 128,
		/// <summary>The right half of a wide character: drawn by the cell before it, no text of its own.</summary>
		Spacer = 256,
	}

	/// <summary>Colour as 0xRRGGBB plus a "set" bit (0x1000000); 0 means the terminal default.</summary>
	readonly record struct TerminalCell(string Text, uint Foreground, uint Background, CellFlags Flags)
	{
		public const uint ColorSet = 0x1000000;
	}

	/// <summary>An immutable copy of the visible grid, safe to read on the UI thread while the PTY thread keeps writing.</summary>
	sealed class TerminalSnapshot
	{
		public int Columns { get; init; }
		public int Rows { get; init; }
		/// <summary>Per row: it continues the previous row (a long line the terminal wrapped), so they are one logical line.</summary>
		public bool[] RowContinues { get; init; } = Array.Empty<bool>();
		public TerminalCell[] Cells { get; init; } = Array.Empty<TerminalCell>();
		public uint DefaultForeground { get; init; }
		public uint DefaultBackground { get; init; }
		public bool CursorVisible { get; init; }
		public int CursorColumn { get; init; }
		public int CursorRow { get; init; }
		public ulong ScrollbackTotal { get; init; }
		public ulong ScrollbackOffset { get; init; }
		public ulong ScrollbackLength { get; init; }

		public TerminalCell this[int column, int row] => Cells[row * Columns + column];

		public string GetRowText(int row)
		{
			var sb = new System.Text.StringBuilder(Columns);
			for (int x = 0; x < Columns; x++) {
				var cell = this[x, row];
				if ((cell.Flags & CellFlags.Spacer) == 0)
					sb.Append(cell.Text ?? " ");
			}
			return sb.ToString().TrimEnd();
		}

		/// <summary>A row's text untrimmed, with for every character the column of the cell it came from (a wide character's spacer has none).</summary>
		public string GetRowText(int row, out int[] columns)
		{
			var sb = new System.Text.StringBuilder(Columns);
			var map = new List<int>(Columns);
			for (int x = 0; x < Columns; x++) {
				var cell = this[x, row];
				if ((cell.Flags & CellFlags.Spacer) != 0)
					continue;
				var text = cell.Text ?? " ";
				sb.Append(text);
				for (int i = 0; i < text.Length; i++)
					map.Add(x);
			}
			columns = map.ToArray();
			return sb.ToString();
		}

		/// <summary>Text between two cells (inclusive, either order), rows joined by newlines, trailing blanks trimmed.</summary>
		public string GetText(int startColumn, int startRow, int endColumn, int endRow)
		{
			if (startRow > endRow || (startRow == endRow && startColumn > endColumn))
				(startColumn, startRow, endColumn, endRow) = (endColumn, endRow, startColumn, startRow);
			startRow = Math.Clamp(startRow, 0, Rows - 1);
			endRow = Math.Clamp(endRow, 0, Rows - 1);
			var lines = new List<string>();
			for (int y = startRow; y <= endRow; y++) {
				int from = y == startRow ? Math.Clamp(startColumn, 0, Columns - 1) : 0;
				int to = y == endRow ? Math.Clamp(endColumn, 0, Columns - 1) : Columns - 1;
				var sb = new System.Text.StringBuilder();
				for (int x = from; x <= to; x++) {
					var cell = this[x, y];
					if ((cell.Flags & CellFlags.Spacer) == 0)
						sb.Append(cell.Text ?? " ");
				}
				var text = sb.ToString();
				bool continuesNext = y < endRow && RowContinues.Length > y + 1 && RowContinues[y + 1];
				if (y > startRow && RowContinues.Length > y && RowContinues[y] && lines.Count > 0) {
					lines[^1] += continuesNext ? text : text.TrimEnd(); // a wrapped line is one line: no newline, no trimming in between
				} else {
					lines.Add(continuesNext ? text : text.TrimEnd());
				}
			}
			return string.Join("\n", lines);
		}

		public string GetText()
		{
			var lines = new List<string>(Rows);
			for (int y = 0; y < Rows; y++)
				lines.Add(GetRowText(y));
			while (lines.Count > 0 && lines[^1].Length == 0)
				lines.RemoveAt(lines.Count - 1);
			return string.Join("\n", lines);
		}
	}
}
