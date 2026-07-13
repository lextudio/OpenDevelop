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
	/// A plain, WPF-native replacement for the font settings this addin used to store as
	/// System.Drawing.Font (which required a WinForms/System.Drawing.Common reference).
	/// Serialized as-is by ICSharpCode.Core's Properties (via XamlServices), same as the
	/// WPF Color values Settings already stored.
	/// </summary>
	public sealed class FontSettings : IEquatable<FontSettings>
	{
		public string FamilyName { get; set; } = "Consolas";

		/// <summary>Font size in WPF device-independent units (96 DPI "pixels"), not points.</summary>
		public double Size { get; set; } = 12.667; // ~9.5pt at 96 DPI

		public bool Bold { get; set; }
		public bool Italic { get; set; }
		public bool Underline { get; set; }

		public FontSettings()
		{
		}

		public FontSettings(string familyName, double size, bool bold = false, bool italic = false, bool underline = false)
		{
			FamilyName = familyName;
			Size = size;
			Bold = bold;
			Italic = italic;
			Underline = underline;
		}

		public Typeface ToTypeface()
		{
			return new Typeface(
				new FontFamily(FamilyName),
				Italic ? FontStyles.Italic : FontStyles.Normal,
				Bold ? FontWeights.Bold : FontWeights.Normal,
				FontStretches.Normal);
		}

		public bool Equals(FontSettings other)
		{
			return other != null
				&& FamilyName == other.FamilyName
				&& Size == other.Size
				&& Bold == other.Bold
				&& Italic == other.Italic
				&& Underline == other.Underline;
		}

		public override bool Equals(object obj) => Equals(obj as FontSettings);

		public override int GetHashCode()
		{
			var hash = new HashCode();
			hash.Add(FamilyName);
			hash.Add(Size);
			hash.Add(Bold);
			hash.Add(Italic);
			hash.Add(Underline);
			return hash.ToHashCode();
		}

		public override string ToString()
		{
			var suffix = (Bold ? " Bold" : "") + (Italic ? " Italic" : "") + (Underline ? " Underline" : "");
			return $"{FamilyName}, {Size:0.##}{suffix}";
		}
	}
}
