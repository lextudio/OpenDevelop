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
using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.WpfDesign.AddIn.Commands
{
	/// <summary>
	/// Opens up the Tools Pad.
	/// </summary>
	class Tools : AbstractMenuCommand
    {
        public override void Run()
        {
            // Looked up by class name, not typeof(ToolsPad): ToolsPad's real implementation
            // lives in the App project (doc/technotes/ilspy.md "Docking and layout replacement"),
            // which this AddIn - like most - only references the Base project, not the App
            // project, so the type itself isn't visible here.
            SD.Workbench.GetPad("ICSharpCode.SharpDevelop.Gui.ToolsPad").BringPadToFront();
        }
    }

	/// <summary>
	/// Opens up the Properties Pad (general-purpose Xceed-based PropertyPad).
	/// </summary>
	class Properties : AbstractMenuCommand
    {
        public override void Run()
        {
            // Looked up by class name, not typeof(PropertyPad): PropertyPad's real
            // implementation lives in the App project (doc/technotes/ilspy.md "Docking and
            // layout replacement"), which this AddIn - like most - only references the Base
            // project, not the App project, so the type itself isn't visible here.
            SD.Workbench.GetPad("ICSharpCode.SharpDevelop.Gui.PropertyPad").BringPadToFront();
        }
    }

	/// <summary>
	/// Opens up the Outline Pad.
	/// </summary>
    class Outline : AbstractMenuCommand
    {
        public override void Run()
        {
            // Same reasoning as Properties above - OutlinePad's real implementation moved to the
            // App project too.
            SD.Workbench.GetPad("ICSharpCode.SharpDevelop.Gui.OutlinePad").BringPadToFront();
        }
    }
}
