using System.Buffers.Binary;
using System.Text;
using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.Engines.Images.Tests.Fixtures;

/// <summary>
/// Builds a real HEIC file: FFmpeg/x265 encodes one frame, and we wrap the HEVC bitstream in a minimal HEIF
/// container (ftyp, meta with hdlr/pitm/iloc/iinf/iprp[hvcC, ispe]/ipma, mdat) — the same structure iPhones write
/// for a single-tile image.
/// </summary>
public static class HeicFixture
{
    /// <summary>
    /// 64×48 HEIC: left half red, right half blue. With <paramref name="rotatedLikeAnIPhone"/> the file also carries
    /// an "irot" transform (90° clockwise) and an Exif item with orientation 6, exactly like a portrait iPhone photo:
    /// displayed correctly it is 48×64 with red on top.
    /// </summary>
    public static async Task<string> CreateAsync(string directory, string name = "iphone.heic", bool rotatedLikeAnIPhone = false)
    {
        const int width = 64, height = 48;
        var hevcPath = Path.Combine(directory, "frame.hevc");
        var result = await ProcessRunner.RunAsync(MediaFixtures.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"color=c=red:s={width}x{height}:d=1",
            "-vf", $"drawbox=x={width / 2}:y=0:w={width / 2}:h={height}:color=blue:t=fill,format=yuv420p",
            "-frames:v", "1", "-c:v", "libx265", "-x265-params", "log-level=error:keyint=1",
            "-f", "hevc", hevcPath,
        ]);
        if (!result.Succeeded)
            throw new InvalidOperationException($"x265 encode failed: {result.StdErrTail}");

        var nals = SplitAnnexB(File.ReadAllBytes(hevcPath));
        File.Delete(hevcPath);
        var vps = nals.First(n => Type(n) == 32);
        var sps = nals.First(n => Type(n) == 33);
        var pps = nals.First(n => Type(n) == 34);
        var slices = nals.Where(n => Type(n) < 32).ToList();

        var payload = new MemoryStream();
        foreach (var slice in slices)
        {
            payload.Write(BE32((uint)slice.Length));
            payload.Write(slice);
        }
        var imageLength = (uint)payload.Length;
        uint exifLength = 0;
        if (rotatedLikeAnIPhone)
        {
            var exif = new ImageMagick.ExifProfile();
            exif.SetValue(ImageMagick.ExifTag.Orientation, (ushort)6);
            exif.SetValue(ImageMagick.ExifTag.Make, "Apple");
            var exifBytes = exif.ToByteArray()!; // "Exif\0\0" + TIFF
            payload.Write(BE32(6));               // offset of the TIFF header within the item
            payload.Write(exifBytes);
            exifLength = (uint)(4 + exifBytes.Length);
        }

        var ftyp = Box("ftyp", [.. "heic"u8, .. BE32(0), .. "mif1"u8, .. "heic"u8]);
        byte[] Meta(uint dataOffset)
        {
            byte[] location = [.. BE16(1), .. BE16(0), .. BE16(1), .. BE32(dataOffset), .. BE32(imageLength)];
            byte[] infos = FullBox("infe", 2, 0, [.. BE16(1), .. BE16(0), .. "hvc1"u8, 0]);
            byte[] properties = [.. HvcC(vps, sps, pps), .. FullBox("ispe", 0, 0, [.. BE32(width), .. BE32(height)])];
            byte[] associations = [2, 0x81, 0x02];
            byte[] references = [];
            ushort items = 1;
            if (rotatedLikeAnIPhone)
            {
                items = 2;
                location = [.. location, .. BE16(2), .. BE16(0), .. BE16(1), .. BE32(dataOffset + imageLength), .. BE32(exifLength)];
                infos = [.. infos, .. FullBox("infe", 2, 0, [.. BE16(2), .. BE16(0), .. "Exif"u8, 0])];
                properties = [.. properties, .. Box("irot", [3])]; // 3 × 90° anticlockwise = 90° clockwise
                associations = [3, 0x81, 0x02, 0x83];
                references = FullBox("iref", 0, 0, Box("cdsc", [.. BE16(2), .. BE16(1), .. BE16(1)]));
            }
            return FullBox("meta", 0, 0,
            [
                .. FullBox("hdlr", 0, 0, [.. BE32(0), .. "pict"u8, .. BE32(0), .. BE32(0), .. BE32(0), 0]),
                .. FullBox("pitm", 0, 0, BE16(1)),
                .. FullBox("iloc", 0, 0, [0x44, 0x00, .. BE16(items), .. location]),
                .. FullBox("iinf", 0, 0, [.. BE16(items), .. infos]),
                .. references,
                .. Box("iprp", [.. Box("ipco", properties), .. FullBox("ipma", 0, 0, [.. BE32(1), .. BE16(1), .. associations])]),
            ]);
        }
        var metaLength = Meta(0).Length;
        var meta = Meta((uint)(ftyp.Length + metaLength + 8));
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [.. ftyp, .. meta, .. Box("mdat", payload.ToArray())]);
        return path;
    }

    static byte[] HvcC(byte[] vps, byte[] sps, byte[] pps)
    {
        // profile_tier_level of the SPS (after the 2-byte NAL header and 1 byte of ids) maps 1:1 onto hvcC bytes 1..12.
        var rbsp = Unescape(sps);
        var config = new MemoryStream();
        config.WriteByte(1);
        config.Write(rbsp, 3, 12);
        config.Write([0xF0, 0x00, 0xFC, 0xFD, 0xF8, 0xF8, 0x00, 0x00, 0x0F, 3]);
        foreach (var nal in new[] { vps, sps, pps })
        {
            config.WriteByte((byte)(0x80 | Type(nal)));
            config.Write(BE16(1));
            config.Write(BE16((ushort)nal.Length));
            config.Write(nal);
        }
        return Box("hvcC", config.ToArray());
    }

    static int Type(byte[] nal) => (nal[0] >> 1) & 0x3F;

    static List<byte[]> SplitAnnexB(byte[] stream)
    {
        var starts = new List<(int Start, int Prefix)>();
        for (var i = 0; i + 3 <= stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                starts.Add((i + 3, 3));
                i += 2;
            }
        }
        var nals = new List<byte[]>();
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1].Start - 3 : stream.Length;
            while (end > starts[k].Start && stream[end - 1] == 0)
                end--; // trailing zero of a 4-byte start code
            nals.Add(stream[starts[k].Start..end]);
        }
        return nals;
    }

    static byte[] Unescape(byte[] nal)
    {
        // Drops emulation-prevention bytes (00 00 03 → 00 00).
        var output = new List<byte>(nal.Length);
        var zeros = 0;
        foreach (var b in nal)
        {
            if (zeros >= 2 && b == 3)
            {
                zeros = 0;
                continue;
            }
            output.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return [.. output];
    }

    static byte[] Box(string type, byte[] content) => [.. BE32((uint)(8 + content.Length)), .. Encoding.ASCII.GetBytes(type), .. content];

    static byte[] FullBox(string type, byte version, uint flags, byte[] content) =>
        Box(type, [version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags, .. content]);

    static byte[] BE16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    static byte[] BE16(int value) => BE16((ushort)value);

    static byte[] BE32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    static byte[] BE32(int value) => BE32((uint)value);
}
