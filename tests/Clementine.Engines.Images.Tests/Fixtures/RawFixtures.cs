using System.Buffers.Binary;
using System.Text;

namespace Clementine.Engines.Images.Tests.Fixtures;

/// <summary>
/// Hand-built camera files for decoder tests: a minimal "linear DNG" (what DxO/Lightroom write for demosaiced raws)
/// that LibRaw decodes like any camera raw, and a HEIC container wrapping an x265 frame encoded by FFmpeg.
/// </summary>
public static class RawFixtures
{
    /// <summary>
    /// Uncompressed 16-bit LinearRaw DNG, <paramref name="width"/>×<paramref name="height"/>: left half red,
    /// right half green (camera RGB = linear sRGB via the colour matrix).
    /// </summary>
    public static string LinearDng(string path, int width = 64, int height = 48)
    {
        var pixels = new byte[width * height * 6];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 6;
                var (r, g, b) = x < width / 2 ? (52000, 2000, 2000) : (2000, 52000, 2000);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(o), (ushort)r);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(o + 2), (ushort)g);
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan(o + 4), (ushort)b);
            }
        }

        var tiff = new TiffBuilder();
        tiff.Long(254, 0);                          // NewSubfileType: main image
        tiff.Long(256, (uint)width);
        tiff.Long(257, (uint)height);
        tiff.Shorts(258, 16, 16, 16);
        tiff.Short(259, 1);                         // uncompressed
        tiff.Short(262, 34892);                     // LinearRaw
        tiff.Ascii(271, "Clementine");
        tiff.Ascii(272, "Linear Test");
        tiff.Data(273, pixels);                     // StripOffsets
        tiff.Short(274, 1);
        tiff.Short(277, 3);
        tiff.Long(278, (uint)height);
        tiff.Long(279, (uint)pixels.Length);
        tiff.Short(284, 1);
        tiff.Bytes(50706, 1, 4, 0, 0);              // DNGVersion
        tiff.Bytes(50707, 1, 1, 0, 0);              // DNGBackwardVersion
        tiff.Ascii(50708, "Clementine Linear Test");
        tiff.Long(50717, 65535);                    // WhiteLevel
        // ColorMatrix1 (XYZ → camera): camera RGB = linear sRGB.
        tiff.SRationals(50721, [32406, -15372, -4986, -9689, 18758, 415, 557, -2040, 10570], 10000);
        tiff.Rationals(50728, [1, 1, 1], 1);        // AsShotNeutral
        tiff.Short(50778, 21);                      // CalibrationIlluminant1 = D65
        File.WriteAllBytes(path, tiff.Build());
        return path;
    }

    /// <summary>Minimal little-endian TIFF writer (one IFD, values stored after it).</summary>
    sealed class TiffBuilder
    {
        readonly List<(ushort Tag, ushort Type, uint Count, byte[] Value)> _entries = [];

        public void Short(ushort tag, ushort value) => Add(tag, 3, 1, LE16(value));

        public void Shorts(ushort tag, params ushort[] values) => Add(tag, 3, (uint)values.Length, values.SelectMany(LE16).ToArray());

        public void Long(ushort tag, uint value) => Add(tag, 4, 1, LE32(value));

        public void Bytes(ushort tag, params byte[] values) => Add(tag, 1, (uint)values.Length, values);

        public void Ascii(ushort tag, string text) => Add(tag, 2, (uint)text.Length + 1, [.. Encoding.ASCII.GetBytes(text), 0]);

        public void Rationals(ushort tag, uint[] numerators, uint denominator) =>
            Add(tag, 5, (uint)numerators.Length, numerators.SelectMany(n => LE32(n).Concat(LE32(denominator))).ToArray());

        public void SRationals(ushort tag, int[] numerators, int denominator) =>
            Add(tag, 10, (uint)numerators.Length, numerators.SelectMany(n => LE32((uint)n).Concat(LE32((uint)denominator))).ToArray());

        /// <summary>A LONG offset to a data block (the strip).</summary>
        public void Data(ushort tag, byte[] data) => Add(tag, 0xFFFF, 1, data);

        void Add(ushort tag, ushort type, uint count, byte[] value) => _entries.Add((tag, type, count, value));

        public byte[] Build()
        {
            var entries = _entries.OrderBy(e => e.Tag).ToList();
            var ifdSize = 2 + entries.Count * 12 + 4;
            var dataStart = 8 + ifdSize;
            using var data = new MemoryStream();
            using var ifd = new MemoryStream();
            ifd.Write(LE16((ushort)entries.Count));
            foreach (var (tag, type, count, value) in entries)
            {
                ifd.Write(LE16(tag));
                if (type == 0xFFFF)
                {
                    ifd.Write(LE16(4));
                    ifd.Write(LE32(1));
                    Align(data);
                    ifd.Write(LE32((uint)(dataStart + data.Length)));
                    data.Write(value);
                    continue;
                }
                ifd.Write(LE16(type));
                ifd.Write(LE32(count));
                if (value.Length <= 4)
                {
                    var inline = new byte[4];
                    value.CopyTo(inline, 0);
                    ifd.Write(inline);
                }
                else
                {
                    Align(data);
                    ifd.Write(LE32((uint)(dataStart + data.Length)));
                    data.Write(value);
                }
            }
            ifd.Write(LE32(0));

            using var file = new MemoryStream();
            file.Write("II"u8);
            file.Write(LE16(42));
            file.Write(LE32(8));
            file.Write(ifd.ToArray());
            file.Write(data.ToArray());
            return file.ToArray();
        }

        static void Align(Stream stream)
        {
            if (stream.Length % 2 != 0)
                stream.WriteByte(0);
        }

        static byte[] LE16(ushort value)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            return bytes;
        }

        static byte[] LE32(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            return bytes;
        }
    }
}
