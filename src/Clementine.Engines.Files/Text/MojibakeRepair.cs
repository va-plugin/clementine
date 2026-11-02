using System.Text;

namespace Clementine.Engines.Files.Text;

/// <summary>
/// Undoes double-encoding: UTF-8 bytes that were decoded as Windows-1252 and saved as UTF-8 again, which turns
/// "Café" into "CafÃ©" and "don’t" into "donâ€™t". The repair is only applied when the whole text maps back to
/// Windows-1252 bytes that form valid UTF-8 — strong evidence that it really was double-encoded.
/// </summary>
internal static class MojibakeRepair
{
    static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    // Windows-1252 bytes 0x80–0x9F. Bytes it leaves undefined (0x81, 0x8D, 0x8F, 0x90, 0x9D) round-trip as U+0081 etc.
    static readonly char[] HighControls =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ',
    ];

    static readonly Dictionary<char, byte> ByteOf = BuildMap();

    public static bool TryRepair(string text, out string repaired)
    {
        repaired = text;
        if (!LooksDoubleEncoded(text))
            return false;

        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (!ByteOf.TryGetValue(text[i], out var b))
                return false;
            bytes[i] = b;
        }
        try
        {
            var candidate = StrictUtf8.GetString(bytes);
            if (candidate.Length >= text.Length)
                return false;
            repaired = candidate;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>A UTF-8 lead byte (Â…ô) followed by a continuation byte (0x80–0xBF), both seen through Windows-1252.</summary>
    static bool LooksDoubleEncoded(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] is >= 'Â' and <= 'ô'
                && ByteOf.TryGetValue(text[i + 1], out var next) && next is >= 0x80 and <= 0xBF)
                return true;
        }
        return false;
    }

    static Dictionary<char, byte> BuildMap()
    {
        var map = new Dictionary<char, byte>();
        for (var b = 0; b < 0x80; b++)
            map[(char)b] = (byte)b;
        for (var b = 0x80; b < 0xA0; b++)
            map[HighControls[b - 0x80]] = (byte)b;
        for (var b = 0xA0; b <= 0xFF; b++)
            map[(char)b] = (byte)b;
        return map;
    }
}
