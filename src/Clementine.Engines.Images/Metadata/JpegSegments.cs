using System.Buffers.Binary;
using System.Text;

namespace Clementine.Engines.Images.Metadata;

/// <summary>
/// Rewrites the marker segments of a JPEG without touching the compressed image data, so metadata can be removed
/// losslessly. Everything after the end-of-image marker (e.g. MPF preview/depth images) is dropped.
/// </summary>
public static class JpegSegments
{
    const byte Soi = 0xD8, Eoi = 0xD9, Sos = 0xDA, App0 = 0xE0, App1 = 0xE1, App2 = 0xE2, App14 = 0xEE, Com = 0xFE;

    /// <summary>Decision for one segment: keep it, drop it, or replace its payload (after the length field).</summary>
    public delegate SegmentAction SegmentFilter(byte marker, ReadOnlySpan<byte> payload);

    public static bool IsJpeg(ReadOnlySpan<byte> data) => data.Length > 4 && data[0] == 0xFF && data[1] == Soi;

    /// <summary>Removes EXIF, XMP, IPTC/Photoshop data, comments, thumbnails and vendor segments; keeps JFIF, ICC and Adobe colour info.</summary>
    public static byte[]? StripAll(byte[] jpeg) => Rewrite(jpeg, static (marker, payload) =>
    {
        if (marker == App0)
            return payload.StartsWith("JFIF\0"u8) ? SegmentAction.Keep : SegmentAction.Drop;
        if (marker == App2)
            return payload.StartsWith("ICC_PROFILE\0"u8) ? SegmentAction.Keep : SegmentAction.Drop;
        if (marker == App14)
            return payload.StartsWith("Adobe"u8) ? SegmentAction.Keep : SegmentAction.Drop;
        if (marker is >= 0xE1 and <= 0xEF || marker == Com)
            return SegmentAction.Drop;
        return SegmentAction.Keep;
    });

    /// <summary>
    /// Removes location data: GPS tags in EXIF and XMP (via the rewriters), extended XMP, and MPF secondary images
    /// (which carry their own EXIF). Everything else is kept byte for byte.
    /// </summary>
    public static byte[]? RemoveLocation(byte[] jpeg, Func<byte[], byte[]?> rewriteExif, Func<byte[], byte[]?> rewriteXmp) =>
        Rewrite(jpeg, (marker, payload) =>
        {
            if (marker == App1 && payload.StartsWith("Exif\0"u8))
                return SegmentAction.Replace(rewriteExif(payload.ToArray()));
            if (marker == App1 && payload.StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
                return SegmentAction.Replace(rewriteXmp(payload.ToArray()));
            if (marker == App1 && payload.StartsWith("http://ns.adobe.com/xmp/extension/\0"u8))
                return SegmentAction.Drop;
            if (marker == App2 && payload.StartsWith("MPF\0"u8))
                return SegmentAction.Drop;
            return SegmentAction.Keep;
        });

    /// <summary>The payloads of all segments with <paramref name="marker"/> whose payload starts with <paramref name="prefix"/>.</summary>
    public static IReadOnlyList<byte[]> Find(byte[] jpeg, byte marker, ReadOnlySpan<byte> prefix)
    {
        var found = new List<byte[]>();
        var prefixCopy = prefix.ToArray();
        Rewrite(jpeg, (m, payload) =>
        {
            if (m == marker && payload.StartsWith(prefixCopy))
                found.Add(payload.ToArray());
            return SegmentAction.Keep;
        });
        return found;
    }

    /// <summary>Applies <paramref name="filter"/> to every marker segment before the image data. Null if the file isn't a well-formed JPEG.</summary>
    public static byte[]? Rewrite(byte[] jpeg, SegmentFilter filter)
    {
        if (!IsJpeg(jpeg))
            return null;
        using var output = new MemoryStream(jpeg.Length);
        output.Write([0xFF, Soi]);
        var pos = 2;
        while (pos + 1 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
                return null;
            while (pos + 1 < jpeg.Length && jpeg[pos + 1] == 0xFF)
                pos++; // fill bytes
            if (pos + 1 >= jpeg.Length)
                return null;
            var marker = jpeg[pos + 1];
            if (marker == Eoi)
            {
                output.Write([0xFF, Eoi]);
                return output.ToArray();
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                output.Write(jpeg, pos, 2);
                pos += 2;
                continue;
            }
            if (pos + 4 > jpeg.Length)
                return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > jpeg.Length)
                return null;
            var segmentEnd = pos + 2 + length;
            var payload = jpeg.AsSpan(pos + 4, length - 2);

            if (marker == Sos)
            {
                output.Write(jpeg, pos, segmentEnd - pos);
                var dataEnd = FindNextMarker(jpeg, segmentEnd);
                output.Write(jpeg, segmentEnd, dataEnd - segmentEnd);
                pos = dataEnd;
                continue;
            }

            var action = filter(marker, payload);
            if (action.Kind == SegmentActionKind.Keep)
            {
                output.Write(jpeg, pos, segmentEnd - pos);
            }
            else if (action.Kind == SegmentActionKind.Replace && action.Payload is { Length: > 0 and <= 65533 } replacement)
            {
                Span<byte> header = [0xFF, marker, 0, 0];
                BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(replacement.Length + 2));
                output.Write(header);
                output.Write(replacement);
            }
            pos = segmentEnd;
        }
        return null; // no EOI: truncated file
    }

    /// <summary>Scans entropy-coded data for the next real marker (skipping stuffed 0xFF00 and restart markers).</summary>
    static int FindNextMarker(byte[] jpeg, int start)
    {
        for (var i = start; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] != 0xFF)
                continue;
            var next = jpeg[i + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7 || next == 0xFF)
                continue;
            return i;
        }
        return jpeg.Length;
    }

    /// <summary>Human-readable marker name for diagnostics.</summary>
    public static string Describe(byte marker, ReadOnlySpan<byte> payload)
    {
        var id = payload.Length > 0 ? Encoding.ASCII.GetString(payload[..Math.Min(12, payload.Length)]).TrimEnd('\0') : "";
        return $"FF{marker:X2} {id}";
    }
}

public enum SegmentActionKind
{
    Keep,
    Drop,
    Replace,
}

/// <summary>What to do with a JPEG segment.</summary>
public readonly record struct SegmentAction(SegmentActionKind Kind, byte[]? Payload = null)
{
    public static readonly SegmentAction Keep = new(SegmentActionKind.Keep);

    public static readonly SegmentAction Drop = new(SegmentActionKind.Drop);

    /// <summary>Replace the payload; a null payload drops the segment.</summary>
    public static SegmentAction Replace(byte[]? payload) => payload is null ? Drop : new(SegmentActionKind.Replace, payload);
}
