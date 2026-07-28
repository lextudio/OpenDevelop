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
				GitFileStatus status = GitStatusService.GetStatus(fullPath);
				return GetImage(status);
			}

			public string GetOverlayKey(string fullPath, bool isDirectory)
			{
				GitFileStatus status = GitStatusService.GetStatus(fullPath);
				return GetImage(status) == null ? null : status.ToString();
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
			return status switch {
				GitFileStatus.Added => StatusImages.Added,
				GitFileStatus.Deleted => StatusImages.Deleted,
				GitFileStatus.Modified => StatusImages.Modified,
				GitFileStatus.Renamed => StatusImages.Renamed,
				GitFileStatus.Untracked => StatusImages.Untracked,
				GitFileStatus.Conflicted => StatusImages.Conflicted,
				_ => null
			};
		}

		static class StatusImages
		{
			static readonly Dictionary<GitFileStatus, ImageSource> images = new Dictionary<GitFileStatus, ImageSource>();

			public static ImageSource Added => Get(GitFileStatus.Added, Color.FromRgb(40, 154, 62), CreateAddedGeometry());
			public static ImageSource Deleted => Get(GitFileStatus.Deleted, Color.FromRgb(211, 47, 47), CreateDeletedGeometry());
			public static ImageSource Modified => Get(GitFileStatus.Modified, Color.FromRgb(245, 160, 30), CreateModifiedGeometry());
			public static ImageSource Renamed => Get(GitFileStatus.Renamed, Color.FromRgb(30, 136, 229), CreateModifiedGeometry());
			public static ImageSource Untracked => Get(GitFileStatus.Untracked, Color.FromRgb(0, 150, 136), CreateAddedGeometry());
			public static ImageSource Conflicted => Get(GitFileStatus.Conflicted, Color.FromRgb(198, 40, 40), CreateConflictedGeometry());

			static ImageSource Get(GitFileStatus status, Color color, Geometry glyph)
			{
				if (images.TryGetValue(status, out ImageSource image))
					return image;

				Brush background = new SolidColorBrush(color);
				background.Freeze();
				Brush foreground = Brushes.White;
				DrawingGroup drawing = new DrawingGroup();
				drawing.Children.Add(new GeometryDrawing(background, null, new EllipseGeometry(new Point(8, 8), 7.5, 7.5)));
				drawing.Children.Add(new GeometryDrawing(foreground, null, glyph));
				drawing.Freeze();
				image = new DrawingImage(drawing);
				image.Freeze();
				images[status] = image;
				return image;
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

			static Geometry CreateConflictedGeometry()
			{
				// Same exclamation-mark shape as Modified's glyph - visually distinct via its color
				// (Conflicted's red matches Deleted's, so relies on the badge's position/shape too),
				// kept intentionally simple rather than inventing a new pictogram for a rare state.
				return Geometry.Parse("M7,3 L9,3 L9,10 L7,10 Z M7,12 L9,12 L9,14 L7,14 Z");
			}
		}
	}
}
