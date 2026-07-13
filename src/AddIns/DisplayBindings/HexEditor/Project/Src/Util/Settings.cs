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
using System.Windows.Media;

using ICSharpCode.Core;

namespace HexEditor.Util
{
	/// <summary>
	/// Persisted HexEditor settings. Fonts are stored as <see cref="FontSettings"/> (WPF-native)
	/// instead of System.Drawing.Font.
	/// </summary>
	public class Settings
	{
		static Properties properties = PropertyService.NestedProperties("HexEditorOptions");

		public static Properties Properties {
			get {
				return properties;
			}
		}

		public static Settings CreateDefault()
		{
			Settings settings = new Settings();

			Settings.BytesPerLine = 16;
			Settings.FitToWidth = false;
			Settings.ViewMode = ViewMode.Hexadecimal;

			Settings.DataForeColor = Colors.Black;
			Settings.OffsetForeColor = Colors.Blue;

			Settings.OffsetFont = Settings.DataFont = new FontSettings("Consolas", 12.667);

			return settings;
		}

		public static Color OffsetForeColor {
			get { return properties.Get("OffsetForeColor", Colors.Blue); }
			set { properties.Set("OffsetForeColor", value); }
		}

		public static Color DataForeColor {
			get { return properties.Get("DataForeColor", Colors.Black); }
			set { properties.Set("DataForeColor", value); }
		}

		public static FontSettings OffsetFont {
			get { return properties.Get("OffsetFont", new FontSettings("Consolas", 12.667)); }
			set { properties.Set("OffsetFont", value); }
		}

		public static FontSettings DataFont {
			get { return properties.Get("DataFont", new FontSettings("Consolas", 12.667)); }
			set { properties.Set("DataFont", value); }
		}

		public static bool FitToWidth {
			get { return properties.Get("FitToWidth", false); }
			set { properties.Set("FitToWidth", value); }
		}

		public static int BytesPerLine {
			get { return properties.Get("BytesPerLine", 16); }
			set { properties.Set("BytesPerLine", value); }
		}

		public static ViewMode ViewMode {
			get { return properties.Get("ViewMode", ViewMode.Hexadecimal); }
			set { properties.Set("ViewMode", value); }
		}
	}
}
