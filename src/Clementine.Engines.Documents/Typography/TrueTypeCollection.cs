using System.Buffers.Binary;

namespace Clementine.Engines.Documents.Typography;

/// <summary>Splits one face out of a TrueType/OpenType collection (.ttc) into a standalone font file.</summary>
internal static class TrueTypeCollection
{
    const uint CollectionTag = 0x74746366; // "ttcf"

    public static bool IsCollection(ReadOnlySpan<byte> font) =>
        font.Length >= 12 && BinaryPrimitives.ReadUInt32BigEndian(font) == CollectionTag;

    /// <summary>Rebuilds face <paramref name="index"/> as an sfnt file whose table offsets are relative to its own start.</summary>
    public static byte[] ExtractFace(byte[] collection, int index)
    {
        var span = collection.AsSpan();
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
        if (index < 0 || index >= count)
            index = 0;
        var faceOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(span[(12 + index * 4)..]);

        var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(span[faceOffset..]);
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(span[(faceOffset + 4)..]);
        var tables = new (uint Tag, uint Checksum, int Offset, int Length)[tableCount];
        for (var i = 0; i < tableCount; i++)
        {
            var record = span[(faceOffset + 12 + i * 16)..];
            tables[i] = (BinaryPrimitives.ReadUInt32BigEndian(record), BinaryPrimitives.ReadUInt32BigEndian(record[4..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]), (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
        }

        var headerLength = 12 + tableCount * 16;
        var total = headerLength + tables.Sum(t => Align4(t.Length));
        var output = new byte[total];
        var o = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(o, sfntVersion);
        BinaryPrimitives.WriteUInt16BigEndian(o[4..], tableCount);
        // searchRange / entrySelector / rangeShift as the spec defines them.
        var entrySelector = (ushort)Math.Floor(Math.Log2(Math.Max(1, (int)tableCount)));
        var searchRange = (ushort)((1 << entrySelector) * 16);
        BinaryPrimitives.WriteUInt16BigEndian(o[6..], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(o[8..], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(o[10..], (ushort)(tableCount * 16 - searchRange));

        var position = headerLength;
        for (var i = 0; i < tableCount; i++)
        {
            var (tag, checksum, offset, length) = tables[i];
            var record = o[(12 + i * 16)..];
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], checksum);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)position);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)length);
            span.Slice(offset, length).CopyTo(o[position..]);
            position += Align4(length);
        }
        return output;
    }

    static int Align4(int length) => (length + 3) & ~3;
}
