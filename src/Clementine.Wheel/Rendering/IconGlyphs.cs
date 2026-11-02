using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using Clementine.Core;

namespace Clementine.Wheel;

/// <summary>
/// Maps the shared icon vocabulary (<c>OperationInfo.IconKey</c>) and file kinds to Segoe Fluent Icons glyphs.
/// Every glyph here was checked to exist in Segoe Fluent Icons (and Segoe MDL2 Assets uses the same code points);
/// <see cref="Resolve"/> additionally confirms coverage at runtime so a missing glyph is skipped, never drawn as a box.
/// </summary>
internal static class IconGlyphs
{
    /// <summary>Internal key of the "More" segment.</summary>
    public const string MoreKey = "more";

    /// <summary>Internal key for "can't run" hints.</summary>
    public const string WarningKey = "warning";

    /// <summary>Internal key for a selection of mixed kinds.</summary>
    public const string MixedKey = "mixed";

    static readonly Dictionary<string, char> ByKey = new(StringComparer.Ordinal)
    {
        ["video"] = '',
        ["audio"] = '',
        ["image"] = '',
        ["pdf"] = '',
        ["document"] = '',
        ["spreadsheet"] = '',
        ["presentation"] = '',
        ["ebook"] = '',
        ["text"] = '',
        ["code"] = '',
        ["subtitle"] = '',
        ["archive"] = '',
        ["folder"] = '',
        ["extract"] = '',
        ["compress"] = '',
        ["resize"] = '',
        ["crop"] = '',
        ["rotate"] = '',
        ["flip"] = '',
        ["trim"] = '',
        ["split"] = '',
        ["join"] = '',
        ["merge"] = '',
        ["speed"] = '',
        ["snapshot"] = '',
        ["frames"] = '',
        ["mute"] = '',
        ["volume"] = '',
        ["normalize"] = '',
        ["channels"] = '',
        ["bleep"] = '',
        ["fade"] = '',
        ["reverse"] = '',
        ["loop"] = '',
        ["metadata"] = '',
        ["info"] = '',
        ["qr"] = '',
        ["adjust"] = '',
        ["layers"] = '',
        ["grid"] = '',
        ["transparent"] = '',
        ["lock"] = '',
        ["unlock"] = '',
        ["pages"] = '',
        ["copy"] = '',
        ["move"] = '',
        ["link"] = '',
        ["terminal"] = '',
        ["hash"] = '',
        ["share"] = '',
        ["pin"] = '',
        ["rename"] = '',
        ["open"] = '',
        ["gif"] = '',
        ["vj"] = '',
        ["film"] = '',
        ["music"] = '',
        ["waveform"] = '',
        ["palette"] = '',
        ["erase"] = '',
        ["convert"] = '',
        [MoreKey] = '',
        [WarningKey] = '',
        [MixedKey] = '',
    };

    static readonly ConcurrentDictionary<(string Family, char Glyph), bool> Coverage = new();

    /// <summary>Every mapped key (the shared vocabulary plus internal keys).</summary>
    public static IReadOnlyDictionary<string, char> All => ByKey;

    public static char? ForKey(string? key) => key is not null && ByKey.TryGetValue(key, out var glyph) ? glyph : null;

    /// <summary>The icon key that represents a file kind (null kind = a mixed selection).</summary>
    public static string KeyForKind(FileKind? kind) => kind switch
    {
        null => MixedKey,
        FileKind.Folder => "folder",
        FileKind.Image => "image",
        FileKind.Video => "video",
        FileKind.Audio => "audio",
        FileKind.Pdf => "pdf",
        FileKind.Document => "document",
        FileKind.Spreadsheet => "spreadsheet",
        FileKind.Presentation => "presentation",
        FileKind.Ebook => "ebook",
        FileKind.Text => "text",
        FileKind.Subtitle => "subtitle",
        FileKind.Archive => "archive",
        _ => "document",
    };

    /// <summary>
    /// The glyph for <paramref name="key"/> and the single font family that really contains it, or null when no
    /// family of <paramref name="iconFont"/> covers it (so nothing is drawn instead of a tofu box).
    /// </summary>
    public static (string Text, FontFamily Family)? Resolve(string? key, FontFamily iconFont)
    {
        if (ForKey(key) is not { } glyph)
            return null;
        foreach (var name in iconFont.Source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Covers(name, glyph))
                return (glyph.ToString(), new FontFamily(name));
        }
        return null;
    }

    /// <summary>Whether the installed font <paramref name="familyName"/> has a real glyph for <paramref name="glyph"/>.</summary>
    public static bool Covers(string familyName, char glyph) => Coverage.GetOrAdd((familyName, glyph), static k =>
    {
        var typeface = new Typeface(new FontFamily(k.Family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphs))
            return false;
        // TryGetGlyphTypeface falls back to another font when the family isn't installed; make sure it's the real one.
        var name = glyphs.FamilyNames.Values.FirstOrDefault() ?? "";
        if (!string.Equals(name, k.Family, StringComparison.OrdinalIgnoreCase))
            return false;
        return glyphs.CharacterToGlyphMap.ContainsKey(k.Glyph);
    });
}
