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
	/// Registers the Unit Tests pad's <see cref="UnitTestsPadToolPaneModel"/> with
	/// <see cref="PadToolPaneProvider"/> (doc/technotes/ilspy.md "Legacy pad migration").
	/// Runs from /SharpDevelop/Autostart, i.e. after the AddInTree is built but before the
	/// workbench is constructed; the provider defers pane construction until the first
	/// PadDescriptor resolution, which happens inside AvalonDockLayout.Attach's ShowPad loop.
	/// </summary>
	public class RegisterUnitTestsPadToolPaneCommand : AbstractCommand
	{
		public override void Run()
		{
			PadToolPaneProvider.Register(
				typeof(UnitTestsPad).FullName,
				() => new UnitTestsPadToolPaneModel());
		}
	}
}
