using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ICSharpCode.IconEditor
{
	public static class AlphaTransparentBitmap
	{
		const int BMP_MARK = 19778;

		public unsafe static BitmapSource LoadAlphaTransparentBitmap(Stream stream)
		{
			const int knownHeaderSize = 4 * 3 + 2 * 2 + 4;
			const int MAXSIZE = ushort.MaxValue;

			using (BinaryReader r = new BinaryReader(stream))
			{
				if (r.ReadUInt16() != BMP_MARK)
					throw new ArgumentException("The specified file is not a bitmap!");
				r.ReadInt32();
				r.ReadInt32();
				r.ReadInt32();

				int biSize = r.ReadInt32();
				if (biSize <= knownHeaderSize)
					throw new ArgumentException("biSize invalid: " + biSize);
				if (biSize > 2048)
					throw new ArgumentException("biSize too high: " + biSize);
				int width = r.ReadInt32();
				int height = r.ReadInt32();
				if (width < 0 || height < 0)
					throw new ArgumentException("width and height must be >= 0");
				if (width > MAXSIZE || height > MAXSIZE)
					throw new ArgumentException("width and height must be < " + ushort.MaxValue);
				if (r.ReadInt16() != 1)
					throw new ArgumentException("biPlanes invalid");
				if (r.ReadInt16() != 32)
					throw new ArgumentException("Only 32bit bitmaps are supported!");
				if (r.ReadInt32() != 0)
					throw new ArgumentException("Only uncompressed bitmaps are supported!");

				r.ReadBytes(biSize - knownHeaderSize);

				var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
				bmp.Lock();
				try
				{
					uint* startPos = (uint*)bmp.BackBuffer.ToPointer();
					int stride = bmp.BackBufferStride;
					uint* linePtr = startPos + (height - 1) * (stride / 4);
					for (int y = 0; y < height; y++)
					{
						uint* endPtr = linePtr + width;
						for (uint* p = linePtr; p < endPtr; p++)
						{
							*p = r.ReadUInt32();
						}
						linePtr -= stride / 4;
					}
				}
				finally
				{
					bmp.Unlock();
				}
				return bmp;
			}
		}

		public unsafe static BitmapSource ConvertToAlphaTransparentBitmap(BitmapSource andMask, BitmapSource xorMask)
		{
			if (andMask == null || xorMask == null)
				return null;
			int width = xorMask.PixelWidth;
			int height = xorMask.PixelHeight;
			if (andMask.PixelWidth != width || andMask.PixelHeight != height)
				throw new ArgumentException();

			var bmp = new WriteableBitmap(xorMask);
			bmp.Lock();
			try
			{
				uint* bmpPtr = (uint*)bmp.BackBuffer.ToPointer();
				int stride = bmp.BackBufferStride;

				FormatConvertedBitmap monoMask = new FormatConvertedBitmap(andMask, PixelFormats.Indexed1, null, 0);
				byte[] maskPixels = new byte[monoMask.PixelHeight * monoMask.PixelWidth / 8];
				monoMask.CopyPixels(maskPixels, monoMask.PixelWidth / 8, 0);

				for (int y = 0; y < height; y++)
				{
					int rowOffset = y * stride / 4;
					int maskRowOffset = y * monoMask.PixelWidth / 8;
					for (int x = 0; x < width; x++)
					{
						uint maskByte = (uint)(maskPixels[maskRowOffset + (x >> 3)] << (x & 7));
						if ((maskByte & 0x80) != 0)
						{
							bmpPtr[rowOffset + x] = 0;
						}
					}
				}
			}
			finally
			{
				bmp.Unlock();
			}
			return bmp;
		}
	}
}
