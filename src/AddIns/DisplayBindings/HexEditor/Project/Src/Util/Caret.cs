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
using System.Windows;
using System.Windows.Media;

namespace HexEditor.Util
{
	/// <summary>
	/// Represents the text caret. In WPF there is no GDI Graphics object to own - instead this
	/// class just tracks position/size and draws itself into whichever DrawingContext the Editor
	/// passes to <see cref="Draw"/> during its own OnRender.
	/// </summary>
	public class Caret
	{
		public Caret(int width, int height, int offset)
		{
			this.Width = width;
			this.Height = height;
			this.Offset = offset;
		}

		public int Width { get; set; }

		public int Height { get; set; }

		public int Offset { get; set; }

		public Point Position { get; private set; }

		public void SetToPosition(Point position)
		{
			this.Position = position;
		}

		public void Draw(DrawingContext context)
		{
			var pen = new Pen(Brushes.Black, 1.0);
			if (Width > 1) {
				context.DrawRectangle(null, pen, new Rect(Position.X - 1, Position.Y, Width, Height - 1));
			} else {
				context.DrawLine(pen, new Point(Position.X - 1, Position.Y), new Point(Position.X - 1, Position.Y + Height - 1));
			}
		}
	}
}
