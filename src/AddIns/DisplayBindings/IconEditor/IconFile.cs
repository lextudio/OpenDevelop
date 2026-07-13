using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace ICSharpCode.IconEditor
{
	public sealed class IconFile
	{
		bool isCursor;
		bool wellFormed = true;
		Collection<IconEntry> icons;

		public bool WellFormed
		{
			get { return wellFormed; }
		}

		public bool IsCursor
		{
			get { return isCursor; }
			set { isCursor = value; }
		}

		public Collection<IconEntry> Icons
		{
			get { return icons; }
		}

		public IconFile()
		{
			icons = new Collection<IconEntry>();
		}

		public IconFile(Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException("stream");
			if (!stream.CanRead)
				throw new ArgumentException("The stream must be readable", "stream");
			if (!stream.CanSeek)
				throw new ArgumentException("The stream must be seekable", "stream");
			LoadIcon(stream);
		}

		public IconFile(string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
			{
				LoadIcon(fs);
			}
		}

		void LoadIcon(Stream stream)
		{
			BinaryReader r = new BinaryReader(stream);
			if (r.ReadUInt16() != 0)
				throw new InvalidIconException("This is not a valid .ico file.");
			ushort type = r.ReadUInt16();
			if (type == 1)
				isCursor = false;
			else if (type == 2)
				isCursor = true;
			else
				throw new InvalidIconException("This is not a valid .ico file.");
			IconEntry[] icons = new IconEntry[r.ReadUInt16()];
			for (int i = 0; i < icons.Length; i++)
			{
				icons[i] = new IconEntry();
				icons[i].ReadHeader(r, isCursor, ref wellFormed);
			}
			for (int i = 0; i < icons.Length; i++)
			{
				icons[i].ReadData(stream, ref wellFormed);
			}
			this.icons = new Collection<IconEntry>(new List<IconEntry>(icons));
		}

		public void Save(string fileName)
		{
			if (fileName == null)
				throw new ArgumentNullException("fileName");
			using (FileStream fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
			{
				Save(fs);
			}
		}

		public void Save(Stream stream)
		{
			if (stream == null)
				throw new ArgumentNullException("stream");
			if (!stream.CanWrite)
				throw new ArgumentException("The stream must be writeable", "stream");
			if (!stream.CanSeek)
				throw new ArgumentException("The stream must be seekable", "stream");
			BinaryWriter w = new BinaryWriter(stream);
			w.Write((ushort)0);
			w.Write((ushort)(isCursor ? 2 : 1));
			w.Write((ushort)icons.Count);
			foreach (IconEntry e in icons)
			{
				e.WriteHeader(stream, isCursor, w);
			}
			foreach (IconEntry e in icons)
			{
				e.WriteData(stream);
			}
		}

		public IconEntry GetEntry(Size size, IconEntryType bestSupported)
		{
			IconEntry best = null;
			foreach (IconEntry e in this.Icons)
			{
				if (e.Size == size && e.Type <= bestSupported)
				{
					if (best == null || best.ColorDepth < e.ColorDepth)
					{
						best = e;
					}
				}
			}
			return best;
		}

		public void AddEntry(IconEntry entry)
		{
			if (entry == null)
				throw new ArgumentNullException("entry");
			for (int i = 0; i < icons.Count; i++)
			{
				if (icons[i].Width == entry.Width && icons[i].Height == entry.Height && icons[i].ColorDepth == entry.ColorDepth)
				{
					icons[i] = entry;
					return;
				}
			}
			icons.Add(entry);
		}

		public void RemoveEntry(int width, int height, int colorDepth)
		{
			for (int i = 0; i < icons.Count; i++)
			{
				if (icons[i].Width == width && icons[i].Height == height && icons[i].ColorDepth == colorDepth)
				{
					icons.RemoveAt(i);
					break;
				}
			}
		}

		public IEnumerable<Size> AvailableSizes
		{
			get
			{
				return this.Icons.Select(e => e.Size).Distinct().OrderBy(s => s.Width).ThenBy(s => s.Height);
			}
		}

		public IEnumerable<int> AvailableColorDepths
		{
			get
			{
				return this.Icons.Select(e => e.ColorDepth).Distinct().OrderBy(v => v);
			}
		}
	}
}
