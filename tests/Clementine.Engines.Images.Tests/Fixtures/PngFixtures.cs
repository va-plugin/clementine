using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace Clementine.Engines.Images.Tests.Fixtures;

/// <summary>PNG files written chunk by chunk: animated PNGs and "decompression bombs".</summary>
public static class PngFixtures
{
    /// <summary>A real 16×16 two-frame APNG (red, then green), looping, 0.5 s per frame.</summary>
    public static string Apng(string directory, string name = "anim.apng")
    {
        var red = Chunks(Encode(MagickColors.Red));
        var green = Chunks(Encode(MagickColors.Lime));
        using var png = new MemoryStream();
        png.Write(Signature);
        Chunk(png, "IHDR", red.First(c => c.Type == "IHDR").Data);
        Chunk(png, "acTL", [.. Int(2), .. Int(0)]); // 2 frames, loop forever
        Chunk(png, "fcTL", FrameControl(0));
        foreach (var data in red.Where(c => c.Type == "IDAT"))
            Chunk(png, "IDAT", data.Data);
        Chunk(png, "fcTL", FrameControl(1));
        var sequence = 2;
        foreach (var data in green.Where(c => c.Type == "IDAT"))
            Chunk(png, "fdAT", [.. Int(sequence++), .. data.Data]);
        Chunk(png, "IEND", []);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, png.ToArray());
        return path;

        static byte[] Encode(MagickColor color)
        {
            using var frame = new MagickImage(color, 16, 16);
            return frame.ToByteArray(MagickFormat.Png24); // 8-bit RGB for both frames, so they share one IHDR
        }

        static byte[] FrameControl(int sequence) =>
            [.. Int(sequence), .. Int(16), .. Int(16), .. Int(0), .. Int(0), 0, 1, 0, 2, 0, 0]; // 1/2 s, no dispose, source blend
    }

    /// <summary>
    /// A valid 1-bit greyscale PNG of zeros declaring <paramref name="width"/> × <paramref name="height"/> (deflate
    /// shrinks it ~1000×). <paramref name="transparent"/> adds a tRNS chunk, so its header reports an alpha channel.
    /// </summary>
    public static byte[] Bomb(int width, int height, bool transparent = false)
    {
        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 1; // bit depth
        header[9] = 0; // greyscale
        Chunk(png, "IHDR", header);
        if (transparent)
            Chunk(png, "tRNS", [0, 0]); // grey level 0 is see-through

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + (width + 7) / 8];
            for (var y = 0; y < height; y++)
                zlib.Write(row);
        }
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    static byte[] Int(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    static List<(string Type, byte[] Data)> Chunks(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            chunks.Add((Encoding.ASCII.GetString(png, at + 4, 4), png.AsSpan(at + 8, length).ToArray()));
            at += 12 + length;
        }
        return chunks;
    }

    static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typed);
        data.CopyTo(typed, 4);
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
