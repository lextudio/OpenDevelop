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
using ICSharpCode.SharpDevelop;
using ICSharpCode.WpfDesign.Designer.Services;

namespace ICSharpCode.WpfDesign.AddIn
{
	/// <summary>
	/// A single entry in the WPF toolbox: either the "Pointer" (selection tool) item,
	/// or a control type that can be created on the design surface. Items are grouped
	/// by <see cref="CategoryName"/> (the WPF popular-controls set, or a referenced
	/// assembly's name).
	/// </summary>
	public sealed class WpfSideTabItem
	{
		public string CategoryName { get; }

		public string DisplayName { get; }

		public ImageSource Icon { get; }

		public ITool Tool { get; }

		/// <summary>Creates the default "Pointer" item (selection tool, no draggable component).</summary>
		public WpfSideTabItem(string categoryName)
		{
			CategoryName = categoryName;
			DisplayName = "Pointer";
			Icon = IconService.GetImageSource("Icons.16x16.FormsDesigner.PointerIcon");
			Tool = null;
		}

		public WpfSideTabItem(string categoryName, Type componentType)
		{
			if (componentType == null)
				throw new ArgumentNullException(nameof(componentType));

			CategoryName = categoryName;
			DisplayName = componentType.Name;
			Icon = null;
			Tool = new CreateComponentTool(componentType);
		}

		public override string ToString()
		{
			return DisplayName;
		}
	}
}
