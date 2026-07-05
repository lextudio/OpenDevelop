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

namespace ICSharpCode.AvalonEdit.AddIn.ContextActions
{
	/// <summary>
	/// In-memory clipboard ring, replacing the WinForms SideBar-backed ring
	/// (ICSharpCode.SharpDevelop.Gui.TextEditorSideBar), which is out of MVP scope.
	/// </summary>
	static class ClipboardRingService
	{
		const int MaxItems = 20;
		static readonly List<string> items = new List<string>();

		public static List<string> GetClipboardRingItems()
		{
			return new List<string>(items);
		}

		public static void PutInClipboardRing(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return;
			items.Remove(text);
			items.Insert(0, text);
			if (items.Count > MaxItems)
				items.RemoveAt(items.Count - 1);
		}
	}
}
