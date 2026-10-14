using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.SharpDevelop.Designer.Presentation;
using Xunit;

namespace OpenDevelop.Base.Tests;

public sealed class BrushEyedropperTests
{
	[Fact]
	public void SamplesTheClickedPixelAndUnpremultipliesIt()
	{
		// 2x1 premultiplied BGRA: opaque red, then 50%-alpha blue (premultiplied to 0x80).
		var pixels = new byte[] { 0, 0, 255, 255, 0x80, 0, 0, 0x80 };
		var frame = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);
		Assert.Equal(Color.FromArgb(255, 255, 0, 0), BrushEyedropper.Sample(frame, 0.1, 0.5));
		Assert.Equal(Color.FromArgb(0x80, 0, 0, 255), BrushEyedropper.Sample(frame, 0.9, 0.5));
		// The far edge belongs to the last pixel; outside the frame there is nothing to pick.
		Assert.Equal(Color.FromArgb(0x80, 0, 0, 255), BrushEyedropper.Sample(frame, 1, 1));
		Assert.Null(BrushEyedropper.Sample(frame, 1.5, 0.5));
		Assert.Null(BrushEyedropper.Sample(frame, -0.1, 0.5));
	}
}
