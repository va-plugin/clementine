using System.IO.Compression;
using System.Xml.Linq;

namespace Clementine.Engines.Documents.Ebook;

/// <summary>A manifest entry of an EPUB: its path inside the zip and its media type.</summary>
internal sealed record EpubItem(string Id, string Path, string MediaType);

/// <summary>
/// Minimal EPUB 2/3 reader: container.xml → package document → manifest, spine (reading order) and metadata.
/// The whole book is read into memory once; entries are looked up by their normalised zip path.
/// </summary>
internal sealed class EpubBook
{
    /// <summary>The most a book may unpack to, counted as it is actually decompressed (a zip's declared sizes can lie).</summary>
    internal static long MaxBookBytes { get; set; } = 500L * 1024 * 1024;

    static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    readonly Dictionary<string, byte[]> _entries;

    EpubBook(Dictionary<string, byte[]> entries, string title, string? author, string? language, IReadOnlyList<EpubItem> spine, IReadOnlyList<EpubItem> manifest)
    {
        _entries = entries;
        Title = title;
        Author = author;
        Language = language;
        Spine = spine;
        Manifest = manifest;
    }

    public string Title { get; }

    public string? Author { get; }

    public string? Language { get; }

    /// <summary>Content documents in reading order (linear items only).</summary>
    public IReadOnlyList<EpubItem> Spine { get; }

    public IReadOnlyList<EpubItem> Manifest { get; }

    public static EpubBook Open(string path, CancellationToken cancellationToken)
    {
        var name = System.IO.Path.GetFileName(path);
        Dictionary<string, byte[]> entries;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            // The declared sizes give a quick no for honest big books; the real check counts what each entry inflates
            // to while it is read, because a crafted zip can claim 100 bytes and inflate to gigabytes.
            var limit = MaxBookBytes;
            if (zip.Entries.Sum(e => e.Length) > limit)
                throw TooLarge();
            entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            var chunk = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream((int)Math.Min(entry.Length, Math.Min(limit - total, 16L * 1024 * 1024)));
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > limit)
                        throw TooLarge();
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length % (8L * 1024 * 1024) < read)
                        cancellationToken.ThrowIfCancellationRequested();
                }
                entries[Normalize(entry.FullName)] = buffer.ToArray();
            }
        }
        catch (InvalidDataException ex)
        {
            throw new OperationException($"\"{name}\" isn't a valid EPUB (it's not a readable zip file).", ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new OperationException($"Couldn't read \"{name}\" — it may be open in another program.", ex.Message, ex);
        }

        var packagePath = FindPackagePath(entries)
            ?? throw new OperationException($"\"{name}\" isn't a valid EPUB (no package document).");
        XDocument package;
        try
        {
            package = XDocument.Load(new MemoryStream(entries[packagePath]));
        }
        catch (System.Xml.XmlException ex)
        {
            throw new OperationException($"\"{name}\" has a damaged package document.", ex.Message, ex);
        }

        var root = package.Root!;
        var packageDirectory = DirectoryOf(packagePath);
        var manifest = root.Element(Opf + "manifest")?.Elements(Opf + "item")
            .Select(item => new EpubItem(
                (string?)item.Attribute("id") ?? "",
                Resolve(packageDirectory, (string?)item.Attribute("href") ?? ""),
                (string?)item.Attribute("media-type") ?? ""))
            .Where(i => i.Id.Length > 0)
            .ToList() ?? [];
        var byId = manifest.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());

        var spine = root.Element(Opf + "spine")?.Elements(Opf + "itemref")
            .Where(r => !string.Equals((string?)r.Attribute("linear"), "no", StringComparison.OrdinalIgnoreCase))
            .Select(r => byId.GetValueOrDefault((string?)r.Attribute("idref") ?? ""))
            .OfType<EpubItem>()
            .Where(i => entries.ContainsKey(i.Path))
            .ToList() ?? [];
        if (spine.Count == 0)
            throw new OperationException($"\"{name}\" has no readable chapters.");

        var metadata = root.Element(Opf + "metadata");
        var title = metadata?.Element(Dc + "title")?.Value.Trim();
        var author = metadata?.Elements(Dc + "creator").Select(c => c.Value.Trim()).FirstOrDefault(c => c.Length > 0);
        var language = metadata?.Element(Dc + "language")?.Value.Trim();

        var book = new EpubBook(entries, string.IsNullOrWhiteSpace(title) ? System.IO.Path.GetFileNameWithoutExtension(path) : title,
            author, language, spine, manifest);
        book.RejectDrm(name);
        return book;
    }

    static OperationException TooLarge() => new("This e-book is too large to convert.");

    public bool TryGet(string path, out byte[] data) => _entries.TryGetValue(Normalize(path), out data!);

    public string MediaTypeOf(string path) =>
        Manifest.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))?.MediaType
        ?? GuessMediaType(path);

    /// <summary>Resolves an href found in <paramref name="fromPath"/> to a zip path (fragment and query removed).</summary>
    public static string Resolve(string fromDirectory, string href)
    {
        var clean = href.Split('#', 2)[0].Split('?', 2)[0];
        clean = Uri.UnescapeDataString(clean).Replace('\\', '/');
        var parts = new List<string>();
        foreach (var segment in (fromDirectory.Length > 0 && !clean.StartsWith('/') ? fromDirectory + "/" + clean : clean.TrimStart('/')).Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return string.Join('/', parts);
    }

    public static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    static string? FindPackagePath(Dictionary<string, byte[]> entries)
    {
        if (entries.TryGetValue("META-INF/container.xml", out var container))
        {
            try
            {
                var doc = XDocument.Load(new MemoryStream(container));
                var full = doc.Descendants(Container + "rootfile").Select(r => (string?)r.Attribute("full-path")).FirstOrDefault(p => p is not null);
                if (full is not null && entries.ContainsKey(Normalize(full)))
                    return Normalize(full);
            }
            catch (System.Xml.XmlException)
            {
            }
        }
        return entries.Keys.FirstOrDefault(k => k.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
    }

    void RejectDrm(string name)
    {
        if (!_entries.TryGetValue("META-INF/encryption.xml", out var encryption))
            return;
        try
        {
            var doc = XDocument.Load(new MemoryStream(encryption));
            XNamespace enc = "http://www.w3.org/2001/04/xmlenc#";
            var encrypted = doc.Descendants(enc + "EncryptedData")
                .Where(d => d.Element(enc + "EncryptionMethod")?.Attribute("Algorithm")?.Value is not ("http://www.idpf.org/2008/embedding" or "http://ns.adobe.com/pdf/enc#RC"))
                .Select(d => d.Descendants(enc + "CipherReference").Select(c => (string?)c.Attribute("URI")).FirstOrDefault())
                .OfType<string>()
                .Select(uri => Resolve("", uri))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Spine.Any(item => encrypted.Contains(item.Path)))
                throw new OperationException($"\"{name}\" is DRM-protected, so Clementine can't read it.");
        }
        catch (System.Xml.XmlException)
        {
        }
    }

    static string GuessMediaType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".css" => "text/css",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
