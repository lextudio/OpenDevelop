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

using System.Windows.Media;

namespace OpenDevelop.Profiler.AddIn.Views;

/// <summary>
/// A colour given as hue (degrees), saturation and value (0 to 1). Carried over from the SharpDevelop
/// profiler's <c>HSVColor</c>, which had no dependency on the old data model. The conversion now clamps
/// its inputs instead of throwing, because angles come from arithmetic on wedge sizes and can land a hair
/// outside the range.
/// </summary>
internal readonly struct HsvColor(double hue, double saturation, double value)
{
	public double Hue { get; } = hue;
	public double Saturation { get; } = saturation;
	public double Value { get; } = value;

	public HsvColor WithValue(double newValue) => new(Hue, Saturation, newValue);

	public Color ToColor()
	{
		var hue = ((Hue % 360) + 360) % 360;
		var saturation = Math.Clamp(Saturation, 0, 1);
		var value = Math.Clamp(Value, 0, 1);

		var sector = (int)Math.Floor(hue / 60);
		var fraction = hue / 60 - sector;
		var p = value * (1 - saturation);
		var q = value * (1 - saturation * fraction);
		var t = value * (1 - saturation * (1 - fraction));

		var (r, g, b) = sector switch
		{
			0 => (value, t, p),
			1 => (q, value, p),
			2 => (p, value, t),
			3 => (p, q, value),
			4 => (t, p, value),
			_ => (value, p, q),
		};
		return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
	}
}
