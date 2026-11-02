using System.Globalization;

namespace Clementine.Engines.Files.Text;

/// <summary>
/// Scores how much a decoded string reads like natural text. Wrong code pages betray themselves through words that
/// mix scripts ("cafй"), symbols wedged between letters ("za¿ó³æ"), C1 control characters, or Latin words made
/// almost entirely of accented letters ("Ïðèâåò" is Russian read as Windows-1252).
/// </summary>
internal static class TextPlausibility
{
    enum Script
    {
        None,
        Latin,
        Cyrillic,
        Greek,
        Hebrew,
        Arabic,
        Thai,
        Kana,
        Han,
        Hangul,
        Other,
    }

    public static double Score(string text)
    {
        double score = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsWordChar(c))
            {
                var start = i;
                while (i < text.Length && IsWordChar(text[i]))
                    i++;
                score += ScoreWord(text.AsSpan(start, i - start));
                continue;
            }

            if (c is >= '\u0080' and <= '\u009F')
                score -= 20;
            else if (c == '\uFFFD')
                score -= 20;
            else if (c is >= '｡' and <= 'ﾟ')
                score -= 3; // half-width katakana: a classic sign of mis-decoded multi-byte text
            else if (c is >= '\uE000' and <= '\uF8FF')
                score -= 10; // private use area
            else if (c >= 0x80 && !char.IsWhiteSpace(c) && i > 0 && i + 1 < text.Length && IsWordChar(text[i - 1]) && IsWordChar(text[i + 1]))
                score -= 6; // a symbol such as '¿' or '³' squeezed between letters
            i++;
        }
        return score;
    }

    static bool IsWordChar(char c) =>
        char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    /// <summary>
    /// The most frequent letters of each script's main languages. Two code pages can both decode to clean words of
    /// one script (Greek read as Cyrillic is still "Cyrillic"); only the right one produces the common letters.
    /// </summary>
    const string FrequentLetters =
        "оеаинтсрвлкмдпуОЕАИНТСРВЛКМДПУ" +       // Russian/Ukrainian
        "αοειτνσηκπρυμλςάέίόήΑΟΕΙΤΝΣΗΚΠΡΥΜΛ" +    // Greek
        "יוהלאמתרבנשכעד" +                        // Hebrew
        "اليمونرتبةهكدسعف";                       // Arabic

    static double ScoreWord(ReadOnlySpan<char> word)
    {
        var script = Script.None;
        var mixed = false;
        int nonAsciiLatin = 0, letters = 0, kana = 0;
        double frequent = 0;
        foreach (var c in word)
        {
            var s = ScriptOf(c);
            if (s == Script.None)
                continue;
            letters++;
            if (c > '\u007F' && FrequentLetters.Contains(c))
                frequent += 0.5;
            if (s == Script.Latin && c > '\u007F')
                nonAsciiLatin++;
            if (s == Script.Kana)
                kana++;
            // Japanese freely mixes kana and kanji; treat them as one script.
            var normalised = s == Script.Kana ? Script.Han : s;
            if (script == Script.None)
                script = normalised;
            else if (script != normalised)
                mixed = true;
        }

        if (letters == 0)
            return 0;
        if (mixed)
            return -5.0 * letters;
        if (script == Script.Other)
            return -1.0 * letters;
        if (script == Script.Latin && nonAsciiLatin > 0)
        {
            // "café", "Straße" are fine; "Ïðèâåò" (all accented) is not.
            if (letters >= 3 && nonAsciiLatin * 2 > letters)
                return -2.0 * nonAsciiLatin;
            return letters - 0.25 * nonAsciiLatin;
        }
        return letters + 0.5 * kana + frequent;
    }

    static Script ScriptOf(char c) => c switch
    {
        _ when c < 'A' => Script.None,
        <= 'Z' => Script.Latin,
        >= 'a' and <= 'z' => Script.Latin,
        _ when c < 'À' => char.IsLetter(c) ? Script.Other : Script.None, // ª º µ
        <= 'ɏ' => c is '×' or '÷' ? Script.None : Script.Latin,
        >= 'Ḁ' and <= 'ỿ' => Script.Latin,
        >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
        >= 'Ѐ' and <= 'ԯ' => Script.Cyrillic,
        >= '֐' and <= '׿' => Script.Hebrew,
        >= '؀' and <= 'ۿ' => Script.Arabic,
        >= '฀' and <= '๿' => Script.Thai,
        >= '぀' and <= 'ヿ' => Script.Kana,
        >= '㐀' and <= '鿿' => Script.Han,
        >= '豈' and <= '﫿' => Script.Han,
        >= '가' and <= '힯' => Script.Hangul,
        >= 'ᄀ' and <= 'ᇿ' => Script.Hangul,
        >= '̀' and <= 'ͯ' => Script.None, // combining marks
        _ => char.IsLetter(c) ? Script.Other : Script.None,
    };
}
