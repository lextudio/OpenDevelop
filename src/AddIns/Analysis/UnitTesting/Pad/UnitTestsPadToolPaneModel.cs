// Copyright (c) 2026 The OpenDevelop Team
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

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.ViewModels;

namespace ICSharpCode.UnitTesting
{
	/// <summary>
	/// Modern ToolPaneModel for the Unit Tests pad (doc/technotes/ilspy.md "Legacy pad
	/// migration"): registered via <see cref="PadToolPaneProvider"/> from an Autostart command,
	/// so <see cref="AvalonDockLayout"/> routes the legacy <c>UnitTestingPad</c>
	/// PadDescriptor to this model like any built-in pane - persisted in layouts,
	/// re-docked by the reconciliation pass after a layout restore, and never detached by the
	/// legacy-pad path that used to break the pad during debugger session layout churn.
	/// The real pad implementation stays in <see cref="UnitTestsPad"/>; this model owns the
	/// single shared instance (see <see cref="UnitTestsPad.SharedInstance"/>).
	/// </summary>
	public sealed class UnitTestsPadToolPaneModel : ToolPaneModel
	{
		public UnitTestsPadToolPaneModel()
		{
			Title = StringParser.Parse("${res:ICSharpCode.NUnitPad.NUnitPadContent.PadName}");
			ContentId = "UnitTestingPad";
			IsVisible = true; // matches the legacy Pad's `defaultPosition = "Left"` (not Hidden)
			IsCloseable = true;
			LegacyPadClass = typeof(UnitTestsPad).FullName;
			PreferredDockSide = ICSharpCode.SharpDevelop.ViewModels.PreferredDockSide.Left;
			PreferredDockSize = 250; // EnsureDefaultPositionSize's left-pad width
			Pad = new UnitTestsPad();
			Content = Pad.Control;
		}

		public UnitTestsPad Pad { get; }
	}
}
