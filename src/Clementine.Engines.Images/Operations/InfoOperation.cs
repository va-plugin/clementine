using System.Globalization;
using System.Text;
using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Psd;

namespace Clementine.Engines.Images.Operations;

/// <summary>Info: a text report (format, size, colour, depth, DPI, frames/layers, ICC, key EXIF and GPS).</summary>
public sealed class InfoOperation : ImageOperation
{
    public InfoOperation()
        : base(new OperationInfo
        {
            Id = "image.info",
            Category = OperationCategory.Tool,
            Group = "Info",
            Title = "Image info",
            Description = "Format, dimensions, colour, resolution, layers, camera and location details.",
            IconKey = "info",
            Order = 90,
        })
    {
    }

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        Report(progress, null, "Reading…");
        var report = Build(request.Input, cancellationToken);
        return OperationResult.FromText($"{request.Input.Name} — image info", report);
    }

    /// <summary>The report text for <paramref name="input"/>.</summary>
    public static string Build(InputFile input, CancellationToken cancellationToken = default)
    {
        MagickRuntime.EnsureInitialized();
        var settings = ImageLoader.ReadSettings(input, LoadOptions.Still, firstFrameOnly: false);
        using var frames = new MagickImageCollection();
        try
        {
            frames.Ping(input.Path, settings);
        }
        catch (MagickException ex)
        {
            throw MagickErrors.Reading(ex, input);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (frames.Count == 0)
            throw new OperationException("This file doesn't contain an image.");

        var image = frames[0];
        var report = new ReportBuilder();
        report.Line("File", $"{input.Name} ({FormatBytes(input.Length)})");
        var formatInfo = MagickNET.SupportedFormats.FirstOrDefault(f => f.Format == image.Format);
        report.Line("Format", formatInfo is null ? image.Format.ToString() : $"{formatInfo.Description} ({image.Format})");

        var rotated = image.Orientation is OrientationType.LeftTop or OrientationType.RightTop or OrientationType.RightBottom or OrientationType.LeftBottom;
        var (w, h) = rotated ? (image.Height, image.Width) : (image.Width, image.Height);
        var megapixels = w * (double)h / 1_000_000;
        report.Line("Dimensions", megapixels >= 0.1 ? $"{w} × {h} px ({megapixels:0.#} megapixels)" : $"{w} × {h} px");
        if (image.Orientation is not (OrientationType.Undefined or OrientationType.TopLeft))
            report.Line("Orientation", $"{Describe(image.Orientation)} (from EXIF)");

        var transparency = Transparency(input, image, cancellationToken);
        report.Line("Colour", $"{ColorName(image.ColorSpace)}, {image.Depth}-bit per channel, {transparency}");
        var icc = image.GetColorProfile();
        report.Line("Colour profile", icc is null ? "none (treated as sRGB)" : string.IsNullOrWhiteSpace(icc.Description) ? "embedded" : icc.Description.Trim());
        var density = image.Density;
        if (density.X > 0)
        {
            var dpi = density.Units == DensityUnit.PixelsPerCentimeter ? density.X * 2.54 : density.X;
            var dpiY = density.Units == DensityUnit.PixelsPerCentimeter ? density.Y * 2.54 : density.Y;
            report.Line("Resolution", density.Units == DensityUnit.Undefined
                ? $"{dpi:0.#} × {dpiY:0.#} (no unit)"
                : $"{dpi:0.#} × {dpiY:0.#} DPI");
        }
        if (image.Format == MagickFormat.Jpeg && image.Quality is > 0 and <= 100)
            report.Line("JPEG quality", $"about {image.Quality}");

        if (input.HasExtension(ImageFormats.Psd))
            DescribeLayers(input, report);
        else if (frames.Count > 1)
            DescribeFrames(input, frames, report);

        DescribeExif(image.GetExifProfile(), report);
        var kinds = new List<string>();
        if (image.HasProfile("exif"))
            kinds.Add("EXIF");
        if (image.HasProfile("xmp"))
            kinds.Add("XMP");
        if (image.HasProfile("iptc") || image.HasProfile("8bim"))
            kinds.Add("IPTC/Photoshop");
        if (icc is not null)
            kinds.Add("ICC");
        if (!string.IsNullOrWhiteSpace(image.Comment))
            kinds.Add("comment");
        report.Line("Metadata", kinds.Count == 0 ? "none" : string.Join(", ", kinds));
        return report.ToString();
    }

    /// <summary>
    /// An alpha channel alone doesn't make an image see-through (a PSD's fully opaque alpha, an RGBA PNG of a photo),
    /// so the pixels the conversions would use (the PSD composite, every animation frame) are decoded and checked.
    /// </summary>
    static string Transparency(InputFile input, IMagickImage<QuantumType> header, CancellationToken cancellationToken)
    {
        const string Unknown = "with an alpha channel";
        if (!header.HasAlpha)
            return "no transparency";
        if ((long)header.Width * header.Height > MagickRuntime.MaxPixels)
            return Unknown;
        try
        {
            using var image = ImageLoader.Load(input, new LoadOptions { KeepAnimation = true, Orient = false, ToDisplayColor = false }, cancellationToken);
            return image.HasTransparency ? "with transparency" : "no transparency";
        }
        catch (OperationException)
        {
            return Unknown; // the rest of the report (from the header) is still worth having
        }
    }

    static void DescribeLayers(InputFile input, ReportBuilder report)
    {
        var document = PsdDocument.TryRead(input.Path);
        if (document is null)
            return;
        var mode = document.ColorMode switch
        {
            PsdColorMode.Rgb => "RGB",
            PsdColorMode.Cmyk => "CMYK",
            PsdColorMode.Grayscale => "Greyscale",
            PsdColorMode.Lab => "Lab",
            PsdColorMode.Indexed => "Indexed colour",
            PsdColorMode.Bitmap => "Bitmap",
            PsdColorMode.Duotone => "Duotone",
            _ => "Multichannel",
        };
        report.Line("Photoshop", $"{(document.IsLargeDocument ? "PSB" : "PSD")}, {mode} mode, {document.Depth}-bit, {document.Channels} channels");
        if (document.HasRealMergedData == false)
            report.Line("Composite", "missing (saved without “Maximize compatibility”) — Clementine rebuilds it from the layers");
        var layers = document.Layers.Where(l => l.Section == PsdSectionType.Layer).ToList();
        var groups = document.Layers.Count(l => l.IsGroupStart);
        report.Line("Layers", layers.Count == 0 ? "none (flat image)" : $"{layers.Count}{(groups > 0 ? $" in {groups} group{(groups == 1 ? "" : "s")}" : "")}");
        foreach (var layer in Enumerable.Reverse(layers).Take(40))
        {
            var notes = new List<string>();
            if (layer.Hidden)
                notes.Add("hidden");
            if (layer.Opacity < 255)
                notes.Add($"{Math.Round(layer.Opacity / 2.55)} % opacity");
            if (layer.BlendKey is not ("norm" or "pass"))
                notes.Add(PsdLayers.BlendModeFor(layer.BlendKey).ToString());
            if (layer.Clipping)
                notes.Add("clipped");
            if (!layer.HasArea)
                notes.Add("no pixels");
            report.Item($"{layer.Name}{(notes.Count > 0 ? $" ({string.Join(", ", notes)})" : "")}");
        }
        if (layers.Count > 40)
            report.Item($"… and {layers.Count - 40} more");
    }

    static void DescribeFrames(InputFile input, MagickImageCollection frames, ReportBuilder report)
    {
        if (input.HasExtension(ImageFormats.MaybeAnimated))
        {
            var ticks = frames.Sum(f => (double)f.AnimationDelay / Math.Max(1, f.AnimationTicksPerSecond));
            var loops = frames[0].AnimationIterations == 0 ? "loops forever" : $"plays {frames[0].AnimationIterations}×";
            report.Line("Animation", $"{frames.Count} frames, {ticks:0.##} s, {loops}");
        }
        else if (input.HasExtension(ImageFormats.Icon))
        {
            report.Line("Sizes", string.Join(", ", frames.Select(f => $"{f.Width}×{f.Height}").Distinct()));
        }
        else
        {
            report.Line("Pages / frames", frames.Count.ToString(CultureInfo.InvariantCulture));
        }
    }

    static void DescribeExif(IExifProfile? exif, ReportBuilder report)
    {
        if (exif is null)
            return;
        var make = exif.GetValue(ExifTag.Make)?.Value?.Trim();
        var model = exif.GetValue(ExifTag.Model)?.Value?.Trim();
        if (!string.IsNullOrEmpty(make) || !string.IsNullOrEmpty(model))
        {
            var camera = model is not null && make is not null && model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}".Trim();
            report.Line("Camera", camera);
        }
        if (exif.GetValue(ExifTag.LensModel)?.Value is { Length: > 0 } lens)
            report.Line("Lens", lens.Trim());
        if ((exif.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif.GetValue(ExifTag.DateTime)?.Value) is { Length: > 0 } taken)
            report.Line("Taken", taken.Trim());

        var exposure = new List<string>();
        if (exif.GetValue(ExifTag.ExposureTime)?.Value is { } time && time.Denominator != 0)
            exposure.Add(time.ToDouble() >= 1 ? $"{time.ToDouble():0.#} s" : $"1/{Math.Round(1 / time.ToDouble())} s");
        if (exif.GetValue(ExifTag.FNumber)?.Value is { } f && f.Denominator != 0)
            exposure.Add($"f/{f.ToDouble():0.#}");
        if (exif.GetValue(ExifTag.ISOSpeedRatings)?.Value is { Length: > 0 } iso)
            exposure.Add($"ISO {iso[0]}");
        if (exif.GetValue(ExifTag.FocalLength)?.Value is { } focal && focal.Denominator != 0)
            exposure.Add($"{focal.ToDouble():0.#} mm");
        if (exposure.Count > 0)
            report.Line("Exposure", string.Join(" · ", exposure));

        if (Coordinate(exif.GetValue(ExifTag.GPSLatitude)?.Value, exif.GetValue(ExifTag.GPSLatitudeRef)?.Value) is { } lat
            && Coordinate(exif.GetValue(ExifTag.GPSLongitude)?.Value, exif.GetValue(ExifTag.GPSLongitudeRef)?.Value) is { } lon)
        {
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{Math.Abs(lat):0.000000}° {(lat < 0 ? "S" : "N")}, {Math.Abs(lon):0.000000}° {(lon < 0 ? "W" : "E")}");
            if (exif.GetValue(ExifTag.GPSAltitude)?.Value is { } altitude && altitude.Denominator != 0)
                text += string.Create(CultureInfo.InvariantCulture, $" (altitude {altitude.ToDouble():0} m)");
            report.Line("Location", text);
        }
        if (exif.GetValue(ExifTag.Software)?.Value is { Length: > 0 } software)
            report.Line("Software", software.Trim());
        if (exif.GetValue(ExifTag.Artist)?.Value is { Length: > 0 } artist)
            report.Line("Artist", artist.Trim());
        if (exif.GetValue(ExifTag.Copyright)?.Value is { Length: > 0 } copyright)
            report.Line("Copyright", copyright.Trim());
    }

    static double? Coordinate(Rational[]? parts, string? reference)
    {
        if (parts is not { Length: 3 } || parts.Any(p => p.Denominator == 0))
            return null;
        var value = parts[0].ToDouble() + parts[1].ToDouble() / 60 + parts[2].ToDouble() / 3600;
        return reference?.Trim() is "S" or "W" ? -value : value;
    }

    static string ColorName(ColorSpace colorSpace) => colorSpace switch
    {
        ColorSpace.sRGB => "RGB (sRGB)",
        ColorSpace.RGB => "linear RGB",
        ColorSpace.Gray or ColorSpace.LinearGray => "greyscale",
        ColorSpace.CMYK => "CMYK",
        ColorSpace.Lab => "Lab",
        _ => colorSpace.ToString(),
    };

    static string Describe(OrientationType orientation) => orientation switch
    {
        OrientationType.TopRight => "mirrored horizontally",
        OrientationType.BottomRight => "rotated 180°",
        OrientationType.BottomLeft => "mirrored vertically",
        OrientationType.LeftTop => "mirrored and rotated 90°",
        OrientationType.RightTop => "rotated 90° clockwise",
        OrientationType.RightBottom => "mirrored and rotated 90° anticlockwise",
        OrientationType.LeftBottom => "rotated 90° anticlockwise",
        _ => "upright",
    };

    static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };

    sealed class ReportBuilder
    {
        readonly StringBuilder _text = new();

        public void Line(string label, string value) => _text.Append(label).Append(": ").AppendLine(value);

        public void Item(string value) => _text.Append("  • ").AppendLine(value);

        public override string ToString() => _text.ToString().TrimEnd();
    }
}
