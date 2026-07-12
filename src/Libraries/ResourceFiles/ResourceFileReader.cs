#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;

namespace LeXtudio.OpenDevelop.ResourceFiles;

public sealed class ResourceEntry : INotifyPropertyChanged
{
    private string name;
    private string type;
    private string value;
    private string? comment;

    public ResourceEntry(string name, string type, string value, string? comment, int? size, bool isEditable = false)
    {
        this.name = name;
        this.type = type;
        this.value = value;
        this.comment = comment;
        Size = size;
        IsEditable = isEditable;
    }

    public string Name
    {
        get => name;
        set => SetField(ref name, value);
    }

    public string Type
    {
        get => type;
        set => SetField(ref type, value);
    }

    public string Value
    {
        get => value;
        set => SetField(ref this.value, value);
    }

    public string? Comment
    {
        get => comment;
        set => SetField(ref comment, value);
    }

    public int? Size { get; }

    public bool IsEditable { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, newValue))
        {
            return;
        }

        field = newValue;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public static class ResourceFileReader
{
    public static IReadOnlyList<ResourceEntry> Read(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".resx", StringComparison.OrdinalIgnoreCase)
            ? ReadResX(fileName)
            : ReadResources(fileName);
    }

    public static bool CanRead(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".resx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".resources", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ResourceEntry> ReadResX(string fileName)
    {
        var document = XDocument.Load(fileName, LoadOptions.PreserveWhitespace);
        return document.Root?
            .Elements("data")
            .Concat(document.Root.Elements("metadata"))
            .Select(element =>
            {
                var name = (string?)element.Attribute("name") ?? string.Empty;
                var type = (string?)element.Attribute("type") ?? (element.Name.LocalName == "metadata" ? "metadata" : "string");
                var value = element.Element("value")?.Value ?? string.Empty;
                var comment = element.Element("comment")?.Value;
                var editable = type.Equals("string", StringComparison.OrdinalIgnoreCase)
                    || type.Equals("metadata", StringComparison.OrdinalIgnoreCase)
                    || IsBooleanType(type);
                return new ResourceEntry(name, type, value, comment, value.Length, editable);
            })
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray()
            ?? Array.Empty<ResourceEntry>();
    }

    public static void SaveResX(string fileName, IEnumerable<ResourceEntry> entries, Stream stream)
    {
        var document = File.Exists(fileName)
            ? XDocument.Load(fileName, LoadOptions.PreserveWhitespace)
            : CreateResXDocument();

        var root = document.Root ?? throw new InvalidDataException("The .resx document has no root element.");
        root.Elements("data").Remove();
        root.Elements("metadata").Remove();

        foreach (var entry in entries.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var isMetadata = entry.Type.Equals("metadata", StringComparison.OrdinalIgnoreCase);
            var element = new XElement(isMetadata ? "metadata" : "data",
                new XAttribute("name", entry.Name),
                new XAttribute(XNamespace.Xml + "space", "preserve"));

            if (!isMetadata && !entry.Type.Equals("string", StringComparison.OrdinalIgnoreCase))
            {
                element.Add(new XAttribute("type", NormalizeResXType(entry.Type)));
            }

            element.Add(new XElement("value", entry.Value ?? string.Empty));
            if (!string.IsNullOrEmpty(entry.Comment))
            {
                element.Add(new XElement("comment", entry.Comment));
            }

            root.Add(element);
        }

        stream.SetLength(0);
        document.Save(stream);
    }

    public static bool IsBooleanType(string type)
        => type.Equals("System.Boolean", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("System.Boolean,", StringComparison.OrdinalIgnoreCase)
            || type.Equals("bool", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeResXType(string type)
        => IsBooleanType(type) ? "System.Boolean, mscorlib" : type;

    private static XDocument CreateResXDocument()
    {
        return new XDocument(
            new XElement("root",
                new XElement("resheader", new XAttribute("name", "resmimetype"), new XElement("value", "text/microsoft-resx")),
                new XElement("resheader", new XAttribute("name", "version"), new XElement("value", "2.0")),
                new XElement("resheader", new XAttribute("name", "reader"), new XElement("value", "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")),
                new XElement("resheader", new XAttribute("name", "writer"), new XElement("value", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"))));
    }

    private static IReadOnlyList<ResourceEntry> ReadResources(string fileName)
    {
        using var stream = File.OpenRead(fileName);
        using var resources = new ResourcesFile(stream, leaveOpen: false);
        return resources
            .Select(pair => CreateEntry(pair.Key, pair.Value))
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static ResourceEntry CreateEntry(string name, object? value)
    {
        if (value is null)
        {
            return new ResourceEntry(name, "null", string.Empty, null, null);
        }

        if (value is byte[] bytes)
        {
            return new ResourceEntry(name, "byte[]", FormatBytes(bytes), null, bytes.Length);
        }

        if (value is MemoryStream stream)
        {
            return new ResourceEntry(name, "stream", FormatBytes(stream.ToArray()), null, checked((int)stream.Length));
        }

        if (value is ResourceSerializedObject serialized)
        {
            var serializedBytes = serialized.GetBytes();
            var type = string.IsNullOrWhiteSpace(serialized.TypeName) ? "serialized object" : serialized.TypeName!;
            return new ResourceEntry(name, type, FormatBytes(serializedBytes), null, serializedBytes.Length);
        }

        return new ResourceEntry(name, value.GetType().FullName ?? value.GetType().Name, Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty, null, null);
    }

    private static string FormatBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "(empty)";
        }

        var shown = Math.Min(bytes.Length, 64);
        var builder = new StringBuilder(shown * 3 + 32);
        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (shown < bytes.Length)
        {
            builder.Append(" ...");
        }

        return builder.ToString();
    }

    private sealed class ResourcesFile : IEnumerable<KeyValuePair<string, object?>>, IDisposable
    {
        private sealed class ResourcesBinaryReader(Stream input, bool leaveOpen) : BinaryReader(input, Encoding.UTF8, leaveOpen)
        {
            public new int Read7BitEncodedInt() => base.Read7BitEncodedInt();

            public void Seek(long pos, SeekOrigin origin) => BaseStream.Seek(pos, origin);
        }

        private enum ResourceTypeCode
        {
            Null = 0,
            String = 1,
            Boolean = 2,
            Char = 3,
            Byte = 4,
            SByte = 5,
            Int16 = 6,
            UInt16 = 7,
            Int32 = 8,
            UInt32 = 9,
            Int64 = 10,
            UInt64 = 11,
            Single = 12,
            Double = 13,
            Decimal = 14,
            DateTime = 0xF,
            TimeSpan = 0x10,
            ByteArray = 0x20,
            Stream = 33,
            StartOfUserTypes = 0x40
        }

        public const int MagicNumber = unchecked((int)0xBEEFCACE);
        private const int ResourceSetVersion = 2;

        private readonly ResourcesBinaryReader reader;
        private readonly int version;
        private readonly bool usesSerializationFormat;
        private readonly int numResources;
        private readonly string[] typeTable;
        private readonly int[] namePositions;
        private readonly long fileStartPosition;
        private readonly long nameSectionPosition;
        private readonly long dataSectionPosition;
        private long[]? startPositions;

        public ResourcesFile(Stream stream, bool leaveOpen)
        {
            fileStartPosition = stream.Position;
            reader = new ResourcesBinaryReader(stream, leaveOpen);

            const string corrupted = "Resources header corrupted.";
            if (reader.ReadInt32() != MagicNumber)
            {
                throw new BadImageFormatException("Not a .resources file - invalid magic number.");
            }

            var headerVersion = reader.ReadInt32();
            var bytesToSkip = reader.ReadInt32();
            if (bytesToSkip < 0 || headerVersion < 0)
            {
                throw new BadImageFormatException(corrupted);
            }

            if (headerVersion > 1)
            {
                reader.BaseStream.Seek(bytesToSkip, SeekOrigin.Current);
            }
            else
            {
                var readerType = reader.ReadString();
                usesSerializationFormat = readerType == "System.Resources.Extensions.DeserializingResourceReader, System.Resources.Extensions, Version=4.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51";
                reader.ReadString();
            }

            version = reader.ReadInt32();
            if (version != ResourceSetVersion && version != 1)
            {
                throw new BadImageFormatException($"Unsupported resource set version: {version}");
            }

            numResources = reader.ReadInt32();
            if (numResources < 0)
            {
                throw new BadImageFormatException(corrupted);
            }

            var numTypes = reader.ReadInt32();
            if (numTypes < 0)
            {
                throw new BadImageFormatException(corrupted);
            }

            typeTable = new string[numTypes];
            for (var i = 0; i < numTypes; i++)
            {
                typeTable[i] = reader.ReadString();
            }

            var pos = reader.BaseStream.Position - fileStartPosition;
            var alignBytes = unchecked((int)pos) & 7;
            if (alignBytes != 0)
            {
                for (var i = 0; i < 8 - alignBytes; i++)
                {
                    reader.ReadByte();
                }
            }

            reader.Seek(checked(4 * numResources), SeekOrigin.Current);
            namePositions = new int[numResources];
            for (var i = 0; i < numResources; i++)
            {
                var namePosition = reader.ReadInt32();
                if (namePosition < 0)
                {
                    throw new BadImageFormatException(corrupted);
                }

                namePositions[i] = namePosition;
            }

            var dataSectionOffset = reader.ReadInt32();
            if (dataSectionOffset < 0)
            {
                throw new BadImageFormatException(corrupted);
            }

            nameSectionPosition = reader.BaseStream.Position;
            dataSectionPosition = fileStartPosition + dataSectionOffset;
            if (dataSectionPosition < nameSectionPosition)
            {
                throw new BadImageFormatException(corrupted);
            }
        }

        public void Dispose() => reader.Dispose();

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < numResources; i++)
            {
                var name = GetResourceName(i, out var dataOffset);
                yield return new KeyValuePair<string, object?>(name, LoadObject(dataOffset));
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private string GetResourceName(int index, out int dataOffset)
        {
            var pos = nameSectionPosition + namePositions[index];
            byte[] bytes;
            lock (reader)
            {
                reader.Seek(pos, SeekOrigin.Begin);
                var byteLen = reader.Read7BitEncodedInt();
                if (byteLen < 0)
                {
                    throw new BadImageFormatException("Resource name has negative length.");
                }

                bytes = reader.ReadBytes(byteLen);
                if (bytes.Length != byteLen)
                {
                    throw new BadImageFormatException("End of stream within a resource name.");
                }

                dataOffset = reader.ReadInt32();
                if (dataOffset < 0)
                {
                    throw new BadImageFormatException("Negative data offset.");
                }
            }

            return Encoding.Unicode.GetString(bytes);
        }

        private object? LoadObject(int dataOffset)
        {
            try
            {
                lock (reader)
                {
                    return version == 1 ? LoadObjectV1(dataOffset) : LoadObjectV2(dataOffset);
                }
            }
            catch (EndOfStreamException e)
            {
                throw new BadImageFormatException("Invalid resource file.", e);
            }
        }

        private string FindType(int typeIndex)
        {
            if (typeIndex < 0 || typeIndex >= typeTable.Length)
            {
                throw new BadImageFormatException("Type index out of bounds.");
            }

            return typeTable[typeIndex];
        }

        private object? LoadObjectV1(int dataOffset)
        {
            Debug.Assert(System.Threading.Monitor.IsEntered(reader));
            reader.Seek(dataSectionPosition + dataOffset, SeekOrigin.Begin);
            var typeIndex = reader.Read7BitEncodedInt();
            if (typeIndex == -1)
            {
                return null;
            }

            var typeName = FindType(typeIndex);
            var comma = typeName.IndexOf(',');
            if (comma > 0)
            {
                typeName = typeName[..comma];
            }

            return typeName switch
            {
                "System.String" => reader.ReadString(),
                "System.Byte" => reader.ReadByte(),
                "System.SByte" => reader.ReadSByte(),
                "System.Int16" => reader.ReadInt16(),
                "System.UInt16" => reader.ReadUInt16(),
                "System.Int32" => reader.ReadInt32(),
                "System.UInt32" => reader.ReadUInt32(),
                "System.Int64" => reader.ReadInt64(),
                "System.UInt64" => reader.ReadUInt64(),
                "System.Single" => reader.ReadSingle(),
                "System.Double" => reader.ReadDouble(),
                "System.DateTime" => new DateTime(reader.ReadInt64()),
                "System.TimeSpan" => new TimeSpan(reader.ReadInt64()),
                "System.Decimal" => new decimal(new[] { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() }),
                _ => new ResourceSerializedObject(FindType(typeIndex), this, reader.BaseStream.Position, usesSerializationFormat)
            };
        }

        private object? LoadObjectV2(int dataOffset)
        {
            Debug.Assert(System.Threading.Monitor.IsEntered(reader));
            reader.Seek(dataSectionPosition + dataOffset, SeekOrigin.Begin);
            var typeCode = (ResourceTypeCode)reader.Read7BitEncodedInt();
            return typeCode switch
            {
                ResourceTypeCode.Null => null,
                ResourceTypeCode.String => reader.ReadString(),
                ResourceTypeCode.Boolean => reader.ReadBoolean(),
                ResourceTypeCode.Char => (char)reader.ReadUInt16(),
                ResourceTypeCode.Byte => reader.ReadByte(),
                ResourceTypeCode.SByte => reader.ReadSByte(),
                ResourceTypeCode.Int16 => reader.ReadInt16(),
                ResourceTypeCode.UInt16 => reader.ReadUInt16(),
                ResourceTypeCode.Int32 => reader.ReadInt32(),
                ResourceTypeCode.UInt32 => reader.ReadUInt32(),
                ResourceTypeCode.Int64 => reader.ReadInt64(),
                ResourceTypeCode.UInt64 => reader.ReadUInt64(),
                ResourceTypeCode.Single => reader.ReadSingle(),
                ResourceTypeCode.Double => reader.ReadDouble(),
                ResourceTypeCode.Decimal => reader.ReadDecimal(),
                ResourceTypeCode.DateTime => DateTime.FromBinary(reader.ReadInt64()),
                ResourceTypeCode.TimeSpan => new TimeSpan(reader.ReadInt64()),
                ResourceTypeCode.ByteArray => ReadByteArray(),
                ResourceTypeCode.Stream => new MemoryStream(ReadByteArray(), writable: false),
                _ when typeCode >= ResourceTypeCode.StartOfUserTypes => new ResourceSerializedObject(FindType(typeCode - ResourceTypeCode.StartOfUserTypes), this, reader.BaseStream.Position, usesSerializationFormat),
                _ => throw new BadImageFormatException("Invalid type code.")
            };
        }

        private byte[] ReadByteArray()
        {
            var len = reader.ReadInt32();
            if (len < 0)
            {
                throw new BadImageFormatException("Resource with negative length.");
            }

            return reader.ReadBytes(len);
        }

        private long[] GetStartPositions()
        {
            if (startPositions is { } existing)
            {
                return existing;
            }

            lock (reader)
            {
                if (startPositions is { } lockedExisting)
                {
                    return lockedExisting;
                }

                var positions = new long[numResources * 2];
                var outPos = 0;
                for (var i = 0; i < numResources; i++)
                {
                    positions[outPos++] = nameSectionPosition + namePositions[i];
                    GetResourceName(i, out var dataOffset);
                    positions[outPos++] = dataSectionPosition + dataOffset;
                }

                Array.Sort(positions);
                startPositions = positions;
                return positions;
            }
        }

        internal byte[] GetBytesForSerializedObject(long pos, bool hasSerializationFormat)
        {
            var positions = GetStartPositions();
            var i = Array.BinarySearch(positions, pos);
            if (i < 0)
            {
                i = ~i;
            }

            lock (reader)
            {
                var endPos = i == positions.Length ? reader.BaseStream.Length : positions[i];
                var len = checked((int)(endPos - pos));
                reader.Seek(pos, SeekOrigin.Begin);
                if (hasSerializationFormat)
                {
                    reader.Read7BitEncodedInt();
                    len = reader.Read7BitEncodedInt();
                }

                return reader.ReadBytes(len);
            }
        }
    }

    private sealed class ResourceSerializedObject(string? typeName, ResourcesFile file, long position, bool usesSerializationFormat)
    {
        public string? TypeName { get; } = usesSerializationFormat ? typeName : null;

        public byte[] GetBytes() => file.GetBytesForSerializedObject(position, usesSerializationFormat);
    }
}
