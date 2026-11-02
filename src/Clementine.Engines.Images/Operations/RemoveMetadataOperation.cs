using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Metadata;

namespace Clementine.Engines.Images.Operations;

public enum MetadataScope
{
    All,
    Location,
}

/// <summary>
/// Remove metadata: All · Location only. JPEG and WebP are edited losslessly (the compressed image data is copied
/// byte for byte); other formats are re-saved without the metadata. The colour profile is always kept, and EXIF
/// orientation is applied to the pixels first so nothing ends up sideways.
/// </summary>
public sealed class RemoveMetadataOperation : ImageOperation
{
    const long LosslessLimit = 512L * 1024 * 1024;

    readonly MetadataScope _scope;

    public RemoveMetadataOperation(MetadataScope scope)
        : base(new OperationInfo
        {
            Id = scope == MetadataScope.All ? "image.metadata.remove" : "image.metadata.location",
            Category = OperationCategory.Tool,
            Group = "Metadata",
            Variant = scope == MetadataScope.All ? "Remove all" : "Location only",
            Title = scope == MetadataScope.All ? "Remove all metadata" : "Remove location",
            Description = scope == MetadataScope.All
                ? "Removes EXIF, GPS, XMP, IPTC, comments and thumbnails; keeps the colour profile. JPEG/WebP stay lossless."
                : "Removes only the GPS location; camera details, dates and ratings stay.",
            IconKey = "metadata",
            Order = 80 + (int)scope,
        }) => _scope = scope;

    public static IEnumerable<IOperation> All() => [new RemoveMetadataOperation(MetadataScope.All), new RemoveMetadataOperation(MetadataScope.Location)];

    string Suffix => _scope == MetadataScope.All ? "no metadata" : "no location";

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var input = request.Input;
        Report(progress, 0.1, "Reading…");
        if (input.Length <= LosslessLimit && TryLossless(request, cancellationToken) is { } lossless)
            return OperationResult.FromOutputs(lossless);

        using var image = ImageLoader.Load(input, LoadOptions.Animated, cancellationToken);
        var changed = false;
        foreach (var frame in image.Frames)
            changed |= _scope == MetadataScope.All ? StripAll(frame) : StripLocation(frame);
        if (!changed && _scope == MetadataScope.Location)
            throw new OperationException($"“{input.Name}” has no location data.");
        if (!changed && image.OriginalOrientation is OrientationType.Undefined or OrientationType.TopLeft)
            throw new OperationException($"“{input.Name}” has no metadata to remove.");

        Report(progress, 0.6, "Saving…");
        var output = ImageFormats.ToolOutputFor(input, image.HasTransparency);
        if (output.IsFallback)
            image.AddWarning($"Saved as {output.Name} because Clementine can't write {input.Extension} files.");
        var path = SaveNextToInput(request, image, output.Format, output.Extension, Suffix, PreservingQuality(image, output), cancellationToken);
        return Result(path, image);
    }

    /// <summary>Byte-level editing for JPEG/WebP; null when the file needs re-encoding (e.g. it must be turned upright).</summary>
    string? TryLossless(OperationRequest request, CancellationToken cancellationToken)
    {
        var input = request.Input;
        var bytes = File.ReadAllBytes(input.Path);
        var jpeg = JpegSegments.IsJpeg(bytes);
        var webp = !jpeg && WebPChunks.IsWebP(bytes);
        if (!jpeg && !webp)
            return null;

        // Read EXIF/XMP straight from the container (in WebP they follow the image data, out of a header ping's reach).
        var exifPayload = jpeg ? JpegSegments.Find(bytes, 0xE1, "Exif\0"u8).FirstOrDefault() : WebPChunks.Find(bytes, "EXIF");
        var xmpPayload = jpeg ? JpegSegments.Find(bytes, 0xE1, "http://ns.adobe.com/xap/1.0/\0"u8).FirstOrDefault() : WebPChunks.Find(bytes, "XMP ");
        var exif = ParseExif(exifPayload);
        var orientation = exif?.GetValue(ExifTag.Orientation)?.Value ?? 1;
        if (_scope == MetadataScope.All && orientation is not (0 or 1))
            return null; // the orientation lives in EXIF: rotate the pixels instead (re-encode)

        byte[]? cleaned;
        if (_scope == MetadataScope.Location)
        {
            var xmpHasGps = xmpPayload is not null && System.Text.Encoding.UTF8.GetString(xmpPayload).Contains("exif:GPS", StringComparison.Ordinal);
            if (!LocationScrubber.HasGps(exif) && !xmpHasGps)
                throw new OperationException($"“{input.Name}” has no location data.");
            cleaned = jpeg
                ? JpegSegments.RemoveLocation(bytes, LocationScrubber.RemoveGpsFromExif, LocationScrubber.RemoveGpsFromXmp)
                : WebPChunks.Rewrite(bytes, (chunk, data) => chunk == "EXIF" ? LocationScrubber.RemoveGpsFromExif(data) : LocationScrubber.RemoveGpsFromXmp(data));
        }
        else
        {
            cleaned = jpeg ? JpegSegments.StripAll(bytes) : WebPChunks.StripAll(bytes);
            if (cleaned is not null && cleaned.Length == bytes.Length)
                throw new OperationException($"“{input.Name}” has no metadata to remove.");
        }
        if (cleaned is null)
            return null; // unusual structure: fall back to re-encoding
        return SaveBytesNextToInput(request, cleaned, input.Extension, Suffix, cancellationToken);
    }

    static ExifProfile? ParseExif(byte[]? payload)
    {
        if (payload is not { Length: > 8 })
            return null;
        try
        {
            return new ExifProfile(payload.AsSpan().StartsWith("Exif\0\0"u8) ? payload : [.. "Exif\0\0"u8, .. payload]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Everything but the ICC profile.</summary>
    static bool StripAll(IMagickImage<QuantumType> frame)
    {
        var hadMetadata = frame.ProfileNames.Any(n => n != "icc") || !string.IsNullOrEmpty(frame.Comment);
        var icc = frame.GetColorProfile();
        frame.Strip();
        if (icc is not null)
            frame.SetProfile(icc);
        return hadMetadata;
    }

    static bool StripLocation(IMagickImage<QuantumType> frame)
    {
        var changed = false;
        if (frame.GetExifProfile() is { } exif && LocationScrubber.RemoveGps(exif))
        {
            frame.SetProfile(exif);
            changed = true;
        }
        if (frame.GetXmpProfile() is { } xmp && xmp.ToByteArray() is { } data)
        {
            var cleaned = LocationScrubber.RemoveGpsFromXmp(data);
            if (cleaned is null)
            {
                frame.RemoveProfile("xmp");
                changed = true;
            }
            else if (!ReferenceEquals(cleaned, data))
            {
                frame.SetProfile(new XmpProfile(cleaned));
                changed = true;
            }
        }
        return changed;
    }
}
