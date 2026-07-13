using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ICSharpCode.IconEditor
{
	public enum IconEntryType
	{
		Classic = 0,
		TrueColor = 1,
		Compressed = 2
	}

	public sealed class IconEntry
	{
		int width, height, colorDepth;
		bool isCompressed;

		int offsetInFile, sizeInBytes;
		byte[] entryData;

		public int Width
		{
			get { return width; }
		}

		public int Height
		{
			get { return height; }
		}

		public Size Size
		{
			get { return new Size(width, height); }
		}

		public int ColorDepth
		{
			get { return colorDepth; }
		}

		public Point Hotspot { get; set; }

		public static PixelFormat GetPixelFormat(int colorDepth)
		{
			switch (colorDepth)
			{
				case 1:
					return PixelFormats.Indexed1;
				case 4:
					return PixelFormats.Indexed4;
				case 8:
					return PixelFormats.Indexed8;
				case 24:
					return PixelFormats.Rgb24;
				case 32:
					return PixelFormats.Bgra32;
				default:
					throw new NotSupportedException();
			}
		}

		public IconEntryType Type
		{
			get
			{
				if (isCompressed)
					return IconEntryType.Compressed;
				else if (colorDepth == 32)
					return IconEntryType.TrueColor;
				else
					return IconEntryType.Classic;
			}
		}

		public Stream GetEntryData()
		{
			return new MemoryStream(entryData, false);
		}

		public Stream GetImageData()
		{
			const int bmpFileHeaderLength = 14;
			const int positionOfHeightInHeader = 8;

			Stream stream = GetEntryData();
			if (isCompressed)
				return stream;
			using (BinaryReader b = new BinaryReader(stream))
			{
				int biBitCount;
				int headerSize = CheckBitmapHeader(b, out biBitCount);
				MemoryStream output = new MemoryStream();
				BinaryWriter w = new BinaryWriter(output);
				w.Write((ushort)BMP_MARK);
				w.Write(0);
				w.Write(0);
				w.Write(0);
				w.Write(entryData, 0, headerSize);
				output.Position = bmpFileHeaderLength + positionOfHeightInHeader;
				w.Write(height);
				output.Position = output.Length;
				if (biBitCount <= 8)
				{
					int colorTableSize = 4 * (1 << biBitCount);
					w.Write(b.ReadBytes(colorTableSize));
				}
				output.Position = 10;
				w.Write((int)output.Length);
				output.Position = output.Length;

				w.Write(b.ReadBytes(GetBitmapSize(width, height, biBitCount)));

				output.Position = 2;
				w.Write((int)output.Length);
				output.Position = 0;
				return output;
			}
		}

		public Stream GetMaskImageData()
		{
			if (isCompressed)
				throw new InvalidOperationException("Image masks are only used in uncompressed icons.");
			Stream readStream = GetEntryData();
			using (BinaryReader b = new BinaryReader(readStream))
			{
				int biBitCount;
				int headerSize = CheckBitmapHeader(b, out biBitCount);
				MemoryStream output = new MemoryStream();
				BinaryWriter w = new BinaryWriter(output);
				w.Write((ushort)BMP_MARK);
				w.Write(0);
				w.Write(0);
				w.Write(0);

				w.Write(40);
				w.Write((int)width);
				w.Write((int)height);
				w.Write((short)1);
				w.Write((short)1);
				w.Write(0);
				w.Write(GetBitmapSize(width, height, 1));
				w.Write(0);
				w.Write(0);
				w.Write(0);
				w.Write(0);

				w.Write(0);

				w.Write((byte)255);
				w.Write((byte)255);
				w.Write((byte)255);
				w.Write((byte)0);

				output.Position = 10;
				w.Write((int)output.Length);
				output.Position = output.Length;

				if (biBitCount <= 8)
				{
					readStream.Position += 4 * (1 << biBitCount);
				}

				readStream.Position += GetBitmapSize(width, height, biBitCount);

				w.Write(b.ReadBytes(GetBitmapSize(width, height, 1)));

				output.Position = 2;
				w.Write((int)output.Length);
				output.Position = 0;
				return output;
			}
		}

		static int GetStride(int width, int bitsPerPixel)
		{
			const int pack = 4;
			const int bitPack = pack * 8;
			int lineBits = width * bitsPerPixel;
			int packUnits = (lineBits + (bitPack - 1)) / bitPack;
			return packUnits * pack;
		}

		static int GetBitmapSize(int width, int height, int bitsPerPixel)
		{
			return GetStride(width, bitsPerPixel) * height;
		}

		public BitmapSource GetImage()
		{
			Stream data = GetImageData();
			if (IsCompressed || ColorDepth != 32)
			{
				return BitmapFrame.Create(data);
			}
			else
			{
				return AlphaTransparentBitmap.LoadAlphaTransparentBitmap(data);
			}
		}

		public BitmapSource GetMaskImage()
		{
			var stream = GetMaskImageData();
			try
			{
				return BitmapFrame.Create(stream);
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		public void SetEntryData(byte[] entryData)
		{
			if (entryData == null)
				throw new ArgumentNullException("imageData");
			this.entryData = entryData;
			isCompressed = false;
			if (entryData.Length > 8)
			{
				if (entryData[0] == 137 &&
					entryData[1] == 80 &&
					entryData[2] == 78 &&
					entryData[3] == 71 &&
					entryData[4] == 13 &&
					entryData[5] == 10 &&
					entryData[6] == 26 &&
					entryData[7] == 10)
				{
					isCompressed = true;
				}
			}
		}

		int CheckBitmapHeader(BinaryReader b, out int biBitCount)
		{
			const int knownHeaderSize = 4 * 3 + 2 * 2 + 6 * 4;
			const int BI_RGB = 0;

			int biSize = b.ReadInt32();
			if (biSize < knownHeaderSize)
				throw new InvalidIconException("biSize invalid: " + biSize);
			if (b.ReadInt32() != width)
				throw new InvalidIconException("biWidth invalid");
			int biHeight = b.ReadInt32();
			if (biHeight != 2 * height)
				throw new InvalidIconException("biHeight invalid: " + biHeight);
			if (b.ReadInt16() != 1)
				throw new InvalidIconException("biPlanes invalid");
			biBitCount = b.ReadInt16();

			int biCompression = b.ReadInt32();
			if (biCompression != BI_RGB)
				throw new InvalidIconException("biCompression invalid");

			b.ReadInt32();
			b.ReadInt32();
			b.ReadInt32();
			int biClrUsed = b.ReadInt32();
			if (biClrUsed != 0 && biClrUsed != (1 << biBitCount))
				throw new InvalidIconException("biClrUsed invalid");

			b.ReadInt32();

			b.ReadBytes(biSize - knownHeaderSize);
			return biSize;
		}

		public bool IsCompressed
		{
			get { return isCompressed; }
		}

		internal IconEntry()
		{
		}

		public IconEntry(int width, int height, int colorDepth, byte[] imageData)
		{
			this.width = width;
			this.height = height;
			this.colorDepth = colorDepth;
			CheckSize();
			CheckColorDepth();
			SetEntryData(imageData);
		}

		public IconEntry(int width, int height, int colorDepth, BitmapSource bitmap, bool? storeCompressed = null)
		{
			this.width = width;
			this.height = height;
			this.colorDepth = colorDepth;
			CheckSize();
			CheckColorDepth();
			SetImage(bitmap, storeCompressed);
		}

		void CheckSize()
		{
			if (width <= 0 || height <= 0 || width > 256 || height > 256)
			{
				throw new InvalidIconException("Invalid icon size: " + width + "x" + width);
			}
		}

		void CheckColorDepth()
		{
			switch (colorDepth)
			{
				case 1:
				case 4:
				case 8:
				case 32:
				case 16:
				case 24:
					break;
				default:
					throw new InvalidIconException("Unknown color depth: " + colorDepth);
			}
		}

		internal void ReadHeader(BinaryReader r, bool isCursor, ref bool wellFormed)
		{
			width = r.ReadByte();
			height = r.ReadByte();
			if (width == 0) width = 256;
			if (height == 0) height = 256;
			CheckSize();
			byte colorCount = r.ReadByte();
			if (colorCount != 0 && colorCount != 2 && colorCount != 16)
			{
				throw new InvalidIconException("Invalid color count: " + colorCount);
			}
			byte reserved = r.ReadByte();
			if (reserved != 0 && reserved != 255)
			{
				throw new InvalidIconException("Invalid value for reserved");
			}

			if (isCursor)
			{
				colorDepth = -1;
				this.Hotspot = new Point(r.ReadUInt16(), r.ReadUInt16());
				if (this.Hotspot.X >= width || this.Hotspot.Y >= height)
					throw new InvalidIconException("Hotspot is outside image");
			}
			else
			{
				uint planeCount = r.ReadUInt16();
				if (planeCount == 0)
				{
					wellFormed = false;
				}
				if (planeCount > 1)
				{
					throw new InvalidIconException("Invalid number of planes: " + planeCount);
				}
				colorDepth = r.ReadUInt16();
				if (colorDepth == 0)
				{
					if (colorCount == 2)
						colorDepth = 1;
					else if (colorCount == 16)
						colorDepth = 4;
					else if (colorCount == 0)
						colorDepth = 8;
				}
				CheckColorDepth();
			}

			sizeInBytes = r.ReadInt32();
			if (sizeInBytes <= 0)
			{
				throw new InvalidIconException("Invalid entry size: " + sizeInBytes);
			}
			if (sizeInBytes > 10 * 1024 * 1024)
			{
				throw new InvalidIconException("Entry too large: " + sizeInBytes);
			}
			offsetInFile = r.ReadInt32();
			if (offsetInFile <= 0)
			{
				throw new InvalidIconException("Invalid offset in file: " + offsetInFile);
			}
		}

		uint saveOffsetToHeaderPosition;

		internal void WriteHeader(Stream stream, bool isCursor, BinaryWriter w)
		{
			w.Write((byte)(width == 256 ? 0 : width));
			w.Write((byte)(height == 256 ? 0 : height));
			w.Write((byte)(colorDepth == 4 ? 16 : 0));
			w.Write((byte)0);
			if (isCursor)
			{
				w.Write((ushort)Hotspot.X);
				w.Write((ushort)Hotspot.Y);
			}
			else
			{
				w.Write((ushort)1);
				w.Write((ushort)colorDepth);
			}
			w.Write((int)entryData.Length);
			saveOffsetToHeaderPosition = (uint)stream.Position;
			w.Write((uint)0);
		}

		internal void ReadData(Stream stream, ref bool wellFormed)
		{
			stream.Position = offsetInFile;
			byte[] imageData = new byte[sizeInBytes];
			int pos = 0;
			while (pos < imageData.Length)
			{
				int c = stream.Read(imageData, pos, imageData.Length - pos);
				if (c == 0)
					throw new InvalidIconException("Unexpected end of stream");
				pos += c;
			}
			SetEntryData(imageData);
			if (isCompressed)
			{
				if (colorDepth == -1)
					colorDepth = 32;
			}
			else
			{
				using (BinaryReader r = new BinaryReader(new MemoryStream(imageData, false)))
				{
					int biBitCount;
					CheckBitmapHeader(r, out biBitCount);
					if (colorDepth == -1)
					{
						colorDepth = biBitCount;
						CheckColorDepth();
					}
					else if (biBitCount != colorDepth)
					{
						wellFormed = false;
						colorDepth = biBitCount;
						CheckColorDepth();
					}
				}
			}
		}

		internal void WriteData(Stream stream)
		{
			uint pos = (uint)stream.Position;
			stream.Position = saveOffsetToHeaderPosition;
			stream.Write(BitConverter.GetBytes(pos), 0, 4);
			stream.Position = pos;
			stream.Write(entryData, 0, entryData.Length);
		}

		public unsafe void SetImage(BitmapSource bitmap, bool? storeCompressed)
		{
			if (bitmap.PixelWidth != width || bitmap.PixelHeight != height)
			{
				bitmap = new TransformedBitmap(bitmap, new ScaleTransform(
					(double)width / bitmap.PixelWidth,
					(double)height / bitmap.PixelHeight));
			}
			PixelFormat format = GetPixelFormat(colorDepth);
			if (storeCompressed ?? (colorDepth == 32 && (width > 48 || height > 48)))
			{
				bitmap = ConvertFormat(bitmap, format);
				using (MemoryStream ms = new MemoryStream())
				{
					PngBitmapEncoder encoder = new PngBitmapEncoder();
					encoder.Frames.Add(BitmapFrame.Create(bitmap));
					encoder.Save(ms);
					SetEntryData(ms.ToArray());
				}
			}
			else
			{
				FormatConvertedBitmap converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
				int maskStride = GetStride(width, 1);
				byte[] andMask = new byte[GetBitmapSize(width, height, 1)];

				byte[] bgraPixels = new byte[width * height * 4];
				converted.CopyPixels(bgraPixels, width * 4, 0);

				for (int y = 0; y < height; y++)
				{
					int rowStart = y * width * 4;
					for (int x = 0; x < width; x++)
					{
						int pixelOffset = rowStart + x * 4;
						byte a = bgraPixels[pixelOffset + 3];
						if (a < 128)
						{
							andMask[y * maskStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
							if (colorDepth < 32)
							{
								bgraPixels[pixelOffset] = 0;
								bgraPixels[pixelOffset + 1] = 0;
								bgraPixels[pixelOffset + 2] = 0;
								bgraPixels[pixelOffset + 3] = 255;
							}
						}
					}
				}

				bitmap = ConvertFormat(BitmapSource.Create(width, height, 96, 96, format, null,
					bgraPixels, width * 4), format);

				MemoryStream ms = new MemoryStream();
				BinaryWriter w = new BinaryWriter(ms);
				w.Write(40);
				w.Write((int)width);
				w.Write((int)height * 2);
				w.Write((ushort)1);
				w.Write((ushort)colorDepth);
				w.Write(0);
				w.Write(0);
				w.Write(0);
				w.Write(0);
				w.Write(0);
				w.Write(0);

				if (colorDepth <= 8)
				{
					BitmapPalette palette = bitmap.Palette;
					int colorCount = 1 << colorDepth;
					for (int i = 0; i < colorCount; i++)
					{
						if (i < palette.Colors.Count)
						{
							Color c = palette.Colors[i];
							w.Write(c.B);
							w.Write(c.G);
							w.Write(c.R);
							w.Write((byte)0);
						}
						else
						{
							w.Write(0);
						}
					}
				}

				byte[] pixelData = new byte[GetBitmapSize(width, height, colorDepth)];
				bitmap.CopyPixels(pixelData, GetStride(width, colorDepth), 0);
				int srcStride = GetStride(width, colorDepth);
				byte[] lineBuffer = new byte[srcStride];
				for (int y = height - 1; y >= 0; y--)
				{
					Buffer.BlockCopy(pixelData, y * srcStride, lineBuffer, 0, srcStride);
					w.Write(lineBuffer);
				}
				w.Write(andMask);
				SetEntryData(ms.ToArray());
			}
		}

		static BitmapSource ConvertFormat(BitmapSource bitmap, PixelFormat targetFormat)
		{
			if (bitmap.Format == targetFormat)
				return bitmap;
			return new FormatConvertedBitmap(bitmap, targetFormat, null, 0);
		}

		public override string ToString()
		{
			return string.Format("[IconEntry {0}x{1}x{2}]", this.width, this.height, this.colorDepth);
		}

		public BitmapSource ExportArgbBitmap()
		{
			if (colorDepth == 32)
			{
				return GetImage();
			}
			else if (isCompressed)
			{
				BitmapFrame image;
				using (Stream data = GetImageData())
				{
					image = BitmapFrame.Create(data);
				}
				return new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
			}
			else
			{
				BitmapSource mask = GetMaskImage();
				BitmapSource image = GetImage();
				AlphaTransparentBitmap.ConvertToAlphaTransparentBitmap(mask, image);
				return image;
			}
		}

		const int BMP_MARK = 19778;
	}
}
