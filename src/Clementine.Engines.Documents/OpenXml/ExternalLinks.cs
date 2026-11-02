using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>
/// Cuts an Office Open XML package's links to the outside world before LibreOffice opens it: linked pictures, linked
/// OLE objects and media, attached templates, external workbooks, frames and sub-documents. LibreOffice loads linked
/// pictures while it converts — from the web (a tracking pixel learns who opened the file), or from a network share,
/// which can hand the user's Windows credentials to that host. Clementine's own converters never fetch anything; with
/// this, neither does LibreOffice. Hyperlinks stay: they are only written into the result, never followed.
/// </summary>
/// <remarks>
/// It works on the relationship parts (*.rels) of the private copy LibreOffice is given, so it covers Word, Excel and
/// PowerPoint packages alike; the references to a removed relationship simply find nothing (as for a broken link).
/// </remarks>
internal static partial class ExternalLinks
{
    /// <summary>Real relationship parts are a few kilobytes; anything this big is dropped rather than parsed.</summary>
    const long MaxRelationshipBytes = 16L * 1024 * 1024;

    const string Declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n";

    /// <summary>
    /// Removes the outside links from the zip package at <paramref name="path"/>, in place, and returns how many went
    /// (0 for files that aren't zip packages, such as .doc, .rtf or .csv). Throws <see cref="InvalidDataException"/>
    /// when the file looks like a zip but can't be read as one.
    /// </summary>
    public static int Strip(string path, CancellationToken cancellationToken = default)
    {
        if (!IsZip(path))
            return 0;

        // Read first; the package is only rewritten when it has something to remove (most files don't).
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        var removed = 0;
        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    continue;
                cancellationToken.ThrowIfCancellationRequested();
                var (cleaned, count) = Clean(entry);
                if (count > 0)
                {
                    changes[entry.FullName] = cleaned;
                    removed += count;
                }
            }
        }
        if (changes.Count == 0)
            return 0;

        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var (name, xml) in changes)
        {
            foreach (var entry in archive.Entries.Where(e => e.FullName == name).ToList())
                entry.Delete();
            if (xml is null)
                continue;
            using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            stream.Write(new UTF8Encoding(false).GetBytes(xml));
        }
        return removed;
    }

    /// <summary>"PK" — a zip (OOXML and OpenDocument packages are zips).</summary>
    static bool IsZip(string path)
    {
        Span<byte> header = stackalloc byte[2];
        using var file = File.OpenRead(path);
        return file.Read(header) == 2 && header[0] == (byte)'P' && header[1] == (byte)'K';
    }

    /// <summary>
    /// The relationship part without its outside links, and how many there were. (null, n &gt; 0): drop the part — it
    /// can't be read safely, so LibreOffice gets no relationships for it rather than ones nobody checked.
    /// </summary>
    static (string? Xml, int Removed) Clean(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxRelationshipBytes)
            return (null, 1);
        XDocument document;
        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            return (null, 1);
        }
        var outside = document.Descendants().Where(e => e.Name.LocalName == "Relationship" && LeavesThePackage(e)).ToList();
        if (outside.Count == 0)
            return (null, 0);
        foreach (var relationship in outside)
            relationship.Remove();
        return (Declaration + document.Root!.ToString(SaveOptions.DisableFormatting), outside.Count);
    }

    /// <summary>
    /// An external target (TargetMode="External"), or one spelled as an absolute URL or network path whatever its mode —
    /// except hyperlinks.
    /// </summary>
    static bool LeavesThePackage(XElement relationship)
    {
        var type = ((string?)relationship.Attribute("Type"))?.Trim() ?? "";
        if (type.EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(((string?)relationship.Attribute("TargetMode"))?.Trim(), "External", StringComparison.OrdinalIgnoreCase))
            return true;
        var target = new string((((string?)relationship.Attribute("Target")) ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim().Replace('\\', '/');
        return target.StartsWith("//", StringComparison.Ordinal) || SchemePrefix().IsMatch(target);
    }

    /// <summary>"http:", "file:", "smb:"… (two or more letters, so a drive letter isn't a scheme).</summary>
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]+:")]
    private static partial Regex SchemePrefix();
}
