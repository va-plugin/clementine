using System.Text;
using Clementine.Core;

namespace Clementine.Engines.Files.Text;

/// <summary>Friendly names for code pages and the list offered by "Choose encoding…".</summary>
internal static class EncodingCatalog
{
    sealed record Known(int CodePage, string Value, string Name);

    static readonly Known[] All =
    [
        new(1252, "windows-1252", "Windows-1252 (Western European)"),
        new(1250, "windows-1250", "Windows-1250 (Central European)"),
        new(1251, "windows-1251", "Windows-1251 (Cyrillic)"),
        new(1253, "windows-1253", "Windows-1253 (Greek)"),
        new(1254, "windows-1254", "Windows-1254 (Turkish)"),
        new(1255, "windows-1255", "Windows-1255 (Hebrew)"),
        new(1256, "windows-1256", "Windows-1256 (Arabic)"),
        new(1257, "windows-1257", "Windows-1257 (Baltic)"),
        new(1258, "windows-1258", "Windows-1258 (Vietnamese)"),
        new(874, "windows-874", "Windows-874 (Thai)"),
        new(28591, "iso-8859-1", "ISO-8859-1 (Latin-1)"),
        new(28605, "iso-8859-15", "ISO-8859-15 (Latin-9)"),
        new(28592, "iso-8859-2", "ISO-8859-2 (Latin-2)"),
        new(20866, "koi8-r", "KOI8-R (Russian)"),
        new(21866, "koi8-u", "KOI8-U (Ukrainian)"),
        new(866, "ibm866", "DOS 866 (Cyrillic)"),
        new(850, "ibm850", "DOS 850 (Western European)"),
        new(437, "ibm437", "DOS 437 (US)"),
        new(932, "shift_jis", "Shift-JIS (Japanese)"),
        new(51932, "euc-jp", "EUC-JP (Japanese)"),
        new(936, "gbk", "GBK (Simplified Chinese)"),
        new(54936, "gb18030", "GB18030 (Chinese)"),
        new(950, "big5", "Big5 (Traditional Chinese)"),
        new(949, "ks_c_5601-1987", "EUC-KR (Korean)"),
        new(10000, "macintosh", "Mac Roman"),
        new(1200, "utf-16", "UTF-16 LE"),
        new(1201, "utf-16BE", "UTF-16 BE"),
        new(12000, "utf-32", "UTF-32 LE"),
        new(12001, "utf-32BE", "UTF-32 BE"),
        new(65001, "utf-8", "UTF-8"),
    ];

    /// <summary>Choices for the "source encoding" option (manual override when auto-detection guesses wrong).</summary>
    public static IReadOnlyList<OptionChoice> Choices { get; } =
        All.Where(k => k.CodePage != 65001).Select(k => new OptionChoice(k.Value, k.Name)).ToList();

    public static string DisplayName(int codePage)
    {
        var known = All.FirstOrDefault(k => k.CodePage == codePage);
        if (known is not null)
            return known.Name;
        TextDecoder.EnsureCodePages();
        try
        {
            return Encoding.GetEncoding(codePage).EncodingName;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return $"code page {codePage}";
        }
    }

    /// <summary>Resolves an option value ("windows-1251") to an encoding, with a friendly error when unknown.</summary>
    public static Encoding Resolve(string value)
    {
        TextDecoder.EnsureCodePages();
        try
        {
            return Encoding.GetEncoding(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new OperationException($"\"{value}\" isn't an encoding Clementine knows.");
        }
    }
}
