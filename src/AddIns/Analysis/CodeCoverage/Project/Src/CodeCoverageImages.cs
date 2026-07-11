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

using System.Windows.Media;
using ICSharpCode.SharpDevelop;

namespace ICSharpCode.CodeCoverage
{
	public enum CodeCoverageImageListIndex
	{
		Module,
		Namespace,
		Class,
		Method,
		Property
	}

	/// <summary>
	/// WPF replacement for the old WinForms ImageList (CodeCoverageImageList/GrayScaleBitmap):
	/// reuses the same shared "Icons.16x16.*" resource keys the rest of the WPF UI already draws
	/// from (see e.g. RoslynSymbolIcons). Deliberate simplification vs. the WinForms version: the
	/// old code additionally grayed out icons for zero-coverage nodes via a hand-rolled pixel-level
	/// GDI+ grayscale conversion (GrayScaleBitmap); that visual distinction is dropped here in favor
	/// of the text foreground coloring CodeCoverageTreeNode already applies (Red/Gray/DarkGreen) -
	/// re-adding a WPF grayscale variant per icon would need real design work (an effect/converter)
	/// that wasn't in scope for this migration pass.
	/// </summary>
	public static class CodeCoverageImages
	{
		public static ImageSource GetImage(CodeCoverageImageListIndex index)
		{
			switch (index) {
				case CodeCoverageImageListIndex.Module:
					return GetResourceImage("Icons.16x16.Library");
				case CodeCoverageImageListIndex.Namespace:
					return GetResourceImage("Icons.16x16.NameSpace");
				case CodeCoverageImageListIndex.Class:
					return GetResourceImage("Icons.16x16.Class");
				case CodeCoverageImageListIndex.Method:
					return GetResourceImage("Icons.16x16.Method");
				case CodeCoverageImageListIndex.Property:
					return GetResourceImage("Icons.16x16.Property");
				default:
					return null;
			}
		}

		static ImageSource GetResourceImage(string name)
		{
			var image = SD.ResourceService.GetImage(name);
			return image != null ? image.ImageSource : null;
		}
	}
}
