namespace Clementine.Engines.Images.Imaging;

/// <summary>Orientation, colour-space and dynamic-range normalisation shared by every operation.</summary>
public static class ColorPipeline
{
    /// <summary>Rotates/flips the pixels according to the EXIF orientation, then records the image as upright.</summary>
    public static void Orient(IMagickImage<QuantumType> image)
    {
        if (image.Orientation is not (OrientationType.Undefined or OrientationType.TopLeft))
            image.AutoOrient();
        image.Orientation = OrientationType.TopLeft;
        if (image.GetExifProfile() is { } exif && exif.GetValue(ExifTag.Orientation) is { } tag && tag.Value != 1)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            image.SetProfile(exif);
        }
    }

    /// <summary>
    /// Converts CMYK, Lab and other non-display colour models to sRGB using the embedded ICC profile (or U.S. Web
    /// Coated SWOP for untagged CMYK). sRGB, grey and linear RGB are left alone. Returns true when it converted.
    /// </summary>
    public static bool ToDisplayColorSpace(IMagickImage<QuantumType> image)
    {
        switch (image.ColorSpace)
        {
            case ColorSpace.sRGB or ColorSpace.Gray or ColorSpace.RGB or ColorSpace.LinearGray or ColorSpace.Undefined or ColorSpace.Transparent:
                return false;
            case ColorSpace.CMYK or ColorSpace.CMY:
                var embedded = image.GetColorProfile();
                var converted = embedded is { ColorSpace: ColorSpace.CMYK }
                    ? image.TransformColorSpace(ColorProfiles.SRGB)
                    : image.TransformColorSpace(ColorProfiles.USWebCoatedSWOP, ColorProfiles.SRGB);
                if (!converted || image.ColorSpace != ColorSpace.sRGB)
                    image.ColorSpace = ColorSpace.sRGB;
                return true;
            default:
                image.ColorSpace = ColorSpace.sRGB;
                return true;
        }
    }

    /// <summary>
    /// Converts pixels tagged with a non-sRGB RGB profile (Display P3, Adobe RGB …) to sRGB, for outputs that mix
    /// several images on one canvas. Returns true when it converted.
    /// </summary>
    public static bool ToSrgbProfile(IMagickImage<QuantumType> image)
    {
        if (image.GetColorProfile() is not { ColorSpace: ColorSpace.sRGB } profile || IsSrgb(profile))
            return false;
        return image.TransformColorSpace(ColorProfiles.SRGB);
    }

    /// <summary>Whether an ICC profile is (a variant of) sRGB.</summary>
    public static bool IsSrgb(IColorProfile profile) =>
        profile.Description?.Contains("sRGB", StringComparison.OrdinalIgnoreCase) == true
        || profile.Description?.Contains("IEC61966-2", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Tone-maps linear HDR pixels (EXR/HDR) into display sRGB with a global Reinhard operator scaled to the scene's
    /// log-average luminance; scenes that already fit in 0..1 are only gamma-encoded, never darkened.
    /// </summary>
    /// <returns>A new 16-bit sRGB image; the caller replaces (and disposes) the source.</returns>
    public static MagickImage ToneMapToDisplay(IMagickImage<QuantumType> image)
    {
        var linear = (IMagickImage<QuantumType>)image.Clone();
        try
        {
            // ImageMagick labels EXR as sRGB with gamma 1.0 (the data is linear) and Radiance HDR as linear RGB.
            var isLinear = linear.ColorSpace is ColorSpace.RGB or ColorSpace.LinearGray || Math.Abs(linear.Gamma - 1.0) < 0.01;
            if (!isLinear)
                linear.ColorSpace = ColorSpace.RGB;

            var max = (float)Quantum.Max;
            var hasAlpha = linear.HasAlpha;
            using var pixels = linear.GetPixelsUnsafe();
            int red = Index(pixels, PixelChannel.Red), green = Index(pixels, PixelChannel.Green),
                blue = Index(pixels, PixelChannel.Blue), alpha = Index(pixels, PixelChannel.Alpha);
            var source = pixels.ToArray() ?? [];
            var stride = (int)linear.ChannelCount;
            var count = source.Length / stride;
            if (red < 0)
                return new MagickImage(image);
            if (green < 0 || blue < 0)
                green = blue = red; // linear grey

            double logSum = 0, peak = 0;
            for (var i = 0; i < source.Length; i += stride)
            {
                var l = Math.Max(0, Luminance(source[i + red], source[i + green], source[i + blue]) / max);
                if (!double.IsFinite(l))
                    l = 0;
                logSum += Math.Log(1e-6 + l);
                peak = Math.Max(peak, l);
            }

            var compress = peak > 1.0;
            var exposure = compress ? 0.18 / Math.Exp(logSum / Math.Max(1, count)) : 1.0;
            var white = Math.Max(1.0, peak * exposure);
            var white2 = white * white;
            var outStride = hasAlpha ? 4 : 3;
            var mapped = new QuantumType[count * outStride];
            for (int i = 0, o = 0; i < source.Length; i += stride, o += outStride)
            {
                double r = Math.Max(0, source[i + red] / max), g = Math.Max(0, source[i + green] / max), b = Math.Max(0, source[i + blue] / max);
                if (compress)
                {
                    var l = Luminance(r, g, b);
                    if (l > 0)
                    {
                        var scaled = l * exposure;
                        var k = scaled * (1 + scaled / white2) / (1 + scaled) / l;
                        r *= k;
                        g *= k;
                        b *= k;
                    }
                }
                mapped[o] = (float)(SrgbEncode(r) * max);
                mapped[o + 1] = (float)(SrgbEncode(g) * max);
                mapped[o + 2] = (float)(SrgbEncode(b) * max);
                if (hasAlpha)
                    mapped[o + 3] = alpha >= 0 ? Math.Clamp(source[i + alpha], 0, max) : max;
            }

            var result = new MagickImage();
            result.ReadPixels(mapped, new PixelReadSettings(linear.Width, linear.Height, StorageType.Quantum, hasAlpha ? PixelMapping.RGBA : PixelMapping.RGB));
            result.Depth = 16;
            result.Density = image.Density;
            return result;
        }
        finally
        {
            linear.Dispose();
        }
    }

    static int Index(IPixelCollection<QuantumType> pixels, PixelChannel channel) =>
        pixels.GetChannelIndex(channel) is { } index ? (int)index : -1;

    static double Luminance(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    static double SrgbEncode(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        return linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
    }
}
