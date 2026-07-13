// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

using HexEditor.Util;
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Workbench;

using UndoAction = HexEditor.Util.UndoAction;

namespace HexEditor
{
	/// <summary>
	/// Hexadecimal editor control (WPF port).
	///
	/// The original WinForms control drew four independent double-buffered GDI+ Panels (header,
	/// side/offsets, hexView, textView) each with their own Graphics object. WPF composites and
	/// double-buffers automatically, so this port instead computes four logical Rects
	/// (<see cref="headerRect"/>/<see cref="sideRect"/>/<see cref="hexRect"/>/<see cref="textRect"/>)
	/// every layout pass and draws all of them from one OnRender/DrawingContext, clipping to each
	/// Rect to reproduce the same visual boundaries. All navigation/selection/undo/hex-input-mode
	/// logic below is a direct, behavior-preserving translation of the original algorithm.
	/// </summary>
	public class Editor : Grid
	{
		// TODO : Make big files compatible (data structures are bad)

		internal enum ViewRegion { Hex, Text }

		/// <summary>
		/// number of the first visible line (first line = 0)
		/// </summary>
		int topline;

		public int TopLine {
			get { return topline; }
			set { topline = value; }
		}

		int charwidth = 3, hexinputmodepos;
		double underscorewidth, underscorewidth3;
		int fontheight;
		bool insertmode = true, hexinputmode, selectionmode, handled, moved;

		public bool Initializing { get; set; }

		Point oldMousePos = new Point(0, 0);

		Rect[] selregion = Array.Empty<Rect>();
		Point[] selpoints = Array.Empty<Point>();
		BufferManager buffer;
		Caret caret;

		public Caret Caret {
			get { return caret; }
		}

		SelectionManager selection;
		UndoManager undoStack;

		ViewRegion activeView = ViewRegion.Hex;

		readonly ScrollBar vScrollBar;

		Rect headerRect, sideRect, hexRect, textRect;

		/// <summary>
		/// Event fired every time something is changed in the editor.
		/// </summary>
		public event EventHandler DocumentChanged;

		protected virtual void OnDocumentChanged(EventArgs e)
		{
			DocumentChanged?.Invoke(this, e);
		}

		/// <summary>
		/// Fired while a file is loading, with a 0-100 percentage. Replaces the WinForms
		/// ToolStripProgressBar reference the original Editor held directly - the container
		/// decides how (or whether) to show progress.
		/// </summary>
		public event Action<int> ProgressChanged;

		internal void ReportProgress(int percentage)
		{
			ProgressChanged?.Invoke(percentage);
		}

		/// <summary>
		/// Creates a new HexEditor Control with basic settings and initialises all components.
		/// </summary>
		public Editor()
		{
			Focusable = true;
			Background = Brushes.White;
			Cursor = System.Windows.Input.Cursors.IBeam;
			ClipToBounds = true;
			FocusVisualStyle = null;

			vScrollBar = new ScrollBar {
				Orientation = Orientation.Vertical,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Stretch,
				Width = 18,
				SmallChange = 1,
				LargeChange = 1,
			};
			vScrollBar.Scroll += VScrollBarScroll;
			Children.Add(vScrollBar);

			buffer = new BufferManager(this);
			selection = new SelectionManager(ref buffer);
			undoStack = new UndoManager();

			underscorewidth = MeasureStringWidth("_", Settings.DataFont);
			underscorewidth3 = underscorewidth * 3;
			fontheight = GetFontHeight(Settings.DataFont);
			headertext = GetHeaderText();

			caret = new Caret(1, fontheight, 0);

			SizeChanged += (s, e) => HexEditSizeChanged();
			GotFocus += (s, e) => InvalidateVisual();
			ContextMenuClosing += (s, e) => InvalidateVisual();

			AdjustScrollBar();
			InvalidateVisual();
		}

		#region Measure functions
		static int GetFontHeight(FontSettings font)
		{
			var ft = new FormattedText("_", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
				font.ToTypeface(), font.Size, Brushes.Black, 1.0);
			return (int)Math.Ceiling(ft.Height) + 1;
		}

		static double MeasureStringWidth(string word, FontSettings font)
		{
			var ft = new FormattedText(word, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
				font.ToTypeface(), font.Size, Brushes.Black, 1.0);
			return ft.WidthIncludingTrailingWhitespace;
		}

		static void DrawText(DrawingContext dc, string text, FontSettings font, Rect rect, Brush foreBrush, Brush backBrush)
		{
			if (backBrush != null)
				dc.DrawRectangle(backBrush, null, rect);
			if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0)
				return;
			var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
				font.ToTypeface(), font.Size, foreBrush, 1.0);
			dc.DrawText(ft, rect.TopLeft);
		}

		readonly struct ClipScope : IDisposable
		{
			readonly DrawingContext dc;
			public ClipScope(DrawingContext dc, Rect rect)
			{
				this.dc = dc;
				dc.PushClip(new RectangleGeometry(rect));
			}
			public void Dispose() => dc.Pop();
		}

		static ClipScope Clip(DrawingContext dc, Rect rect) => new ClipScope(dc, rect);
		#endregion

		/// <summary>
		/// used to store headertext for calculation.
		/// </summary>
		string headertext = String.Empty;

		#region Properties
		ViewMode viewMode = ViewMode.Hexadecimal;
		int bytesPerLine = 16;
		bool fitToWindowWidth;
		string fileName;
		Encoding encoding = Encoding.Default;

		/// <summary>
		/// Represents the current buffer of the editor.
		/// </summary>
		public BufferManager Buffer {
			get { return buffer; }
		}

		/// <summary>
		/// Offers access to the current selection
		/// </summary>
		public SelectionManager Selection {
			get { return selection; }
		}

		/// <summary>
		/// Represents the undo stack of the editor.
		/// </summary>
		public UndoManager UndoStack {
			get { return undoStack; }
		}

		/// <summary>
		/// Returns the name of the currently loaded file.
		/// </summary>
		public string FileName {
			get { return fileName; }
			set { fileName = value; }
		}

		/// <summary>
		/// Property for future use to allow user to select encoding.
		/// </summary>
		public Encoding Encoding {
			get { return encoding; }
			set { encoding = value; }
		}

		/// <summary>
		/// The font used for all data displays in the hex editor.
		/// </summary>
		public FontSettings DataFont {
			get { return Settings.DataFont; }
			set {
				Settings.DataFont = value;
				underscorewidth = MeasureStringWidth("_", value);
				underscorewidth3 = underscorewidth * 3;
				fontheight = GetFontHeight(value);
				InvalidateVisual();
			}
		}

		/// <summary>
		/// The font used for all offset displays in the hex editor.
		/// </summary>
		public FontSettings OffsetFont {
			get { return Settings.OffsetFont; }
			set {
				Settings.OffsetFont = value;
				underscorewidth = MeasureStringWidth("_", value);
				underscorewidth3 = underscorewidth * 3;
				fontheight = GetFontHeight(value);
				InvalidateVisual();
			}
		}

		/// <summary>
		/// The ViewMode used in the hex editor.
		/// </summary>
		public ViewMode ViewMode {
			get { return viewMode; }
			set {
				viewMode = value;
				UpdateViews();
				headertext = GetHeaderText();
				InvalidateVisual();
			}
		}

		/// <summary>
		/// "Auto-fit width" setting.
		/// </summary>
		public bool FitToWindowWidth {
			get { return fitToWindowWidth; }
			set {
				fitToWindowWidth = value;
				if (value) this.BytesPerLine = CalculateMaxBytesPerLine();
			}
		}

		/// <summary>
		/// Gets or sets how many bytes (chars) are displayed per line.
		/// </summary>
		public int BytesPerLine {
			get { return bytesPerLine; }
			set {
				if (value < 1) value = 1;
				if (!Initializing && value > CalculateMaxBytesPerLine()) value = CalculateMaxBytesPerLine();
				bytesPerLine = value;
				UpdateViews();
				headertext = GetHeaderText();
				InvalidateVisual();
			}
		}

		string GetHeaderText()
		{
			StringBuilder text = new StringBuilder();
			for (int i = 0; i < this.BytesPerLine; i++) {
				switch (this.ViewMode) {
					case ViewMode.Decimal:
						text.Append(' ', 3 - GetLength(i));
						text.Append(i.ToString());
						break;
					case ViewMode.Hexadecimal:
						text.Append(' ', 3 - string.Format("{0:X}", i).Length);
						text.AppendFormat("{0:X}", i);
						break;
					case ViewMode.Octal:
						int tmp = i;
						string num = "";
						if (tmp == 0) num = "0";
						while (tmp != 0) {
							num = (tmp % 8).ToString() + num;
							tmp = (int)(tmp / 8);
						}
						text.Append(' ', 3 - num.Length);
						text.Append(num);
						break;
				}
			}
			return text.ToString();
		}
		#endregion

		#region Layout
		void HexEditSizeChanged()
		{
			if (this.FitToWindowWidth) this.BytesPerLine = CalculateMaxBytesPerLine();
			InvalidateVisual();
		}

		void UpdateViews()
		{
			double width = Math.Max(0, ActualWidth - vScrollBar.Width);
			double height = Math.Max(0, ActualHeight);

			int sidetext = this.GetMaxLines() * this.BytesPerLine;
			double textwidth = MeasureStringWidth(new string('_', this.BytesPerLine + 1), Settings.DataFont);
			double hexwidth = underscorewidth3 * this.BytesPerLine;

			double headerTop = 0;
			double viewTop = fontheight - 1;

			string st;
			switch (this.ViewMode) {
				case ViewMode.Hexadecimal:
					st = sidetext.ToString().Length < 8 ? "  Offset" : "  " + string.Format("{0:X}", sidetext);
					break;
				case ViewMode.Octal:
					if (sidetext.ToString().Length < 8) {
						st = "  Offset";
					} else {
						int tmp = sidetext;
						string s = "";
						while (tmp != 0) {
							s = (tmp % 8).ToString() + s;
							tmp = (int)(tmp / 8);
						}
						st = "  " + s;
					}
					break;
				case ViewMode.Decimal:
				default:
					st = sidetext.ToString().Length < 8 ? "  Offset" : "  " + sidetext.ToString();
					break;
			}

			double sideWidth = MeasureStringWidth(st, Settings.OffsetFont);
			double sideLeft = 0;
			double hexLeft = sideWidth + 10;

			double hexW, textW, textLeft;
			if ((textwidth + hexwidth + 25) > width - sideWidth) {
				hexW = width - sideWidth - textwidth - 30;
				textW = textwidth;
				textLeft = width - textwidth - 16;
			} else {
				hexW = hexwidth;
				textW = textwidth;
				textLeft = hexwidth + hexLeft + 20;
			}

			double viewHeight = Math.Max(0, height - fontheight - 18);

			sideRect = new Rect(sideLeft, 0, Math.Max(0, sideWidth), height);
			headerRect = new Rect(hexLeft, headerTop, Math.Max(0, hexW + 10), Math.Max(0, fontheight));
			hexRect = new Rect(hexLeft, viewTop, Math.Max(0, hexW), viewHeight);
			textRect = new Rect(textLeft, viewTop, Math.Max(0, textW), viewHeight);

			SetCaretPosition(caret.Offset);
			AdjustScrollBar();
		}

		/// <summary>
		/// Calculates the max possible bytes per line.
		/// </summary>
		internal int CalculateMaxBytesPerLine()
		{
			double width = ActualWidth - vScrollBar.Width - sideRect.Width - 90;
			double textwidth = 0, hexwidth = 0;
			int count = 0;
			while ((textwidth + hexwidth) < width) {
				count++;
				textwidth = underscorewidth * count;
				hexwidth = underscorewidth3 * count;
			}
			if (count < 1) count = 1;
			return count;
		}
		#endregion

		#region MouseActions/Focus/ScrollBar
		void AdjustScrollBar()
		{
			int linecount = this.GetMaxLines();
			if (linecount > GetMaxVisibleLines()) {
				vScrollBar.IsEnabled = true;
				vScrollBar.Maximum = linecount - 1;
				vScrollBar.Minimum = 0;
			} else {
				vScrollBar.Value = 0;
				vScrollBar.IsEnabled = false;
			}
		}

		void SyncTopLineFromScrollBar()
		{
			UpdateViews();
			this.topline = (int)vScrollBar.Value;
			SetCaretPosition(caret.Offset);
			InvalidateVisual();
		}

		void VScrollBarScroll(object sender, ScrollEventArgs e)
		{
			SyncTopLineFromScrollBar();
		}

		protected override void OnMouseWheel(MouseWheelEventArgs e)
		{
			base.OnMouseWheel(e);
			if (!vScrollBar.IsEnabled) return;

			int delta = -(e.Delta / 3 / 10);
			double newValue = vScrollBar.Value + delta;
			if (newValue > vScrollBar.Maximum) newValue = vScrollBar.Maximum;
			if (newValue < vScrollBar.Minimum) newValue = vScrollBar.Minimum;
			vScrollBar.Value = newValue;
			SyncTopLineFromScrollBar();
			e.Handled = true;
		}

		protected override void OnMouseDown(MouseButtonEventArgs e)
		{
			base.OnMouseDown(e);
			Point pos = e.GetPosition(this);
			Focus();

			if (hexRect.Contains(pos)) {
				activeView = ViewRegion.Hex;
				charwidth = 3;
				caret.Width = insertmode ? 1 : (int)(underscorewidth * 2);
				if (e.ChangedButton != MouseButton.Right) {
					selectionmode = true;
					selection.Start = GetOffsetForPosition(ToLocal(pos, hexRect), 3);
					selection.End = selection.Start;
					selection.HasSomethingSelected = false;
				}
			} else if (textRect.Contains(pos)) {
				hexinputmode = false;
				activeView = ViewRegion.Text;
				charwidth = 1;
				caret.Width = insertmode ? 1 : (int)underscorewidth;
				if (e.ChangedButton != MouseButton.Right) {
					selectionmode = true;
					selection.Start = GetOffsetForPosition(ToLocal(pos, textRect), 1);
					selection.End = selection.Start;
					selection.HasSomethingSelected = false;
				}
			}

			CaptureMouse();
			InvalidateVisual();
		}

		protected override void OnMouseMove(MouseEventArgs e)
		{
			base.OnMouseMove(e);
			Point pos = e.GetPosition(this);

			if (e.LeftButton == MouseButtonState.Pressed && selectionmode && pos != oldMousePos) {
				int cw = activeView == ViewRegion.Hex ? 3 : 1;
				Rect region = activeView == ViewRegion.Hex ? hexRect : textRect;
				selection.End = GetOffsetForPosition(ToLocal(pos, region), cw);
				selection.HasSomethingSelected = true;
				moved = true;
				caret.Offset = selection.End;
				SetCaretPosition(caret.Offset);
				InvalidateVisual();
			}

			oldMousePos = pos;
		}

		protected override void OnMouseUp(MouseButtonEventArgs e)
		{
			base.OnMouseUp(e);
			ReleaseMouseCapture();
			if (e.ChangedButton == MouseButton.Right) return;

			Point pos = e.GetPosition(this);
			int cw = activeView == ViewRegion.Hex ? 3 : 1;
			Rect region = activeView == ViewRegion.Hex ? hexRect : textRect;

			if (selectionmode) {
				selection.HasSomethingSelected = true;
				if ((selection.End == selection.Start) || (selection.Start == 0 && selection.End == 0)) {
					selection.HasSomethingSelected = false;
					selectionmode = false;
				}
			} else {
				if (!moved) {
					selection.HasSomethingSelected = false;
					selection.Start = 0;
					selection.End = 0;
				}
				moved = false;
			}

			caret.Offset = GetOffsetForPosition(ToLocal(pos, region), cw);
			SetCaretPosition(caret.Offset);
			selectionmode = false;

			InvalidateVisual();
		}

		static Point ToLocal(Point p, Rect region) => new Point(p.X - region.X, p.Y - region.Y);
		#endregion

		#region Painters
		protected override void OnRender(DrawingContext dc)
		{
			base.OnRender(dc);

			UpdateViews();
			CalculateSelectionRegions();

			dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, ActualWidth, ActualHeight));

			PaintHeader(dc);
			PaintOffsetNumbers(dc);
			PaintHex(dc);
			PaintText(dc);
			PaintPointer(dc);
			PaintSelection(dc, true);
			caret.Draw(dc);
		}

		void PaintHeader(DrawingContext dc)
		{
			using (Clip(dc, headerRect)) {
				dc.DrawRectangle(Brushes.White, null, headerRect);
				var rect = new Rect(headerRect.X + 1, headerRect.Y + 1, headerRect.Width + 5, fontheight);
				DrawText(dc, headertext, Settings.OffsetFont, rect, new SolidColorBrush(Settings.OffsetForeColor), null);
			}
		}

		void PaintOffsetNumbers(DrawingContext dc)
		{
			int top = (int)vScrollBar.Value;
			using (Clip(dc, sideRect)) {
				dc.DrawRectangle(Brushes.White, null, sideRect);

				int count = top + this.GetMaxVisibleLines();
				StringBuilder builder = new StringBuilder(StringParser.Parse("${res:AddIns.HexEditor.Display.Elements.Offset}\n"));
				if (count == 0) builder.Append("0\n");

				for (int i = top; i < count; i++) {
					if ((i * this.BytesPerLine) <= this.buffer.BufferSize) {
						switch (this.ViewMode) {
							case ViewMode.Decimal:
								builder.AppendLine((i * this.BytesPerLine).ToString());
								break;
							case ViewMode.Hexadecimal:
								builder.AppendFormat("{0:X}", i * this.BytesPerLine);
								builder.AppendLine();
								break;
							case ViewMode.Octal:
								int tmp = i * this.BytesPerLine;
								if (tmp == 0) {
									builder.AppendLine("0");
								} else {
									StringBuilder num = new StringBuilder();
									while (tmp != 0) {
										num.Insert(0, (tmp % 8).ToString());
										tmp = (int)(tmp / 8);
									}
									builder.AppendLine(num.ToString());
								}
								break;
						}
					}
				}

				if (sideRect.Width > 0 && sideRect.Height > 0) {
					var ft = new FormattedText(builder.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
						Settings.OffsetFont.ToTypeface(), Settings.OffsetFont.Size, new SolidColorBrush(Settings.OffsetForeColor), 1.0);
					ft.TextAlignment = TextAlignment.Right;
					ft.MaxTextWidth = Math.Max(1, sideRect.Width);
					dc.DrawText(ft, sideRect.TopLeft);
				}
			}
		}

		void PaintHex(DrawingContext dc)
		{
			int top = (int)vScrollBar.Value;
			using (Clip(dc, hexRect)) {
				dc.DrawRectangle(Brushes.White, null, hexRect);
				StringBuilder builder = new StringBuilder();
				int offset = GetOffsetForLine(top);
				for (int i = 0; i < GetMaxVisibleLines(); i++) {
					builder.AppendLine(GetHex(buffer.GetBytes(offset, this.BytesPerLine)));
					offset = GetOffsetForLine(top + i + 1);
				}
				DrawText(dc, builder.ToString(), Settings.DataFont, hexRect, new SolidColorBrush(Settings.DataForeColor), null);
			}
		}

		void PaintText(DrawingContext dc)
		{
			int top = (int)vScrollBar.Value;
			using (Clip(dc, textRect)) {
				dc.DrawRectangle(Brushes.White, null, textRect);
				int offset = GetOffsetForLine(top);
				StringBuilder builder = new StringBuilder();
				for (int i = 0; i < GetMaxVisibleLines(); i++) {
					builder.AppendLine(GetText(buffer.GetBytes(offset, this.BytesPerLine)));
					offset = GetOffsetForLine(top + i + 1);
				}
				DrawText(dc, builder.ToString(), Settings.DataFont, textRect, new SolidColorBrush(Settings.DataForeColor), null);
			}
		}

		/// <summary>
		/// Draws a pointer to show the cursor in the opposite view panel.
		/// </summary>
		void PaintPointer(DrawingContext dc)
		{
			if (selection.HasSomethingSelected) return;

			var pen = new Pen(Brushes.Black, 1) { DashStyle = DashStyles.Dot };
			if (activeView == ViewRegion.Hex) {
				Point pos = GetPositionForOffset(caret.Offset, 1);
				if (hexinputmode) pos = GetPositionForOffset(caret.Offset - 1, 1);
				var size = new Size(underscorewidth, fontheight);
				var rect = new Rect(new Point(textRect.X + pos.X, textRect.Y + pos.Y), size);
				using (Clip(dc, textRect)) dc.DrawRectangle(null, pen, rect);
			} else {
				Point pos = GetPositionForOffset(caret.Offset, 3);
				pos.Y += 1;
				var size = new Size(underscorewidth * 2, fontheight);
				var rect = new Rect(new Point(hexRect.X + pos.X, hexRect.Y + pos.Y), size);
				using (Clip(dc, hexRect)) dc.DrawRectangle(null, pen, rect);
			}
		}

		/// <summary>
		/// Recalculates the current selection regions for drawing. Both the highlight rects
		/// (drawn in the active view, using its own charwidth/step) and the outline marker points
		/// (drawn in the OTHER view, using ITS charwidth/step) are computed here - the original
		/// GDI+ implementation drew the outline onto the opposite panel's Graphics deliberately,
		/// so this preserves that exact pairing.
		/// </summary>
		void CalculateSelectionRegions()
		{
			var regions = new List<Rect>();
			var points = new List<Point>();

			int lines = Math.Abs(GetLineForOffset(selection.End) - GetLineForOffset(selection.Start));
			int start, end;

			if (selection.End > selection.Start) {
				start = selection.Start;
				end = selection.End;
			} else {
				start = selection.End;
				end = selection.Start;
			}

			int start_dummy = start;

			if (start < GetOffsetForLine(topline)) {
				start = GetOffsetForLine(topline) - 2;
				start_dummy = GetOffsetForLine(topline - 2);
			}

			if (end > GetOffsetForLine(topline + GetMaxVisibleLines())) end = GetOffsetForLine(topline + GetMaxVisibleLines() + 1);

			int tmp_start = start;
			if (((selection.End % bytesPerLine) == 0) && (selection.End < selection.Start))
				tmp_start++;

			bool isHexActive = activeView == ViewRegion.Hex;

			int regionCharWidth = isHexActive ? 3 : 1;
			double regionStep = isHexActive ? underscorewidth3 : underscorewidth;
			Point regionOrigin = isHexActive ? hexRect.TopLeft : textRect.TopLeft;

			Point RegionPos(int offset)
			{
				var p = GetPositionForOffset(offset, regionCharWidth);
				return new Point(p.X + regionOrigin.X, p.Y + regionOrigin.Y);
			}

			if (GetLineForOffset(end) == GetLineForOffset(tmp_start)) {
				Point pt = RegionPos(start);
				regions.Add(new Rect(new Point(pt.X - 4, pt.Y), new Size(Math.Max(0, (end - start) * regionStep + 2), fontheight)));
			} else {
				Point pt = RegionPos(start);
				regions.Add(new Rect(new Point(pt.X - 4, pt.Y), new Size(Math.Max(0, (this.BytesPerLine - (start - this.BytesPerLine * GetLineForOffset(start))) * regionStep + 2), fontheight)));

				Point pt2 = RegionPos((1 + GetLineForOffset(start)) * this.BytesPerLine);
				regions.Add(new Rect(new Point(pt2.X - 4, pt2.Y), new Size(Math.Max(0, this.BytesPerLine * regionStep + 2), Math.Max(0, fontheight * (lines - 1) - lines + 1))));

				Point pt3 = RegionPos(GetLineForOffset(end) * this.BytesPerLine);
				regions.Add(new Rect(new Point(pt3.X - 4, pt3.Y), new Size(Math.Max(0, (end - GetLineForOffset(end) * this.BytesPerLine) * regionStep + 2), fontheight)));
			}

			selregion = regions.ToArray();

			start = start_dummy;

			int outlineCharWidth = isHexActive ? 1 : 3;
			double outlineStep = isHexActive ? underscorewidth : underscorewidth3;
			Point outlineOrigin = isHexActive ? textRect.TopLeft : hexRect.TopLeft;

			Point OutlinePos(int offset)
			{
				var p = GetPositionForOffset(offset, outlineCharWidth);
				return new Point(p.X + outlineOrigin.X, p.Y + outlineOrigin.Y);
			}

			if (GetLineForOffset(end) == GetLineForOffset(tmp_start)) {
				Point pt = OutlinePos(start);
				points.Add(new Point(pt.X - 1, pt.Y));
				points.Add(new Point(pt.X - 1, pt.Y + fontheight));
				points.Add(new Point(pt.X - 1 + (end - start + 1) * outlineStep - 8, pt.Y + fontheight));
				points.Add(new Point(pt.X - 1 + (end - start + 1) * outlineStep - 8, pt.Y));
			} else {
				Point pt = OutlinePos(start);
				pt = new Point(pt.X - 1, pt.Y);
				points.Add(pt);
				pt = new Point(pt.X, pt.Y + fontheight - 1);
				points.Add(pt);

				pt = OutlinePos(GetOffsetForLine(GetLineForOffset(start) + 1));
				pt = new Point(pt.X - 1, pt.Y);
				points.Add(pt);

				pt = OutlinePos(GetOffsetForLine(GetLineForOffset(end)));
				pt = (end % bytesPerLine) != 0 ? new Point(pt.X - 1, pt.Y + fontheight) : new Point(pt.X - 1, pt.Y + fontheight - 1);
				points.Add(pt);

				if ((end % bytesPerLine) != 0) {
					pt = OutlinePos(end);
					points.Add(new Point(pt.X, pt.Y + fontheight));
					pt = OutlinePos(end);
					points.Add(new Point(pt.X, pt.Y));
				}

				pt = OutlinePos(end + (bytesPerLine - (end % bytesPerLine)) - 1);
				points.Add(new Point(pt.X + outlineStep, pt.Y));

				pt = OutlinePos(end + (bytesPerLine - (end % bytesPerLine)) - 1);
				points.Add(new Point(pt.X + outlineStep, OutlinePos(start).Y));
			}

			selpoints = points.ToArray();
		}

		/// <summary>
		/// Draws the current selection highlight and its outline marker.
		/// </summary>
		void PaintSelection(DrawingContext dc, bool paintMarker)
		{
			if (!selection.HasSomethingSelected) return;

			int start, end;
			if (selection.End > selection.Start) {
				start = selection.Start;
				end = selection.End;
			} else {
				start = selection.End;
				end = selection.Start;
			}

			if (start > GetOffsetForLine(topline + GetMaxVisibleLines())) return;

			if (start < GetOffsetForLine(topline)) start = GetOffsetForLine(topline) - 2;
			if (end > GetOffsetForLine(topline + GetMaxVisibleLines())) end = GetOffsetForLine(topline + GetMaxVisibleLines() + 1);

			bool isHexActive = activeView == ViewRegion.Hex;
			Brush highlightBrush = SystemColors.HighlightBrush;
			Brush dataBrush = Brushes.White;

			var builder = new StringBuilder();
			for (int i = GetLineForOffset(start) + 1; i < GetLineForOffset(end); i++)
				builder.AppendLine(isHexActive ? GetLineHex(i) : GetLineText(i));

			string firstLine = isHexActive ? GetHex(buffer.GetBytes(start, this.BytesPerLine)) : GetText(buffer.GetBytes(start, this.BytesPerLine));
			string lastLine = isHexActive ? GetLineHex(GetLineForOffset(end)) : GetLineText(GetLineForOffset(end));

			if (selregion.Length == 3) {
				DrawText(dc, firstLine, Settings.DataFont, selregion[0], dataBrush, highlightBrush);
				DrawText(dc, builder.ToString(), Settings.DataFont, selregion[1], dataBrush, highlightBrush);
				DrawText(dc, lastLine, Settings.DataFont, selregion[2], dataBrush, highlightBrush);
			} else if (selregion.Length == 2) {
				DrawText(dc, firstLine, Settings.DataFont, selregion[0], dataBrush, highlightBrush);
				DrawText(dc, lastLine, Settings.DataFont, selregion[1], dataBrush, highlightBrush);
			} else if (selregion.Length == 1) {
				DrawText(dc, firstLine, Settings.DataFont, selregion[0], dataBrush, highlightBrush);
			}

			if (!paintMarker || selpoints.Length == 0) return;

			var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
			using (var ctx = geometry.Open()) {
				bool sameOrAdjacentFull = GetLineForOffset(start) == GetLineForOffset(end)
					|| ((start % bytesPerLine) == 0 && GetLineForOffset(start) + 1 == GetLineForOffset(end));

				if (sameOrAdjacentFull) {
					if (selpoints.Length == 8) {
						ctx.BeginFigure(selpoints[0], false, false);
						ctx.LineTo(selpoints[1], true, false);
						ctx.BeginFigure(selpoints[3], false, false);
						ctx.LineTo(selpoints[4], true, false);
						ctx.LineTo(selpoints[5], true, false);
						ctx.LineTo(selpoints[0], true, false);
					} else {
						ctx.BeginFigure(selpoints[0], false, true);
						for (int i = 1; i < selpoints.Length; i++) ctx.LineTo(selpoints[i], true, false);
					}
				} else if ((GetLineForOffset(start) == GetLineForOffset(end) - 1) && (start % bytesPerLine >= end % bytesPerLine)) {
					if (selpoints.Length < 8) {
						ctx.BeginFigure(selpoints[0], false, true);
						for (int i = 1; i < selpoints.Length; i++) ctx.LineTo(selpoints[i], true, false);
					} else {
						ctx.BeginFigure(selpoints[0], false, false);
						ctx.LineTo(selpoints[1], true, false);
						ctx.LineTo(selpoints[6], true, false);
						ctx.LineTo(selpoints[7], true, true);
						ctx.BeginFigure(selpoints[2], false, false);
						ctx.LineTo(selpoints[3], true, false);
						ctx.LineTo(selpoints[4], true, false);
						ctx.LineTo(selpoints[5], true, true);
					}
				} else {
					ctx.BeginFigure(selpoints[0], false, true);
					for (int i = 1; i < selpoints.Length; i++) ctx.LineTo(selpoints[i], true, false);
				}
			}

			dc.DrawGeometry(null, new Pen(Brushes.Black, 1), geometry);
		}
		#endregion

		#region Undo/Redo
		public void Redo()
		{
			OnDocumentChanged(EventArgs.Empty);

			UndoStep step = undoStack.Redo(ref buffer);
			hexinputmode = false;
			hexinputmodepos = 0;
			selection.Clear();
			if (step != null) SetCaretPosition(step.Start);
			InvalidateVisual();
		}

		public void Undo()
		{
			OnDocumentChanged(EventArgs.Empty);

			UndoStep step = undoStack.Undo(ref buffer);
			hexinputmode = false;
			hexinputmodepos = 0;
			selection.Clear();
			if (step != null) {
				int offset = step.Start;
				if (offset > buffer.BufferSize) offset = buffer.BufferSize;
				SetCaretPosition(offset);
			}
			InvalidateVisual();
		}

		public bool CanUndo {
			get { return undoStack.CanUndo; }
		}

		public bool CanRedo {
			get { return undoStack.CanRedo; }
		}
		#endregion

		#region Selection
		public void SetSelection(int start, int end)
		{
			if (start > buffer.BufferSize) start = buffer.BufferSize;
			if (start < 0) start = 0;
			selection.Start = start;
			if (end > buffer.BufferSize) end = buffer.BufferSize;
			selection.End = end;
			selection.HasSomethingSelected = true;
			hexinputmode = false;
			hexinputmodepos = 0;

			CalculateSelectionRegions();

			InvalidateVisual();
		}

		public bool HasSomethingSelected {
			get { return selection.HasSomethingSelected; }
		}

		public void SelectAll()
		{
			SetSelection(0, this.buffer.BufferSize);
		}
		#endregion

		#region Clipboard Actions
		public string Copy()
		{
			string text = selection.SelectionText;
			if (text.Contains("\0"))
				text = text.Replace("\0", "");
			return text;
		}

		public string CopyAsHexString()
		{
			return GetHex(selection.GetSelectionBytes());
		}

		public string CopyAsBinary()
		{
			return Copy();
		}

		public void Paste(string text)
		{
			if (caret.Offset > buffer.BufferSize) caret.Offset = buffer.BufferSize;
			if (selection.HasSomethingSelected) {
				byte[] old = selection.GetSelectionBytes();
				int start = selection.Start;
				if (selection.Start > selection.End) start = selection.End;

				buffer.RemoveBytes(start, Math.Abs(selection.End - selection.Start));
				buffer.SetBytes(start, this.Encoding.GetBytes(text.ToCharArray()), false);
				undoStack.AddOverwriteStep(start, this.Encoding.GetBytes(text.ToCharArray()), old);

				caret.Offset = start + text.Length;
				selection.Clear();
			} else {
				buffer.SetBytes(caret.Offset, this.Encoding.GetBytes(text.ToCharArray()), false);
				undoStack.AddRemoveStep(caret.Offset, this.Encoding.GetBytes(text.ToCharArray()));
				caret.Offset += text.Length;
			}
			if (GetLineForOffset(caret.Offset) < this.topline) this.topline = GetLineForOffset(caret.Offset);
			if (GetLineForOffset(caret.Offset) > this.topline + this.GetMaxVisibleLines() - 2) this.topline = GetLineForOffset(caret.Offset) - this.GetMaxVisibleLines() + 2;
			if (this.topline < 0) this.topline = 0;
			if (this.topline > vScrollBar.Maximum) {
				AdjustScrollBar();
				if (this.topline > vScrollBar.Maximum) this.topline = (int)vScrollBar.Maximum;
			}
			vScrollBar.Value = this.topline;
			InvalidateVisual();

			OnDocumentChanged(EventArgs.Empty);
		}

		public void Delete()
		{
			if (hexinputmode) return;
			if (selection.HasSomethingSelected) {
				byte[] old = selection.GetSelectionBytes();
				buffer.RemoveBytes(selection.Start, Math.Abs(selection.End - selection.Start));
				caret.Offset = selection.Start;

				undoStack.AddInsertStep(selection.Start, old);

				selection.Clear();
			}
			InvalidateVisual();

			OnDocumentChanged(EventArgs.Empty);
		}
		#endregion

		#region TextProcessing
		string GetText(byte[] bytes)
		{
			for (int i = 0; i < bytes.Length; i++) {
				if (bytes[i] < 32 || (bytes[i] >= 0x80 && bytes[i] < 0xA0)) bytes[i] = 46;
			}
			string text = this.Encoding.GetString(bytes);
			return text.Replace("&", "&&");
		}

		string GetLineText(int line)
		{
			return GetText(buffer.GetBytes(GetOffsetForLine(line), this.BytesPerLine));
		}

		string GetLineHex(int line)
		{
			return GetHex(buffer.GetBytes(GetOffsetForLine(line), this.BytesPerLine));
		}

		static string GetHex(byte[] bytes)
		{
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < bytes.Length; i++)
				builder.Append(string.Format("{0:X2} ", bytes[i]));
			return builder.ToString();
		}
		#endregion

		#region Keyboard input
		void SetCaretPosition(int offset)
		{
			Point local = GetPositionForOffset(offset, charwidth);
			Point origin = activeView == ViewRegion.Hex ? hexRect.TopLeft : textRect.TopLeft;
			caret.SetToPosition(new Point(local.X + origin.X, local.Y + origin.Y));
		}

		static bool TryGetHexChar(Key key, out char ch)
		{
			if (key >= Key.D0 && key <= Key.D9) { ch = (char)('0' + (key - Key.D0)); return true; }
			if (key >= Key.NumPad0 && key <= Key.NumPad9) { ch = (char)('0' + (key - Key.NumPad0)); return true; }
			if (key >= Key.A && key <= Key.F) { ch = (char)('A' + (key - Key.A)); return true; }
			ch = '\0';
			return false;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);

			bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
			bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

			int start = selection.Start;
			int end = selection.End;
			if (selection.Start > selection.End) {
				start = selection.End;
				end = selection.Start;
			}

			if (activeView != ViewRegion.Hex) {
				hexinputmode = false;
				hexinputmodepos = 0;
			}

			if (ctrl) {
				switch (e.Key) {
					case Key.Up:
						if (this.topline > 0) this.topline--;
						break;
					case Key.Down:
						if (this.topline < this.GetMaxLines()) this.topline++;
						break;
				}
				vScrollBar.Value = this.topline;
				handled = true;
			}

			switch (e.Key) {
				case Key.Up:
				case Key.Down:
				case Key.Left:
				case Key.Right:
					if (!ctrl) {
						int oldoffset = caret.Offset;
						MoveCaret(e.Key, ctrl);
						if (GetLineForOffset(caret.Offset) < this.topline) this.topline = GetLineForOffset(caret.Offset);
						if (GetLineForOffset(caret.Offset) > this.topline + this.GetMaxVisibleLines() - 2) this.topline = GetLineForOffset(caret.Offset) - this.GetMaxVisibleLines() + 2;
						vScrollBar.Value = this.topline;

						if (shift) {
							if (selection.HasSomethingSelected) {
								this.SetSelection(selection.Start, caret.Offset);
							} else {
								this.SetSelection(oldoffset, caret.Offset);
							}
						} else {
							this.selection.Clear();
						}
						handled = true;
					}
					break;
				case Key.Insert:
					insertmode = !insertmode;
					caret.Width = insertmode ? 1 : (activeView == ViewRegion.Hex ? (int)(underscorewidth * 2) : (int)underscorewidth);
					SetCaretPosition(caret.Offset);
					handled = true;
					break;
				case Key.Back:
					handled = true;
					if (hexinputmode) { e.Handled = true; return; }
					if (selection.HasSomethingSelected) {
						byte[] bytes = selection.GetSelectionBytes();
						buffer.RemoveBytes(start, Math.Abs(end - start));
						caret.Offset = start;
						undoStack.AddInsertStep(start, bytes);
						selection.Clear();
					} else {
						byte b = buffer.GetByte(caret.Offset - 1);
						if (buffer.RemoveByte(caret.Offset - 1)) {
							if (caret.Offset > -1) caret.Offset--;
							if (GetLineForOffset(caret.Offset) < this.topline) this.topline = GetLineForOffset(caret.Offset);
							if (GetLineForOffset(caret.Offset) > this.topline + this.GetMaxVisibleLines() - 2) this.topline = GetLineForOffset(caret.Offset) - this.GetMaxVisibleLines() + 2;
							undoStack.AddInsertStep(caret.Offset, new byte[] { b });
						}
					}
					OnDocumentChanged(EventArgs.Empty);
					break;
				case Key.Delete:
					handled = true;
					if (hexinputmode) { e.Handled = true; return; }
					if (selection.HasSomethingSelected) {
						byte[] old = selection.GetSelectionBytes();
						buffer.RemoveBytes(start, Math.Abs(selection.End - selection.Start));
						caret.Offset = selection.Start;
						undoStack.AddInsertStep(selection.Start, old);
						selection.Clear();
					} else {
						byte b = buffer.GetByte(caret.Offset);
						buffer.RemoveByte(caret.Offset);
						undoStack.AddInsertStep(caret.Offset, new byte[] { b });
						if (GetLineForOffset(caret.Offset) < this.topline) this.topline = GetLineForOffset(caret.Offset);
						if (GetLineForOffset(caret.Offset) > this.topline + this.GetMaxVisibleLines() - 2) this.topline = GetLineForOffset(caret.Offset) - this.GetMaxVisibleLines() + 2;
					}
					OnDocumentChanged(EventArgs.Empty);
					break;
				case Key.CapsLock:
				case Key.LeftShift:
				case Key.RightShift:
				case Key.LeftCtrl:
				case Key.RightCtrl:
					break;
				case Key.Tab:
					if (activeView == ViewRegion.Hex) {
						activeView = ViewRegion.Text;
						charwidth = 1;
					} else {
						activeView = ViewRegion.Hex;
						charwidth = 3;
					}
					handled = true;
					e.Handled = true;
					break;
				default:
					if (ctrl) {
						handled = true;
						switch (e.Key) {
							case Key.A:
								this.SetSelection(0, buffer.BufferSize);
								break;
							case Key.C:
								ClipboardManager.Copy(this.Copy());
								break;
							case Key.V:
								if (ClipboardManager.ContainsText) {
									this.Paste(ClipboardManager.Paste());
									if (GetLineForOffset(caret.Offset) < this.topline) this.topline = GetLineForOffset(caret.Offset);
									if (GetLineForOffset(caret.Offset) > this.topline + this.GetMaxVisibleLines() - 2) this.topline = GetLineForOffset(caret.Offset) - this.GetMaxVisibleLines() + 2;
									if (this.topline < 0) this.topline = 0;
									if (this.topline > vScrollBar.Maximum) {
										AdjustScrollBar();
										if (this.topline > vScrollBar.Maximum) this.topline = (int)vScrollBar.Maximum;
									}
									vScrollBar.Value = this.topline;
								}
								break;
							case Key.X:
								if (selection.HasSomethingSelected) {
									ClipboardManager.Copy(this.Copy());
									this.Delete();
								}
								break;
							case Key.Y:
								Redo();
								break;
							case Key.Z:
								Undo();
								break;
						}
						break;
					}
					if (activeView == ViewRegion.Hex && TryGetHexChar(e.Key, out char hexChar)) {
						ProcessHexInput(hexChar);
						handled = true;
						e.Handled = true;
						return;
					}
					break;
			}

			if (handled) {
				InvalidateVisual();
				SetCaretPosition(caret.Offset);
				e.Handled = true;
			}
		}

		protected override void OnTextInput(TextCompositionEventArgs e)
		{
			base.OnTextInput(e);

			if (handled) {
				handled = false;
				e.Handled = true;
				return;
			}

			foreach (char ch in e.Text) {
				byte[] old = buffer.GetBytes(caret.Offset, 1);
				try {
					if (selection.HasSomethingSelected) {
						Delete();
						buffer.SetByte(caret.Offset, (byte)ch, !insertmode);
					} else {
						buffer.SetByte(caret.Offset, (byte)ch, !insertmode);
					}
				} catch (ArgumentOutOfRangeException) { }
				caret.Offset++;
				if (GetLineForOffset(caret.Offset) < this.topline) this.topline = GetLineForOffset(caret.Offset);
				if (GetLineForOffset(caret.Offset) > this.topline + this.GetMaxVisibleLines() - 2) this.topline = GetLineForOffset(caret.Offset) - this.GetMaxVisibleLines() + 2;
				vScrollBar.Value = this.topline;

				if (insertmode)
					undoStack.AddRemoveStep(caret.Offset - 1, new byte[] { (byte)ch });
				else
					undoStack.AddOverwriteStep(caret.Offset - 1, new byte[] { (byte)ch }, old);

				OnDocumentChanged(EventArgs.Empty);
			}

			SetCaretPosition(caret.Offset);
			InvalidateVisual();
			e.Handled = true;
		}

		void MoveCaret(Key key, bool ctrl)
		{
			if (!ctrl) {
				hexinputmode = false;
				hexinputmodepos = 0;
			}
			switch (key) {
				case Key.Up:
					if (caret.Offset >= this.BytesPerLine) {
						caret.Offset -= this.BytesPerLine;
						SetCaretPosition(caret.Offset);
					}
					break;
				case Key.Down:
					if (caret.Offset <= this.Buffer.BufferSize - this.BytesPerLine) {
						caret.Offset += this.BytesPerLine;
					} else {
						caret.Offset = this.Buffer.BufferSize;
					}
					SetCaretPosition(caret.Offset);
					break;
				case Key.Left:
					if (caret.Offset >= 1) {
						if (activeView == ViewRegion.Hex) {
							if (ctrl) {
								hexinputmode = false;
								if (hexinputmodepos == 0) {
									caret.Offset--;
									hexinputmodepos = 1;
									hexinputmode = true;
								} else {
									hexinputmodepos--;
								}
							} else {
								caret.Offset--;
							}
						} else {
							caret.Offset--;
						}
						SetCaretPosition(caret.Offset);
					}
					break;
				case Key.Right:
					if (caret.Offset <= this.Buffer.BufferSize - 1) {
						if (activeView == ViewRegion.Hex) {
							if (ctrl) {
								hexinputmode = true;
								if (hexinputmodepos == 1) {
									caret.Offset++;
									hexinputmodepos = 0;
									hexinputmode = false;
								} else {
									hexinputmodepos++;
								}
							} else {
								caret.Offset++;
							}
						} else {
							caret.Offset++;
						}
						SetCaretPosition(caret.Offset);
					}
					break;
			}
		}

		/// <summary>
		/// Processes 0-9/A-F input and handles the special hex-inputmode (entering a byte as two
		/// nibbles). <paramref name="inputChar"/> is already validated by <see cref="TryGetHexChar"/>.
		/// </summary>
		void ProcessHexInput(char inputChar)
		{
			int start = selection.Start;
			int end = selection.End;
			if (selection.Start > selection.End) {
				start = selection.End;
				end = selection.Start;
			}

			hexinputmode = true;
			if (insertmode) {
				byte[] old;
				if (selection.HasSomethingSelected) {
					old = selection.GetSelectionBytes();
					buffer.RemoveBytes(start, Math.Abs(end - start));
				} else {
					old = null;
				}

				string @in;
				if (hexinputmodepos == 1) {
					@in = string.Format("{0:X}", buffer.GetByte(caret.Offset));
					if (@in.Length == 1) @in = "0" + @in;

					undoStack.AddOverwriteStep(caret.Offset, new byte[] { (byte)(Convert.ToInt32(@in.Remove(1) + inputChar, 16)) }, buffer.GetBytes(caret.Offset, 1));

					@in = @in.Remove(1) + inputChar;
					hexinputmodepos = 0;
					hexinputmode = false;

					buffer.SetByte(caret.Offset, (byte)(Convert.ToInt32(@in, 16)), true);
					caret.Offset++;

					SetCaretPosition(caret.Offset);
				} else if (hexinputmodepos == 0) {
					UndoAction action;
					if (selection.HasSomethingSelected) {
						action = UndoAction.Overwrite;
						caret.Offset = start;
						selection.Clear();
					} else {
						action = UndoAction.Remove;
					}
					@in = inputChar + "0";
					if (caret.Offset > buffer.BufferSize) caret.Offset = buffer.BufferSize;
					buffer.SetByte(caret.Offset, (byte)(Convert.ToInt32(@in, 16)), false);
					hexinputmodepos = 1;

					undoStack.AddUndoStep(new UndoStep(new byte[] { (byte)(Convert.ToInt32(@in, 16)) }, old, caret.Offset, action));

					SetCaretPosition(caret.Offset);
				}

				SetCaretPosition(caret.Offset);
			} else {
				UndoAction action;
				string @in;
				if (hexinputmodepos == 1) {
					byte[] _old = buffer.GetBytes(caret.Offset, 1);
					@in = string.Format("{0:X}", buffer.GetByte(caret.Offset));
					if (@in.Length == 1) @in = "0" + @in;
					@in = @in.Remove(1) + inputChar;
					hexinputmodepos = 0;
					hexinputmode = false;
					buffer.SetByte(caret.Offset, (byte)(Convert.ToInt32(@in, 16)), true);
					caret.Offset++;

					if (insertmode) {
						action = UndoAction.Insert;
						_old = null;
					} else {
						action = UndoAction.Overwrite;
					}

					undoStack.AddUndoStep(new UndoStep(new byte[] { (byte)(Convert.ToInt32(@in, 16)) }, _old, caret.Offset - 1, action));

					SetCaretPosition(caret.Offset);
				} else {
					byte[] _old = buffer.GetBytes(caret.Offset, 1);
					@in = inputChar + "0";
					buffer.SetByte(caret.Offset, (byte)(Convert.ToInt32(@in, 16)), true);
					hexinputmodepos = 1;

					if (insertmode) {
						action = UndoAction.Insert;
						_old = null;
					} else {
						action = UndoAction.Overwrite;
					}

					undoStack.AddUndoStep(new UndoStep(new byte[] { (byte)(Convert.ToInt32(@in, 16)) }, _old, caret.Offset, action));
				}

				SetCaretPosition(caret.Offset);
			}

			OnDocumentChanged(EventArgs.Empty);
			InvalidateVisual();
		}
		#endregion

		#region file functions
		public void LoadFile(OpenedFile file, Stream stream)
		{
			buffer.Load(file, stream);
		}

		internal void LoadingFinished()
		{
			this.FileName = fileName;
			selection.Clear();
			Cursor = System.Windows.Input.Cursors.IBeam;
			GC.Collect();
			InvalidateVisual();
		}

		public void SaveFile(OpenedFile file, Stream stream)
		{
			buffer.Save(file, stream);
		}
		#endregion

		#region Offset/position math
		int GetOffsetForPosition(Point position, int charwidth)
		{
			int line = (int)Math.Round(position.Y / fontheight) + topline;
			if (position.Y > (hexRect.Height / 2.0)) line++;

			double col = position.X / (charwidth * underscorewidth);
			double diff = col - Math.Floor(col);
			int ch = diff >= 0.75 ? (int)col + 1 : (int)col;

			if (ch > this.BytesPerLine) ch = this.BytesPerLine;
			if (ch < 0) ch = 0;

			int offset = line * this.BytesPerLine + ch;

			if ((diff > 0.35) && (diff < 0.75)) {
				this.hexinputmodepos = 1;
				this.hexinputmode = true;
			} else {
				this.hexinputmodepos = 0;
				this.hexinputmode = false;
			}

			if (offset < 0) return 0;
			if (offset < this.buffer.BufferSize) return offset;
			return this.buffer.BufferSize;
		}

		Point GetPositionForOffset(int offset, int charwidth)
		{
			int line = (int)(offset / this.BytesPerLine) - this.topline;
			int pline = line * fontheight - 1 * (line - 1) - 1;
			double col = (offset % this.BytesPerLine) * underscorewidth * charwidth + 4;
			if (hexinputmode && !selectionmode && !selection.HasSomethingSelected && this.insertmode) col += (hexinputmodepos * underscorewidth);
			return new Point(col, pline);
		}

		internal int GetOffsetForLine(int line)
		{
			return line * this.BytesPerLine;
		}

		internal int GetLineForOffset(int offset)
		{
			if (offset == 0) return 0;
			return (int)Math.Ceiling((double)offset / (double)this.BytesPerLine) - 1;
		}

		internal int GetMaxVisibleLines()
		{
			return (int)(hexRect.Height / fontheight) + 3;
		}

		internal int GetMaxLines()
		{
			if (buffer == null) return 1;
			int lines = (int)(buffer.BufferSize / this.BytesPerLine);
			if ((buffer.BufferSize % this.BytesPerLine) != 0) lines++;
			return lines;
		}

		static int GetLength(int number)
		{
			int count = 1;
			while (number > 9) {
				number = number / 10;
				count++;
			}
			return count;
		}
		#endregion
	}
}
