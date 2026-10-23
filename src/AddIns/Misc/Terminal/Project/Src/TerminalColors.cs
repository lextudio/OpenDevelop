namespace OpenDevelop.Terminal
{
	/// <summary>
	/// The colours a terminal shows for "default" foreground/background and for the 256-colour palette,
	/// as 0xRRGGBB values; plain data so the engine does not depend on WPF resources.
	/// </summary>
	sealed record TerminalColors(uint Background, uint Foreground, bool IsDark)
	{
		// VS Code's default terminal ANSI colours for its dark and light themes.
		static readonly uint[] DarkAnsi = {
			0x000000, 0xcd3131, 0x0dbc79, 0xe5e510, 0x2472c8, 0xbc3fbc, 0x11a8cd, 0xe5e5e5,
			0x666666, 0xf14c4c, 0x23d18b, 0xf5f543, 0x3b8eea, 0xd670d6, 0x29b8db, 0xe5e5e5,
		};

		static readonly uint[] LightAnsi = {
			0x000000, 0xcd3131, 0x00bc00, 0x949800, 0x0451a5, 0xbc05bc, 0x0598bc, 0x555555,
			0x666666, 0xcd3131, 0x14ce14, 0xb5ba00, 0x0451a5, 0xbc05bc, 0x0598bc, 0xa5a5a5,
		};

		/// <summary>Changes whenever the colours do (used to notice an IDE theme switch).</summary>
		public string Signature => $"{Background:x6}/{Foreground:x6}";

		/// <summary>256 RGB triples: the 16 ANSI colours of the theme, then the standard xterm 6x6x6 cube and grey ramp.</summary>
		public byte[] PaletteBytes()
		{
			var ansi = IsDark ? DarkAnsi : LightAnsi;
			var bytes = new byte[256 * 3];
			void Set(int index, uint rgb)
			{
				bytes[index * 3] = (byte)(rgb >> 16);
				bytes[index * 3 + 1] = (byte)(rgb >> 8);
				bytes[index * 3 + 2] = (byte)rgb;
			}
			for (int i = 0; i < 16; i++)
				Set(i, ansi[i]);
			int[] levels = { 0, 95, 135, 175, 215, 255 };
			for (int i = 0; i < 216; i++)
				Set(16 + i, (uint)(levels[i / 36] << 16 | levels[i / 6 % 6] << 8 | levels[i % 6]));
			for (int i = 0; i < 24; i++) {
				int v = 8 + 10 * i;
				Set(232 + i, (uint)(v << 16 | v << 8 | v));
			}
			return bytes;
		}

		public static byte[] Rgb(uint color) => new[] { (byte)(color >> 16), (byte)(color >> 8), (byte)color };
	}
}
