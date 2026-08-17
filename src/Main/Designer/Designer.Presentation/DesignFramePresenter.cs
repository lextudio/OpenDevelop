using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ICSharpCode.SharpDevelop.Designer.Presentation
{
	/// <summary>
	/// Owns exactly one <see cref="Image"/> element that displays a decoded design frame,
	/// sized to a <see cref="DesignViewport"/>'s current design-to-surface scale. Deliberately
	/// minimal: it has no opinion on WHERE the image sits (that is each backend's own
	/// Canvas/Grid placement, unchanged) and no opinion on HOW the wire bytes are decoded -
	/// WinForms decodes PNG via <see cref="BitmapImage"/> (native WIC codec); WinUI/Uno decodes
	/// raw BGRA32 via <see cref="BitmapSource.Create(int, int, double, double, PixelFormat,
	/// System.Windows.Media.Imaging.BitmapPalette, System.Array, int)"/> (a deliberate WIC-avoidance
	/// workaround for LibreWPF on macOS) - both remain in their own control, calling
	/// <see cref="SetSource"/> with whatever they decoded.
	/// </summary>
	public sealed class DesignFramePresenter
	{
		public Image Visual { get; }

		public DesignFramePresenter(Stretch stretch, bool snapsToDevicePixels = false,
			HorizontalAlignment horizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment verticalAlignment = VerticalAlignment.Stretch)
		{
			Visual = new Image {
				Stretch = stretch,
				SnapsToDevicePixels = snapsToDevicePixels,
				HorizontalAlignment = horizontalAlignment,
				VerticalAlignment = verticalAlignment
			};
		}

		/// <summary>Sets the already-decoded frame. Sizing is separate (see <see cref="Resize"/>)
		/// since a viewport zoom/pan change resizes the same frame without a new render.</summary>
		public void SetSource(ImageSource? source) => Visual.Source = source;

		/// <summary>Sizes the image to <paramref name="viewport"/>'s current design-to-surface
		/// scale (<c>DesignWidth</c>/<c>DesignHeight</c> * <c>Scale</c>) - the same formula every
		/// backend already computed inline before this was extracted.</summary>
		public void Resize(DesignViewport viewport)
		{
			Visual.Width = viewport.DesignWidth * viewport.Scale;
			Visual.Height = viewport.DesignHeight * viewport.Scale;
		}

		public void Clear() => Visual.Source = null;
	}
}
