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

using System.Windows;
using System.Windows.Media;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// A horizontal bar filled to <see cref="Value"/> (0 to 1) of its width. Carried over from the
/// SharpDevelop profiler's controls (<c>ICSharpCode.Profiler.Controls.PercentBar</c>), which had no
/// dependency on the old data model; only the frozen brush and pen are new.
/// </summary>
public sealed class PercentBar : FrameworkElement
{
    static readonly Brush FillBrush = CreateFill();
    static readonly Pen OutlinePen = CreateOutline();

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(PercentBar),
            new FrameworkPropertyMetadata(
                0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (RenderSize.Height > 4 && RenderSize.Width > 0)
        {
            var width = RenderSize.Width * Math.Clamp(Value, 0, 1);
            drawingContext.DrawRectangle(FillBrush, OutlinePen, new Rect(0, 2, width, RenderSize.Height - 4));
        }
    }

    static Brush CreateFill()
    {
        var brush = new LinearGradientBrush(Colors.Red, Colors.Orange, 0);
        brush.Freeze();
        return brush;
    }

    static Pen CreateOutline()
    {
        var pen = new Pen(Brushes.Black, 1);
        pen.Freeze();
        return pen;
    }
}
