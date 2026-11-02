namespace Clementine.Engines.Documents.Common;

/// <summary>Extension families the Documents engine works with (lower-case, leading dot).</summary>
internal static class DocumentFormats
{
    public static readonly IReadOnlySet<string> Pdf = Set(".pdf");

    /// <summary>Word processing files Clementine reads natively with OpenXML.</summary>
    public static readonly IReadOnlySet<string> WordOpenXml = Set(".docx", ".docm", ".dotx");

    /// <summary>Word processing files only LibreOffice can read.</summary>
    public static readonly IReadOnlySet<string> WordLegacy = Set(".doc", ".dot", ".odt", ".ott", ".rtf", ".wps", ".wpd", ".fodt");

    public static readonly IReadOnlySet<string> Word = Union(WordOpenXml, WordLegacy);

    public static readonly IReadOnlySet<string> SheetOpenXml = Set(".xlsx", ".xlsm");

    public static readonly IReadOnlySet<string> SheetLegacy = Set(".xls", ".xlsb", ".ods", ".fods");

    public static readonly IReadOnlySet<string> Sheet = Union(SheetOpenXml, SheetLegacy);

    public static readonly IReadOnlySet<string> DelimitedText = Set(".csv", ".tsv");

    public static readonly IReadOnlySet<string> SlidesOpenXml = Set(".pptx", ".pptm", ".ppsx");

    public static readonly IReadOnlySet<string> SlidesLegacy = Set(".ppt", ".pps", ".odp", ".fodp");

    public static readonly IReadOnlySet<string> Slides = Union(SlidesOpenXml, SlidesLegacy);

    public static readonly IReadOnlySet<string> PlainText = Set(".txt", ".text", ".log");

    public static readonly IReadOnlySet<string> Markdown = Set(".md", ".markdown");

    public static readonly IReadOnlySet<string> Html = Set(".html", ".htm", ".xhtml");

    public static readonly IReadOnlySet<string> Epub = Set(".epub");

    public static readonly IReadOnlySet<string> Apple = Set(".pages", ".numbers", ".key");

    public static IReadOnlySet<string> Set(params string[] extensions) =>
        new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Union(params IReadOnlySet<string>[] sets) =>
        new HashSet<string>(sets.SelectMany(s => s), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Except(IReadOnlySet<string> set, params string[] remove) =>
        new HashSet<string>(set.Where(e => !remove.Contains(e, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
}
