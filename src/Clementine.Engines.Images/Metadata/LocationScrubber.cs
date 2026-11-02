using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Clementine.Engines.Images.Metadata;

/// <summary>Removes GPS location from EXIF and XMP payloads, keeping every other tag.</summary>
public static class LocationScrubber
{
    static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    static readonly XNamespace ExifNamespace = "http://ns.adobe.com/exif/1.0/";

    /// <summary>True when the EXIF profile carries any GPS tag.</summary>
    public static bool HasGps(IExifProfile? exif) => exif?.Values.Any(v => IsGpsTag(v.Tag)) == true;

    /// <summary>Removes every GPS tag from <paramref name="exif"/> in place; returns whether anything changed.</summary>
    public static bool RemoveGps(IExifProfile exif)
    {
        var tags = exif.Values.Where(v => IsGpsTag(v.Tag)).Select(v => v.Tag).ToList();
        foreach (var tag in tags)
            exif.RemoveValue(tag);
        return tags.Count > 0;
    }

    /// <summary>EXIF bytes (with or without the "Exif\0\0" header) → the same without GPS, in the same framing.</summary>
    public static byte[]? RemoveGpsFromExif(byte[] payload)
    {
        var hasHeader = payload.AsSpan().StartsWith(ExifHeader);
        var profile = new ExifProfile(hasHeader ? payload : [.. ExifHeader, .. payload]);
        if (!RemoveGps(profile))
            return payload;
        var bytes = profile.ToByteArray();
        if (bytes is null || bytes.Length <= ExifHeader.Length)
            return null;
        var withHeader = bytes.AsSpan().StartsWith(ExifHeader) ? bytes : [.. ExifHeader, .. bytes];
        return hasHeader ? withHeader : withHeader[ExifHeader.Length..];
    }

    /// <summary>XMP packet (with or without the JPEG namespace header) → the same without exif:GPS* properties. Null drops it.</summary>
    public static byte[]? RemoveGpsFromXmp(byte[] payload)
    {
        var hasHeader = payload.AsSpan().StartsWith(XmpHeader);
        var xml = Encoding.UTF8.GetString(hasHeader ? payload.AsSpan(XmpHeader.Length) : payload).TrimEnd('\0');
        if (!xml.Contains("GPS", StringComparison.Ordinal))
            return payload;
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            RemoveGps(document);
            var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
            using var buffer = new MemoryStream();
            using (var writer = XmlWriter.Create(buffer, settings))
                document.Save(writer);
            var cleaned = buffer.ToArray();
            return hasHeader ? [.. XmpHeader, .. cleaned] : cleaned;
        }
        catch (XmlException)
        {
            return null; // unparseable XMP that mentions GPS: drop it rather than leak the location
        }
    }

    /// <summary>Removes exif:GPS* attributes and elements anywhere in an XMP document.</summary>
    public static void RemoveGps(XDocument document)
    {
        foreach (var attribute in document.Descendants().Attributes().Where(IsGps).ToList())
            attribute.Remove();
        foreach (var element in document.Descendants().Where(e => e.Name.Namespace == ExifNamespace && e.Name.LocalName.StartsWith("GPS", StringComparison.Ordinal)).ToList())
            element.Remove();
    }

    static bool IsGps(XAttribute attribute) =>
        attribute.Name.Namespace == ExifNamespace && attribute.Name.LocalName.StartsWith("GPS", StringComparison.Ordinal);

    static bool IsGpsTag(ExifTag tag) => tag.ToString().StartsWith("GPS", StringComparison.Ordinal);
}
