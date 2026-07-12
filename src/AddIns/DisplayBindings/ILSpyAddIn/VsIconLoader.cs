// This file is NEW glue code written for OpenDevelop (not linked from the ILSpy submodule).
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ICSharpCode.ILSpyAddIn
{
	/// <summary>
	/// Loads tree/member icons from the VS2017 Image Library's own pre-converted XAML vector
	/// format (Microsoft's "AiToXaml" tool output, embedded under Icons/*.xaml - no SVG/PNG
	/// rendering involved). Each file is a
	/// <c>&lt;Viewbox&gt;&lt;Rectangle Fill="{DrawingBrush}"&gt;&lt;/Viewbox&gt;</c> wrapper around
	/// a <see cref="DrawingGroup"/>; this unwraps that down to the DrawingGroup and exposes it as
	/// a plain <see cref="ImageSource"/>, matching what real ILSpy's own Images.cs would have
	/// returned from its (here-replaced) pack-URI-based loader.
	/// </summary>
	static class VsIconLoader
	{
		private static readonly ConcurrentDictionary<string, ImageSource> cache = new();

		/// <summary>Returns null if no embedded icon named "Icons/{name}.xaml" exists.</summary>
		public static ImageSource Load(string name)
		{
			return cache.GetOrAdd(name, LoadCore);
		}

		private static ImageSource LoadCore(string name)
		{
			using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Icons.{name}.xaml");
			if (stream == null)
				return null;

			var viewbox = (Viewbox)XamlReader.Load(stream);
			var rectangle = (Rectangle)viewbox.Child;
			var brush = (DrawingBrush)rectangle.Fill;

			var image = new DrawingImage(brush.Drawing);
			if (image.CanFreeze)
				image.Freeze();
			return image;
		}
	}
}
