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
using System.Windows;
using System.Windows.Media;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Services;

namespace ICSharpCode.GitAddIn
{
	public static class OverlayIconManager
	{
		public static readonly IProjectBrowserNodeOverlayProvider Provider = new GitProjectBrowserOverlayProvider();

		public static void Invalidate(string fileName)
		{
			GitStatusService.ClearCachedStatus(fileName);
			ICSharpCode.SharpDevelop.SD.GetService<IProjectBrowserOverlayService>()?.Invalidate(fileName);
		}

		sealed class GitProjectBrowserOverlayProvider : IProjectBrowserNodeOverlayProvider
		{
			public ImageSource GetOverlay(string fullPath, bool isDirectory)
			{
				GitFileStatus status = GitStatusService.GetStatusForTreeNode(fullPath, isDirectory);
				return GetImage(status);
			}
			
			public string GetOverlayKey(string fullPath, bool isDirectory)
			{
				GitFileStatus status = GitStatusService.GetStatusForTreeNode(fullPath, isDirectory);
				return GitStatusPresentationService.GetPresentation(status).Key;
			}
		}

		// Only genuinely non-clean states get a badge - a clean tracked file shows nothing, matching
		// VS/VS Code convention (the old GitStatusCache-backed version badged every tracked file with
		// a green checkmark via a separate `git ls-files` pass; GitStatusService, shared with
		// UnoDevelop, doesn't distinguish "clean and tracked" from "not in a git repo at all" - both
		// report GitFileStatus.None - so that checkmark-on-every-file behavior is intentionally
		// dropped rather than reintroduced just for parity).
		public static ImageSource GetImage(GitFileStatus status)
		{
			GitStatusPresentation presentation = GitStatusPresentationService.GetPresentation(status);
			return presentation.HasOverlay ? StatusImages.Get(status, presentation) : null;
		}

		static class StatusImages
		{
			static readonly Dictionary<GitFileStatus, ImageSource> images = new Dictionary<GitFileStatus, ImageSource>();

			public static ImageSource Get(GitFileStatus status, GitStatusPresentation presentation)
			{
				if (images.TryGetValue(status, out ImageSource image))
					return image;

				Color color = (Color)ColorConverter.ConvertFromString(presentation.ColorHex);
				Brush background = new SolidColorBrush(color);
				background.Freeze();
				Brush foreground = Brushes.White;
				DrawingGroup drawing = new DrawingGroup();
				drawing.Children.Add(new GeometryDrawing(background, null, new EllipseGeometry(new Point(8, 8), 7.5, 7.5)));
				drawing.Children.Add(new GeometryDrawing(foreground, null, CreateGlyphGeometry(presentation.Glyph)));
				drawing.Freeze();
				image = new DrawingImage(drawing);
				image.Freeze();
				images[status] = image;
				return image;
			}

			static Geometry CreateGlyphGeometry(string glyph)
			{
				return glyph switch {
					"+" => CreateAddedGeometry(),
					"-" => CreateDeletedGeometry(),
					">" => CreateRenamedGeometry(),
					_ => CreateModifiedGeometry()
				};
			}

			static Geometry CreateAddedGeometry()
			{
				return Geometry.Parse("M7,3 L9,3 L9,7 L13,7 L13,9 L9,9 L9,13 L7,13 L7,9 L3,9 L3,7 L7,7 Z");
			}

			static Geometry CreateDeletedGeometry()
			{
				return Geometry.Parse("M3,7 L13,7 L13,9 L3,9 Z");
			}

			static Geometry CreateModifiedGeometry()
			{
				return Geometry.Parse("M7,3 L9,3 L9,10 L7,10 Z M7,12 L9,12 L9,14 L7,14 Z");
			}

			static Geometry CreateRenamedGeometry()
			{
				return Geometry.Parse("M4,4 L10,8 L4,12 Z M10,4 L13,4 L13,12 L10,12 Z");
			}
		}
	}
}
