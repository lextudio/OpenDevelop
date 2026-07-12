#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LeXtudio.OpenDevelop.ResourceFiles;

public sealed class IconCursorFile
{
    public IconCursorFile(string fileName, IconCursorFileKind kind, IReadOnlyList<IconCursorFrame> frames)
    {
        FileName = fileName;
        Kind = kind;
        Frames = frames;
    }

    public string FileName { get; }

    public IconCursorFileKind Kind { get; }

    public IReadOnlyList<IconCursorFrame> Frames { get; }
}

public enum IconCursorFileKind
{
    Icon,
    Cursor
}

public sealed class IconCursorFrame
{
    public IconCursorFrame(
        int index,
        int width,
        int height,
        int colorCount,
        int planesOrHotspotX,
        int bitCountOrHotspotY,
        int bytesInResource,
        int imageOffset,
        byte[] data,
        IconCursorFileKind kind)
    {
        Index = index;
        Width = width;
        Height = height;
        ColorCount = colorCount;
        PlanesOrHotspotX = planesOrHotspotX;
        BitCountOrHotspotY = bitCountOrHotspotY;
        BytesInResource = bytesInResource;
        ImageOffset = imageOffset;
        Data = data;
        Kind = kind;
    }

    public int Index { get; }

    public int Width { get; }

    public int Height { get; }

    public int ColorCount { get; }

    public int PlanesOrHotspotX { get; }

    public int BitCountOrHotspotY { get; }

    public int BytesInResource { get; }

    public int ImageOffset { get; }

    public byte[] Data { get; }

    public IconCursorFileKind Kind { get; }

    public bool IsPng => IconCursorFileReader.IsPng(Data);

    public string Format => IsPng ? "PNG" : "DIB";

    public string SizeText => $"{Width} x {Height}";

    public string BitDepthText => Kind == IconCursorFileKind.Icon ? BitCountOrHotspotY.ToString() : string.Empty;

    public string HotspotText => Kind == IconCursorFileKind.Cursor ? $"{PlanesOrHotspotX}, {BitCountOrHotspotY}" : string.Empty;

    public string Description
        => Kind == IconCursorFileKind.Icon
            ? $"{SizeText}, {BitDepthText} bpp, {Format}, {BytesInResource:n0} bytes"
            : $"{SizeText}, hotspot {HotspotText}, {Format}, {BytesInResource:n0} bytes";
}

public static class IconCursorFileReader
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static bool CanRead(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cur", StringComparison.OrdinalIgnoreCase);
    }

    public static IconCursorFile Read(string fileName)
    {
        using var stream = File.OpenRead(fileName);
        return Read(stream, fileName);
    }

    /// <summary>
    /// Parses an ICO/CUR container from an in-memory stream (e.g. bytes embedded in a .resx
    /// entry) rather than a standalone file. <paramref name="displayName"/> only populates
    /// <see cref="IconCursorFile.FileName"/> and does not need to exist on disk.
    /// </summary>
    public static IconCursorFile Read(Stream stream, string displayName)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        var reserved = reader.ReadUInt16();
        var type = reader.ReadUInt16();
        var count = reader.ReadUInt16();
        if (reserved != 0 || (type != 1 && type != 2) || count == 0)
        {
            throw new BadImageFormatException("The file is not a valid ICO/CUR container.");
        }

        var kind = type == 1 ? IconCursorFileKind.Icon : IconCursorFileKind.Cursor;
        var entries = new List<DirectoryEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var width = DecodeDimension(reader.ReadByte());
            var height = DecodeDimension(reader.ReadByte());
            var colorCount = reader.ReadByte();
            _ = reader.ReadByte();
            var planesOrHotspotX = reader.ReadUInt16();
            var bitCountOrHotspotY = reader.ReadUInt16();
            var bytesInResource = checked((int)reader.ReadUInt32());
            var imageOffset = checked((int)reader.ReadUInt32());

            entries.Add(new DirectoryEntry(
                i,
                width,
                height,
                colorCount,
                planesOrHotspotX,
                bitCountOrHotspotY,
                bytesInResource,
                imageOffset));
        }

        var frames = new List<IconCursorFrame>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.ImageOffset < 0 || entry.BytesInResource < 0 || entry.ImageOffset + entry.BytesInResource > stream.Length)
            {
                throw new BadImageFormatException("The ICO/CUR directory points outside the file.");
            }

            stream.Position = entry.ImageOffset;
            var data = reader.ReadBytes(entry.BytesInResource);
            if (data.Length != entry.BytesInResource)
            {
                throw new BadImageFormatException("The ICO/CUR image data is truncated.");
            }

            frames.Add(new IconCursorFrame(
                entry.Index,
                entry.Width,
                entry.Height,
                entry.ColorCount,
                entry.PlanesOrHotspotX,
                entry.BitCountOrHotspotY,
                entry.BytesInResource,
                entry.ImageOffset,
                data,
                kind));
        }

        return new IconCursorFile(displayName, kind, frames.OrderBy(frame => frame.Width).ThenBy(frame => frame.Height).ToArray());
    }

    public static bool IsPng(byte[] data)
    {
        return data.Length >= PngSignature.Length
            && PngSignature.AsSpan().SequenceEqual(data.AsSpan(0, PngSignature.Length));
    }

    private static int DecodeDimension(byte value) => value == 0 ? 256 : value;

    private sealed record DirectoryEntry(
        int Index,
        int Width,
        int Height,
        int ColorCount,
        int PlanesOrHotspotX,
        int BitCountOrHotspotY,
        int BytesInResource,
        int ImageOffset);
}
