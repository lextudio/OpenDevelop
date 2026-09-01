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
using ICSharpCode.ILSpy.ViewModels;

namespace ICSharpCode.XmlEditor
{
	/// <summary>
	/// Modern (doc/technotes/ilspy.md "Legacy Pad migration", 2026-08-09) replacement for the
	/// legacy AddInTree-registered <see cref="XPathQueryPad"/> (AddInTree pad id "XPathQueryPad").
	/// Not a MEF part - the AddIn's assembly is never scanned by <c>OpenDevelopMefHost</c> - so
	/// it is constructed with a plain <c>new</c> by the <see cref="XPathQueryPad"/> shim on first
	/// real use and registered with the real docking host via <c>IPaneModelHost.Add</c>.
	/// </summary>
	sealed class XPathQueryPadViewModel : ToolPaneModel
	{
		public const string XPathQueryControlProperties = "XPathQueryControl.Options";

		readonly XPathQueryControl xpathQueryControl;
		bool disposed;

		public XPathQueryPadViewModel()
		{
			Title = "XPath Query";
			ContentId = "XPathQueryPad";
			IsVisible = false; // Matches the legacy Pad's `defaultPosition = "Bottom, Hidden"`.
			IsCloseable = true;
			LegacyPadClass = typeof(XPathQueryPad).FullName;
			PreferredDockSide = ICSharpCode.ILSpy.ViewModels.PreferredDockSide.Bottom;

			xpathQueryControl = new XPathQueryControl();
			Content = xpathQueryControl;

			SD.Workbench.ActiveViewContentChanged += ActiveViewContentChanged;
			Properties properties = PropertyService.NestedProperties(XPathQueryControlProperties);
			xpathQueryControl.SetMemento(properties);
		}

		public void Dispose()
		{
			if (!disposed) {
				disposed = true;
				SD.Workbench.ActiveViewContentChanged -= ActiveViewContentChanged;
				Properties properties = xpathQueryControl.CreateMemento();
				PropertyService.SetNestedProperties(XPathQueryControlProperties, properties);
			}
		}

		void ActiveViewContentChanged(object source, EventArgs e)
		{
			xpathQueryControl.ActiveWindowChanged();
		}
	}
}
