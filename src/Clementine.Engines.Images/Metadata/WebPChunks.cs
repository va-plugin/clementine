using System.Buffers.Binary;
using System.Text;

namespace Clementine.Engines.Images.Metadata;

/// <summary>
/// Edits the RIFF chunks of a WebP file (EXIF and XMP metadata) without re-encoding the image.
/// </summary>
public static class WebPChunks
{
    const byte ExifFlag = 0x08, XmpFlag = 0x04;

    public static bool IsWebP(ReadOnlySpan<byte> data) =>
        data.Length >= 16 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>The payload of the first chunk named <paramref name="fourCc"/> ("EXIF", "XMP "), or null.</summary>
    public static byte[]? Find(byte[] webp, string fourCc)
    {
        if (!IsWebP(webp))
            return null;
        var pos = 12;
        while (pos + 8 <= webp.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(pos + 4));
            if (pos + 8 + size > webp.Length)
                return null;
            if (Encoding.ASCII.GetString(webp, pos, 4) == fourCc)
                return webp.AsSpan(pos + 8, (int)size).ToArray();
            pos += 8 + (int)size + (int)(size & 1);
        }
        return null;
    }

    /// <summary>Removes the EXIF and XMP chunks (ICC, image data and animation are kept).</summary>
    public static byte[]? StripAll(byte[] webp) => Rewrite(webp, static (_, _) => null);

    /// <summary>Rewrites the EXIF and XMP chunks with the given functions (null result = remove the chunk).</summary>
    public static byte[]? Rewrite(byte[] webp, Func<string, byte[], byte[]?> metadataRewriter)
    {
        if (!IsWebP(webp))
            return null;
        var chunks = new List<(string FourCc, byte[] Data)>();
        var pos = 12;
        while (pos + 8 <= webp.Length)
        {
            var fourCc = Encoding.ASCII.GetString(webp, pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(pos + 4));
            if (pos + 8 + size > webp.Length)
                return null;
            var data = webp.AsSpan(pos + 8, (int)size).ToArray();
            if (fourCc is "EXIF" or "XMP ")
            {
                var replaced = metadataRewriter(fourCc, data);
                if (replaced is { Length: > 0 })
                    chunks.Add((fourCc, replaced));
            }
            else
            {
                chunks.Add((fourCc, data));
            }
            pos += 8 + (int)size + (int)(size & 1);
        }

        // Keep the VP8X feature flags in step with the chunks that remain.
        var index = chunks.FindIndex(c => c.FourCc == "VP8X");
        if (index >= 0 && chunks[index].Data.Length >= 1)
        {
            var flags = chunks[index].Data;
            flags[0] = (byte)(flags[0] & ~(ExifFlag | XmpFlag));
            if (chunks.Any(c => c.FourCc == "EXIF"))
                flags[0] |= ExifFlag;
            if (chunks.Any(c => c.FourCc == "XMP "))
                flags[0] |= XmpFlag;
        }

        using var output = new MemoryStream(webp.Length);
        output.Write("RIFF"u8);
        output.Write(new byte[4]); // size, patched below
        output.Write("WEBP"u8);
        Span<byte> size4 = stackalloc byte[4];
        foreach (var (fourCc, data) in chunks)
        {
            output.Write(Encoding.ASCII.GetBytes(fourCc));
            BinaryPrimitives.WriteUInt32LittleEndian(size4, (uint)data.Length);
            output.Write(size4);
            output.Write(data);
            if ((data.Length & 1) != 0)
                output.WriteByte(0);
        }
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }
}

/// <summary>Cheap facts about a WebP file, read from its chunk headers.</summary>
public static class WebPInfo
{
    /// <summary>True when the image data is VP8L (lossless).</summary>
    public static bool IsLossless(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[64];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            var span = header.AsSpan(0, read);
            if (!WebPChunks.IsWebP(span))
                return false;
            var pos = 12;
            while (pos + 8 <= span.Length)
            {
                var fourCc = Encoding.ASCII.GetString(span.Slice(pos, 4));
                if (fourCc == "VP8L")
                    return true;
                if (fourCc is "VP8 " or "ANIM")
                    return false;
                var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
                if (fourCc != "VP8X")
                    break;
                pos += 8 + (int)size + (int)(size & 1);
            }
            // Further chunks (ICCP/EXIF before the bitstream): scan the file.
            stream.Position = 0;
            var all = new byte[Math.Min(stream.Length, 4 * 1024 * 1024)];
            stream.ReadExactly(all);
            return all.AsSpan().IndexOf("VP8L"u8) >= 0 && all.AsSpan().IndexOf("VP8 "u8) < 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
