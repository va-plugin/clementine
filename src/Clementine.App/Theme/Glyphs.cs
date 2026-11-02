using Clementine.Core;

namespace Clementine.App.Theme;

/// <summary>
/// Segoe Fluent Icons code points used by the shell. Every glyph here was checked against the font
/// (see <c>GlyphTests</c>), so none renders as a missing-glyph box.
/// </summary>
public static class Glyphs
{
    public const string Settings = "";
    public const string Close = "";
    public const string Cancel = "";
    public const string Add = "";
    public const string More = "";
    public const string ChevronDown = "";
    public const string ChevronRight = "";
    public const string Folder = "";
    public const string FolderOpen = "";
    public const string OpenFile = "";
    public const string OpenExternal = "";
    public const string Copy = "";
    public const string Info = "";
    public const string Warning = "";
    public const string Error = "";
    public const string Check = "";
    public const string Accept = "";
    public const string Pin = "";
    public const string Delete = "";
    public const string History = "";
    public const string Help = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Power = "";
    public const string Document = "";
    public const string Refresh = "";
    public const string Download = "";
    public const string Keyboard = "";
    public const string Shield = "";
    public const string Speed = "";
    public const string Tools = "";
    public const string Wheel = "";
    public const string Sliders = "";
    public const string Explorer = "";
    public const string Heart = "";
    public const string Text = "";

    static readonly Dictionary<string, string> ByIconKey = new(StringComparer.Ordinal)
    {
        ["video"] = "",
        ["audio"] = "",
        ["image"] = "",
        ["pdf"] = "",
        ["document"] = "",
        ["spreadsheet"] = "",
        ["presentation"] = "",
        ["ebook"] = "",
        ["text"] = "",
        ["code"] = "",
        ["subtitle"] = "",
        ["archive"] = "",
        ["folder"] = "",
        ["extract"] = "",
        ["compress"] = "",
        ["resize"] = "",
        ["crop"] = "",
        ["rotate"] = "",
        ["flip"] = "",
        ["trim"] = "",
        ["split"] = "",
        ["join"] = "",
        ["merge"] = "",
        ["speed"] = "",
        ["snapshot"] = "",
        ["frames"] = "",
        ["mute"] = "",
        ["volume"] = "",
        ["normalize"] = "",
        ["channels"] = "",
        ["bleep"] = "",
        ["fade"] = "",
        ["reverse"] = "",
        ["loop"] = "",
        ["metadata"] = "",
        ["info"] = "",
        ["qr"] = "",
        ["adjust"] = "",
        ["layers"] = "",
        ["grid"] = "",
        ["transparent"] = "",
        ["lock"] = "",
        ["unlock"] = "",
        ["pages"] = "",
        ["copy"] = "",
        ["move"] = "",
        ["link"] = "",
        ["terminal"] = "",
        ["hash"] = "",
        ["share"] = "",
        ["pin"] = "",
        ["rename"] = "",
        ["open"] = "",
        ["gif"] = "",
        ["vj"] = "",
        ["film"] = "",
        ["music"] = "",
        ["waveform"] = "",
        ["palette"] = "",
        ["erase"] = "",
        ["convert"] = "",
    };

    /// <summary>All glyphs this class can return (for tests).</summary>
    public static IEnumerable<string> All =>
        ByIconKey.Values.Concat(typeof(Glyphs).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!));

    public static bool IsKnownIconKey(string iconKey) => ByIconKey.ContainsKey(iconKey);

    /// <summary>Glyph for an operation icon key, or a generic "convert" glyph.</summary>
    public static string ForIconKey(string? iconKey) =>
        iconKey is not null && ByIconKey.TryGetValue(iconKey, out var glyph) ? glyph : ByIconKey["convert"];

    /// <summary>Glyph for a file family.</summary>
    public static string ForKind(FileKind kind) => kind switch
    {
        FileKind.Folder => ByIconKey["folder"],
        FileKind.Image => ByIconKey["image"],
        FileKind.Video => ByIconKey["video"],
        FileKind.Audio => ByIconKey["audio"],
        FileKind.Pdf => ByIconKey["pdf"],
        FileKind.Document => ByIconKey["document"],
        FileKind.Spreadsheet => ByIconKey["spreadsheet"],
        FileKind.Presentation => ByIconKey["presentation"],
        FileKind.Ebook => ByIconKey["ebook"],
        FileKind.Text => ByIconKey["text"],
        FileKind.Subtitle => ByIconKey["subtitle"],
        FileKind.Archive => ByIconKey["archive"],
        _ => Document,
    };
}
