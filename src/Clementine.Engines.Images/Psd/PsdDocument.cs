using System.Buffers.Binary;
using System.Text;

namespace Clementine.Engines.Images.Psd;

/// <summary>Photoshop colour modes (file header).</summary>
public enum PsdColorMode
{
    Bitmap = 0,
    Grayscale = 1,
    Indexed = 2,
    Rgb = 3,
    Cmyk = 4,
    Multichannel = 7,
    Duotone = 8,
    Lab = 9,
}

/// <summary>Layer-group markers stored in the "lsct" additional layer information.</summary>
public enum PsdSectionType
{
    Layer = 0,
    OpenGroup = 1,
    ClosedGroup = 2,
    GroupEnd = 3,
}

/// <summary>One layer record, as stored (bottom-most first). Pixel data is decoded by ImageMagick; this is the structure.</summary>
public sealed record PsdLayerRecord(
    int Index,
    string Name,
    int Top,
    int Left,
    int Bottom,
    int Right,
    string BlendKey,
    byte Opacity,
    byte FillOpacity,
    bool Clipping,
    bool Hidden,
    PsdSectionType Section)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);

    public bool HasArea => Width > 0 && Height > 0;

    public bool IsGroupStart => Section is PsdSectionType.OpenGroup or PsdSectionType.ClosedGroup;

    public bool IsGroupEnd => Section == PsdSectionType.GroupEnd;
}

/// <summary>
/// Reads the structure of a PSD/PSB file (header, image resources, layer records) without decoding pixels,
/// so Clementine can tell whether the merged composite is real and honour layer visibility, opacity, groups and
/// clipping when it has to rebuild the image from layers.
/// </summary>
public sealed class PsdDocument
{
    const int VersionInfoResource = 0x0421;

    // Additional-info keys whose length field is 8 bytes in PSB files.
    static readonly HashSet<string> LongLengthKeys = new(StringComparer.Ordinal)
    {
        "LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD",
    };

    PsdDocument()
    {
    }

    /// <summary>True for PSB ("large document format").</summary>
    public bool IsLargeDocument { get; private init; }

    public int Channels { get; private init; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    public int Depth { get; private init; }

    public PsdColorMode ColorMode { get; private init; }

    /// <summary>
    /// From the "version info" resource: false when the file was saved without "Maximize compatibility" (the
    /// composite image is then blank), null when the resource is absent.
    /// </summary>
    public bool? HasRealMergedData { get; private init; }

    /// <summary>Layer records, bottom-most first (the order they are stored and composited in).</summary>
    public IReadOnlyList<PsdLayerRecord> Layers { get; private init; } = [];

    /// <summary>Records that carry pixels (not group markers, not empty adjustment layers).</summary>
    public IEnumerable<PsdLayerRecord> PixelLayers => Layers.Where(l => l.HasArea && l.Section == PsdSectionType.Layer);

    /// <summary>Parses <paramref name="path"/>; returns null if it isn't a PSD/PSB or the structure is unreadable.</summary>
    public static PsdDocument? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Parses a PSD/PSB stream (must be seekable).</summary>
    public static PsdDocument? Read(Stream stream)
    {
        var reader = new BigEndianReader(stream);
        if (reader.ReadAscii(4) != "8BPS")
            return null;
        var version = reader.ReadUInt16();
        if (version is not (1 or 2))
            return null;
        var large = version == 2;
        reader.Skip(6);
        var channels = reader.ReadUInt16();
        var height = reader.ReadInt32();
        var width = reader.ReadInt32();
        var depth = reader.ReadUInt16();
        var mode = (PsdColorMode)reader.ReadUInt16();

        reader.Skip(reader.ReadUInt32()); // colour mode data
        var hasRealMergedData = ReadResources(reader);

        IReadOnlyList<PsdLayerRecord> layers = [];
        try
        {
            layers = ReadLayerAndMaskSection(reader, large);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            // A damaged or exotic layer section: the composite is still usable, we just can't rebuild from layers.
        }

        return new PsdDocument
        {
            IsLargeDocument = large,
            Channels = channels,
            Width = width,
            Height = height,
            Depth = depth,
            ColorMode = mode,
            HasRealMergedData = hasRealMergedData,
            Layers = layers,
        };
    }

    static bool? ReadResources(BigEndianReader reader)
    {
        long length = reader.ReadUInt32();
        var end = reader.Position + length;
        bool? hasRealMergedData = null;
        while (reader.Position + 12 <= end)
        {
            reader.ReadAscii(4); // "8BIM" (or a legacy signature)
            var id = reader.ReadUInt16();
            var nameLength = reader.ReadByte();
            reader.Skip(nameLength + ((nameLength + 1) % 2)); // Pascal string padded to even
            long size = reader.ReadUInt32();
            var dataStart = reader.Position;
            if (id == VersionInfoResource && size >= 5)
            {
                reader.ReadUInt32(); // version
                hasRealMergedData = reader.ReadByte() != 0;
            }
            reader.Position = dataStart + size + (size % 2);
        }
        reader.Position = end;
        return hasRealMergedData;
    }

    static IReadOnlyList<PsdLayerRecord> ReadLayerAndMaskSection(BigEndianReader reader, bool large)
    {
        var sectionLength = large ? reader.ReadInt64() : reader.ReadUInt32();
        if (sectionLength <= 0)
            return [];
        var sectionEnd = reader.Position + sectionLength;

        var layerInfoLength = large ? reader.ReadInt64() : reader.ReadUInt32();
        var layerInfoEnd = reader.Position + layerInfoLength;
        IReadOnlyList<PsdLayerRecord> layers = layerInfoLength > 0 ? ReadLayerRecords(reader, large, layerInfoEnd) : [];
        reader.Position = layerInfoEnd;

        if (reader.Position + 4 <= sectionEnd)
            reader.Skip(reader.ReadUInt32()); // global layer mask info

        // 16- and 32-bit documents keep their layers in an "Lr16"/"Lr32" block here instead.
        while (layers.Count == 0 && AlignToSignature(reader, sectionEnd))
        {
            reader.ReadAscii(4);
            var key = reader.ReadAscii(4);
            var length = large && LongLengthKeys.Contains(key) ? reader.ReadInt64() : reader.ReadUInt32();
            var start = reader.Position;
            if (key is "Lr16" or "Lr32" or "Layr")
                layers = ReadLayerRecords(reader, large, start + length);
            reader.Position = start + length;
        }
        return layers;
    }

    static List<PsdLayerRecord> ReadLayerRecords(BigEndianReader reader, bool large, long end)
    {
        var count = Math.Abs((int)reader.ReadInt16());
        var records = new List<PsdLayerRecord>(count);
        for (var i = 0; i < count && reader.Position < end; i++)
        {
            var top = reader.ReadInt32();
            var left = reader.ReadInt32();
            var bottom = reader.ReadInt32();
            var right = reader.ReadInt32();
            var channelCount = reader.ReadUInt16();
            reader.Skip(channelCount * (large ? 10L : 6L));
            reader.ReadAscii(4); // "8BIM"
            var blendKey = reader.ReadAscii(4);
            var opacity = reader.ReadByte();
            var clipping = reader.ReadByte() != 0;
            var flags = reader.ReadByte();
            reader.ReadByte(); // filler
            long extraLength = reader.ReadUInt32();
            var extraEnd = reader.Position + extraLength;

            reader.Skip(reader.ReadUInt32()); // layer mask data
            reader.Skip(reader.ReadUInt32()); // blending ranges
            var nameLength = reader.ReadByte();
            var name = DecodePascalName(reader.ReadBytes(nameLength));
            var padded = (nameLength + 1 + 3) / 4 * 4;
            reader.Skip(padded - nameLength - 1);

            var section = PsdSectionType.Layer;
            byte fill = 255;
            while (AlignToSignature(reader, extraEnd))
            {
                reader.ReadAscii(4);
                var key = reader.ReadAscii(4);
                var length = large && LongLengthKeys.Contains(key) ? reader.ReadInt64() : reader.ReadUInt32();
                var start = reader.Position;
                if (length < 0 || start + length > extraEnd)
                    break;
                switch (key)
                {
                    case "luni" when length >= 4:
                        var chars = (int)Math.Min(reader.ReadUInt32(), (length - 4) / 2);
                        var unicode = Encoding.BigEndianUnicode.GetString(reader.ReadBytes(chars * 2)).TrimEnd('\0');
                        if (unicode.Length > 0)
                            name = unicode;
                        break;
                    case "lsct" or "lsdk" when length >= 4:
                        var type = reader.ReadUInt32();
                        section = type <= 3 ? (PsdSectionType)type : PsdSectionType.Layer;
                        break;
                    case "iOpa" when length >= 1:
                        fill = reader.ReadByte();
                        break;
                }
                reader.Position = start + length;
            }
            reader.Position = extraEnd;

            records.Add(new PsdLayerRecord(i, name, top, left, bottom, right, blendKey, opacity, fill, clipping,
                Hidden: (flags & 0x02) != 0, section));
        }
        return records;
    }

    /// <summary>
    /// Moves to the next "8BIM"/"8B64" signature within a few padding bytes (writers disagree on whether
    /// additional-info blocks are padded to 2 or 4 bytes). False when there is no further block before <paramref name="end"/>.
    /// </summary>
    static bool AlignToSignature(BigEndianReader reader, long end)
    {
        for (var skip = 0; skip < 4; skip++)
        {
            var at = reader.Position + skip;
            if (at + 12 > end)
                return false;
            var saved = reader.Position;
            reader.Position = at;
            var signature = reader.ReadAscii(4);
            reader.Position = at;
            if (signature is "8BIM" or "8B64")
                return true;
            reader.Position = saved;
        }
        return false;
    }

    static string DecodePascalName(byte[] bytes)
    {
        // Photoshop writes the system code page here (and the real name in "luni"); other writers use UTF-8.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Minimal big-endian reader over a seekable stream.</summary>
    sealed class BigEndianReader(Stream stream)
    {
        readonly byte[] _buffer = new byte[8];

        public long Position
        {
            get => stream.Position;
            set
            {
                if (value < 0 || value > stream.Length)
                    throw new EndOfStreamException();
                stream.Position = value;
            }
        }

        public void Skip(long count)
        {
            if (count < 0)
                throw new InvalidDataException("Negative length in PSD structure.");
            Position += count;
        }

        public byte ReadByte()
        {
            var b = stream.ReadByte();
            return b < 0 ? throw new EndOfStreamException() : (byte)b;
        }

        public byte[] ReadBytes(int count)
        {
            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public string ReadAscii(int count) => Encoding.Latin1.GetString(ReadBytes(count));

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(Fill(2));

        public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(Fill(2));

        public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(Fill(4));

        public long ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(Fill(4));

        public long ReadInt64() => BinaryPrimitives.ReadInt64BigEndian(Fill(8));

        ReadOnlySpan<byte> Fill(int count)
        {
            stream.ReadExactly(_buffer, 0, count);
            return _buffer.AsSpan(0, count);
        }
    }
}
