namespace Clementine.Core;

/// <summary>
/// Extension → <see cref="FileKind"/> registry. Extensions are lower-case with a leading dot;
/// compound archive extensions such as ".tar.gz" are recognised as a single extension.
/// </summary>
public static class FileTypes
{
    public static readonly IReadOnlySet<string> Image = Set(
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff",
        ".bmp", ".dib", ".gif", ".ico", ".cur", ".svg", ".svgz", ".jxl", ".psd", ".psb", ".tga", ".dds", ".exr",
        ".hdr", ".jp2", ".j2k", ".qoi", ".pcx", ".ppm", ".pgm", ".pbm", ".xcf",
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef",
        ".srw", ".x3f", ".3fr", ".iiq", ".erf", ".mrw", ".kdc", ".dcr");

    public static readonly IReadOnlySet<string> Video = Set(
        ".mp4", ".m4v", ".mov", ".qt", ".mkv", ".avi", ".wmv", ".asf", ".webm", ".flv", ".f4v", ".mpg", ".mpeg",
        ".m2v", ".m2ts", ".mts", ".ts", ".3gp", ".3g2", ".ogv", ".mxf", ".vob", ".dv", ".y4m");

    public static readonly IReadOnlySet<string> Audio = Set(
        ".mp3", ".m4a", ".m4b", ".aac", ".wav", ".wave", ".flac", ".ogg", ".oga", ".opus", ".aiff", ".aif",
        ".aifc", ".wma", ".amr", ".ac3", ".eac3", ".dts", ".mka", ".caf", ".ape", ".wv", ".mp2", ".mpc", ".tta");

    public static readonly IReadOnlySet<string> Pdf = Set(".pdf");

    public static readonly IReadOnlySet<string> Document = Set(
        ".docx", ".docm", ".doc", ".dotx", ".dot", ".odt", ".ott", ".rtf", ".pages", ".wps", ".wpd", ".fodt");

    public static readonly IReadOnlySet<string> Spreadsheet = Set(
        ".xlsx", ".xlsm", ".xlsb", ".xls", ".ods", ".fods", ".csv", ".tsv", ".numbers");

    public static readonly IReadOnlySet<string> Presentation = Set(
        ".pptx", ".pptm", ".ppt", ".ppsx", ".pps", ".odp", ".fodp", ".key");

    public static readonly IReadOnlySet<string> Ebook = Set(".epub");

    public static readonly IReadOnlySet<string> Text = Set(
        ".txt", ".text", ".md", ".markdown", ".html", ".htm", ".xhtml", ".json", ".jsonl", ".ndjson", ".xml", ".yaml",
        ".yml", ".log");

    public static readonly IReadOnlySet<string> Subtitle = Set(".srt", ".vtt", ".ass", ".ssa", ".sbv");

    public static readonly IReadOnlySet<string> Archive = Set(
        ".zip", ".tar", ".gz", ".tgz", ".tar.gz", ".bz2", ".tbz", ".tbz2", ".tar.bz2", ".xz", ".txz", ".tar.xz",
        ".zst", ".tzst", ".tar.zst", ".7z", ".rar");

    static readonly string[] CompoundExtensions = [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"];

    static readonly (FileKind Kind, IReadOnlySet<string> Extensions)[] Families =
    [
        (FileKind.Image, Image),
        (FileKind.Video, Video),
        (FileKind.Audio, Audio),
        (FileKind.Pdf, Pdf),
        (FileKind.Document, Document),
        (FileKind.Spreadsheet, Spreadsheet),
        (FileKind.Presentation, Presentation),
        (FileKind.Ebook, Ebook),
        (FileKind.Text, Text),
        (FileKind.Subtitle, Subtitle),
        (FileKind.Archive, Archive),
    ];

    static readonly Dictionary<string, FileKind> KindByExtension = BuildKindMap();

    /// <summary>The <see cref="FileKind"/> for an extension such as ".png" (case-insensitive).</summary>
    public static FileKind KindOf(string extension) =>
        KindByExtension.TryGetValue(extension, out var kind) ? kind : FileKind.Unknown;

    /// <summary>All extensions registered for a kind.</summary>
    public static IReadOnlySet<string> ExtensionsOf(FileKind kind) =>
        Families.FirstOrDefault(f => f.Kind == kind).Extensions ?? Set();

    /// <summary>Lower-case extension with a leading dot ("" if none), recognising ".tar.gz" style extensions.</summary>
    public static string ExtensionOf(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        foreach (var compound in CompoundExtensions)
        {
            if (name.Length > compound.Length && name.EndsWith(compound, StringComparison.Ordinal))
                return compound;
        }
        return Path.GetExtension(name);
    }

    static Dictionary<string, FileKind> BuildKindMap()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, extensions) in Families)
        {
            foreach (var ext in extensions)
                map.TryAdd(ext, kind);
        }
        return map;
    }

    static IReadOnlySet<string> Set(params string[] extensions) =>
        new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
}
