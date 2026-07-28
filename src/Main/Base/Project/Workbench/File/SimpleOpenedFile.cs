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

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Gui;

namespace ICSharpCode.SharpDevelop.Workbench
{
	/// <summary>
	/// Minimal opened-file implementation for hosts/view contents that only need file identity and view registration.
	/// </summary>
	public class SimpleOpenedFile : OpenedFile
	{
		readonly List<IViewContent> registeredViewContents = new List<IViewContent>();
		
		public SimpleOpenedFile(string fileName)
		{
			FileName = FileName.Create(fileName);
		}
		
		public SimpleOpenedFile(FileName fileName)
		{
			FileName = fileName;
		}
		
		public override event EventHandler FileClosed;
		
		public override IList<IViewContent> RegisteredViewContents {
			get { return registeredViewContents; }
		}
		
		public override void RegisterView(IViewContent view)
		{
			if (view == null)
				throw new ArgumentNullException("view");
			if (!registeredViewContents.Contains(view))
				registeredViewContents.Add(view);
		}
		
		public override void UnregisterView(IViewContent view)
		{
			registeredViewContents.Remove(view);
		}
		
		public void NotifyClosed()
		{
			if (FileClosed != null)
				FileClosed(this, EventArgs.Empty);
		}
	}
}
