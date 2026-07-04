// MVP mock: GetBitmap(string)/GetIcon(string) extension methods on IResourceService used to convert the
// WPF-native resource image into a System.Drawing.Bitmap/Icon via the (now-excluded) WinForms bridge
// (IWinFormsService.GetResourceServiceBitmap/Icon, which did real GDI+ interop). Not needed for a WPF-only
// first-boot milestone - callers only use these as a fallback alongside the WPF ImageSource path.
using System.Drawing;
using ICSharpCode.Core;

namespace ICSharpCode.SharpDevelop
{
	public static class ResourceServiceWinFormsExtensions
	{
		public static Bitmap GetBitmap(this IResourceService resourceService, string resourceName)
		{
			return null;
		}

		public static Icon GetIcon(this IResourceService resourceService, string resourceName)
		{
			return null;
		}
	}
}
