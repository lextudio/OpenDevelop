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
using System.Windows.Media;

using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.SharpDevelop.Workbench;

namespace ICSharpCode.AvalonEdit.AddIn
{
	/// <summary>
	/// AvalonEdit colorizer that mirrors ICSharpCode.ILSpy.TextView.ThemeAwareHighlightingColorizer
	/// so the workbench's source editor renders syntax colors exactly like ILSpy's decompiled view:
	/// both editors resolve their C# highlighting through the shared HighlightingManager (ILSpy's
	/// lazy registration re-themes the definition with the current semantic theme), and for any
	/// definition that does NOT carry ILSpy's applied theme colors (recognized by the
	/// "ILSpy.IsThemeAware" property key - e.g. a file opened before ILSpy registered its C#
	/// definition, or a language without theme entries) the same lightness-inversion fallback is
	/// applied while the shell is in the dark theme.
	/// </summary>
	public class ThemeAwareHighlightingColorizer : HighlightingColorizer
	{
		const string IsThemeAwareKey = "ILSpy.IsThemeAware";
		
		readonly Dictionary<HighlightingColor, HighlightingColor> darkColors = new Dictionary<HighlightingColor, HighlightingColor>();
		readonly bool isHighlightingThemeAware;
		
		public ThemeAwareHighlightingColorizer(IHighlighter highlighter, IHighlightingDefinition highlightingDefinition)
			: base(highlighter)
		{
			isHighlightingThemeAware = highlightingDefinition.Properties.TryGetValue(IsThemeAwareKey, out string value)
				&& value == bool.TrueString;
		}
		
		protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
		{
			if (!isHighlightingThemeAware && IdeThemeService.CurrentTheme == IdeThemeService.Dark) {
				color = GetColorForDarkTheme(color);
			}
			base.ApplyColorToElement(element, color);
		}
		
		HighlightingColor GetColorForDarkTheme(HighlightingColor lightColor)
		{
			if (lightColor.Foreground == null && lightColor.Background == null) {
				return lightColor;
			}
			
			if (!darkColors.TryGetValue(lightColor, out HighlightingColor darkColor)) {
				darkColors[lightColor] = darkColor = GetColorForDarkThemeCore(lightColor);
			}
			
			return darkColor;
		}
		
		// Ported from ICSharpCode.ILSpy.Themes.ThemeManager (MIT).
		static HighlightingColor GetColorForDarkThemeCore(HighlightingColor lightColor)
		{
			var darkColor = lightColor.Clone();
			darkColor.Foreground = AdjustForDarkTheme(darkColor.Foreground);
			darkColor.Background = AdjustForDarkTheme(darkColor.Background);
			return darkColor;
		}
		
		static HighlightingBrush AdjustForDarkTheme(HighlightingBrush lightBrush)
		{
			if (lightBrush is SimpleHighlightingBrush simpleBrush && simpleBrush.GetBrush(null) is SolidColorBrush brush) {
				return new SimpleHighlightingBrush(AdjustForDarkTheme(brush.Color));
			}
			return lightBrush;
		}
		
		static Color AdjustForDarkTheme(Color color)
		{
			var c = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
			var (h, s, l) = (c.GetHue(), c.GetSaturation(), c.GetBrightness());
			
			// Invert the lightness, but also increase it a bit
			l = 1f - (float)Math.Pow(l, 1.2f);
			
			// Desaturate the colors, as they'd be too intense otherwise
			if (s > 0.75f && l < 0.75f) {
				s *= 0.75f;
				l *= 1.2f;
			}
			
			var (r, g, b) = HslToRgb(h, s, l);
			return Color.FromArgb(color.A, r, g, b);
		}
		
		static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
		{
			// https://en.wikipedia.org/wiki/HSL_and_HSV#HSL_to_RGB
			
			var c = (1f - Math.Abs(2f * l - 1f)) * s;
			h = h % 360f / 60f;
			var x = c * (1f - Math.Abs(h % 2f - 1f));
			
			var (r1, g1, b1) = (int)Math.Floor(h) switch {
				0 => (c, x, 0f),
				1 => (x, c, 0f),
				2 => (0f, c, x),
				3 => (0f, x, c),
				4 => (x, 0f, c),
				_ => (c, 0f, x)
			};
			
			var m = l - c / 2f;
			var r = (byte)((r1 + m) * 255f);
			var g = (byte)((g1 + m) * 255f);
			var b = (byte)((b1 + m) * 255f);
			return (r, g, b);
		}
	}
}
