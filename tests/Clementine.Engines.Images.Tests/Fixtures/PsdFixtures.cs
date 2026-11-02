using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace Clementine.Engines.Images.Tests.Fixtures;

/// <summary>A solid-colour layer for a generated PSD.</summary>
public sealed record LayerSpec(string Name, string Color, int X, int Y, int Width, int Height)
{
    public byte Opacity { get; init; } = 255;

    public bool Hidden { get; init; }

    /// <summary>Photoshop blend key ("norm", "mul ", "scrn" …).</summary>
    public string BlendKey { get; init; } = "norm";

    public bool Clipped { get; init; }

    /// <summary>16-bit value written into every channel instead of <see cref="Color"/> (for depth tests).</summary>
    public ushort[]? RawRgb16 { get; init; }
}

/// <summary>Describes a generated PSD/PSB.</summary>
public sealed record PsdSpec(int Width, int Height, IReadOnlyList<LayerSpec> Layers)
{
    public int Depth { get; init; } = 8;

    public bool Cmyk { get; init; }

    /// <summary>Write PSB (large document format).</summary>
    public bool Large { get; init; }

    /// <summary>Replace the merged image with opaque white, as Photoshop does without "Maximize compatibility".</summary>
    public bool BlankComposite { get; init; }

    /// <summary>Add the "version info" resource with hasRealMergedData = 0.</summary>
    public bool MarkNoRealMergedData { get; init; }

    /// <summary>Add the "version info" resource with hasRealMergedData = 1.</summary>
    public bool MarkRealMergedData { get; init; }
}

/// <summary>
/// Builds PSD fixtures with Magick.NET and then patches the layer records so they carry real Photoshop semantics
/// (ImageMagick's writer stores blend keys byte-reversed and never writes hidden/opacity/clipping flags).
/// </summary>
public static class PsdFixtures
{
    public static string Write(string path, PsdSpec spec)
    {
        using var collection = new MagickImageCollection();
        var composite = ReferenceComposite(spec);
        collection.Add(composite);
        foreach (var layer in spec.Layers)
            collection.Add(CreateLayer(layer, spec));

        foreach (var image in collection)
        {
            image.Depth = (uint)spec.Depth;
            if (spec.Cmyk)
                ToCmyk(image);
            else
                image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
        }

        var bytes = collection.ToByteArray(spec.Large ? MagickFormat.Psb : MagickFormat.Psd);
        bytes = Patch(bytes, spec);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>What Photoshop would show: visible layers composited with their opacity and blend mode.</summary>
    public static MagickImage ReferenceComposite(PsdSpec spec)
    {
        var canvas = new MagickImage(MagickColors.Transparent, (uint)spec.Width, (uint)spec.Height);
        canvas.ColorType = ColorType.TrueColorAlpha;
        MagickImage? pending = null; // the current clipping base (with its clipped layers applied)
        var pendingOp = CompositeOperator.Over;
        var baseHidden = false;

        void Flush()
        {
            if (pending is null)
                return;
            canvas.Composite(pending, pendingOp);
            pending.Dispose();
            pending = null;
        }

        foreach (var layer in spec.Layers)
        {
            if (layer.Clipped && (pending is not null || baseHidden))
            {
                if (layer.Hidden || baseHidden)
                    continue;
                using var clipped = Placed(layer, spec);
                pending!.Composite(clipped, CompositeOperator.Atop);
                continue;
            }
            Flush();
            baseHidden = layer.Hidden;
            if (layer.Hidden)
                continue;
            pending = Placed(layer, spec);
            pendingOp = layer.BlendKey == "mul " ? CompositeOperator.Multiply : CompositeOperator.Over;
        }
        Flush();
        return canvas;
    }

    /// <summary>The layer (with its opacity applied) placed on a transparent document-sized canvas.</summary>
    static MagickImage Placed(LayerSpec layer, PsdSpec spec)
    {
        using var image = CreateLayer(layer, spec);
        if (layer.Opacity != 255)
            image.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, layer.Opacity / 255.0);
        var placed = new MagickImage(MagickColors.Transparent, (uint)spec.Width, (uint)spec.Height);
        placed.Composite(image, layer.X, layer.Y, CompositeOperator.Over);
        return placed;
    }

    static MagickImage CreateLayer(LayerSpec layer, PsdSpec spec)
    {
        var image = new MagickImage(new MagickColor(layer.Color), (uint)layer.Width, (uint)layer.Height);
        image.Alpha(AlphaOption.Set);
        if (layer.RawRgb16 is { } raw)
        {
            image.Evaluate(Channels.Red, EvaluateOperator.Set, raw[0]);
            image.Evaluate(Channels.Green, EvaluateOperator.Set, raw[1]);
            image.Evaluate(Channels.Blue, EvaluateOperator.Set, raw[2]);
        }
        image.Label = layer.Name;
        image.Page = new MagickGeometry(layer.X, layer.Y, (uint)layer.Width, (uint)layer.Height);
        return image;
    }

    static void ToCmyk(IMagickImage<float> image)
    {
        // Plain formula conversion: pure sRGB primaries map to pure inks (cyan → C=100 %, etc.).
        image.ColorSpace = ColorSpace.CMYK;
    }

    static byte[] Patch(byte[] psd, PsdSpec spec)
    {
        var large = spec.Large;
        var pos = 26;
        pos += 4 + (int)BinaryPrimitives.ReadUInt32BigEndian(psd.AsSpan(pos));            // colour mode data
        var resourcesAt = pos;
        var resourcesLength = (int)BinaryPrimitives.ReadUInt32BigEndian(psd.AsSpan(pos));
        pos += 4 + resourcesLength;

        var layerSectionAt = pos;
        var layerSectionLength = large ? BinaryPrimitives.ReadInt64BigEndian(psd.AsSpan(pos)) : BinaryPrimitives.ReadUInt32BigEndian(psd.AsSpan(pos));
        pos += large ? 8 : 4;
        var imageDataAt = (int)(pos + layerSectionLength);
        pos += large ? 8 : 4; // layer info length
        var count = Math.Abs((int)BinaryPrimitives.ReadInt16BigEndian(psd.AsSpan(pos)));
        pos += 2;
        if (count != spec.Layers.Count)
            throw new InvalidOperationException($"Expected {spec.Layers.Count} layer records, found {count}.");

        for (var i = 0; i < count; i++)
        {
            var layer = spec.Layers[i];
            pos += 16;
            var channels = BinaryPrimitives.ReadUInt16BigEndian(psd.AsSpan(pos));
            pos += 2 + channels * (large ? 10 : 6);
            if (Encoding.ASCII.GetString(psd, pos, 4) != "8BIM")
                throw new InvalidOperationException("Unexpected layer record layout.");
            Encoding.ASCII.GetBytes(layer.BlendKey).CopyTo(psd, pos + 4);
            psd[pos + 8] = layer.Opacity;
            psd[pos + 9] = (byte)(layer.Clipped ? 1 : 0);
            psd[pos + 10] = (byte)((psd[pos + 10] & ~0x02) | (layer.Hidden ? 0x02 : 0));
            pos += 12;
            var extra = (int)BinaryPrimitives.ReadUInt32BigEndian(psd.AsSpan(pos));
            pos += 4 + extra;
        }

        var output = new MemoryStream();
        output.Write(psd, 0, resourcesAt);
        var resources = psd.AsSpan(resourcesAt + 4, resourcesLength).ToArray();
        if (spec.MarkNoRealMergedData || spec.MarkRealMergedData)
            resources = [.. resources, .. VersionInfoResource(spec.MarkRealMergedData)];
        WriteUInt32(output, (uint)resources.Length);
        output.Write(resources);
        output.Write(psd, layerSectionAt, imageDataAt - layerSectionAt);

        if (spec.BlankComposite)
        {
            var header = psd.AsSpan(0, 26);
            var channelCount = BinaryPrimitives.ReadUInt16BigEndian(header[12..]);
            var height = BinaryPrimitives.ReadInt32BigEndian(header[14..]);
            var width = BinaryPrimitives.ReadInt32BigEndian(header[18..]);
            var depth = BinaryPrimitives.ReadUInt16BigEndian(header[22..]);
            output.Write([0, 0]); // raw
            var white = new byte[channelCount * height * width * (depth / 8)];
            Array.Fill(white, (byte)0xFF);
            output.Write(white);
        }
        else
        {
            output.Write(psd, imageDataAt, psd.Length - imageDataAt);
        }
        return output.ToArray();
    }

    static byte[] VersionInfoResource(bool hasRealMergedData)
    {
        var data = new MemoryStream();
        WriteUInt32(data, 1);
        data.WriteByte(hasRealMergedData ? (byte)1 : (byte)0);
        WriteUnicode(data, "Adobe Photoshop");
        WriteUnicode(data, "Adobe Photoshop 2025");
        WriteUInt32(data, 1);
        var payload = data.ToArray();

        var block = new MemoryStream();
        block.Write("8BIM"u8);
        block.Write([0x04, 0x21]);
        block.Write([0, 0]); // empty name, padded
        WriteUInt32(block, (uint)payload.Length);
        block.Write(payload);
        if (payload.Length % 2 != 0)
            block.WriteByte(0);
        return block.ToArray();
    }

    static void WriteUnicode(Stream stream, string text)
    {
        WriteUInt32(stream, (uint)text.Length);
        stream.Write(Encoding.BigEndianUnicode.GetBytes(text));
    }

    static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
