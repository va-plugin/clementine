using System.Globalization;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Files.Common;

namespace Clementine.Engines.Files.Text;

/// <summary>A decoded text file plus what was learned about its original encoding.</summary>
/// <param name="Text">The text, without any byte-order mark.</param>
/// <param name="EncodingName">Friendly name of the detected (or forced) encoding, e.g. "Windows-1252 (Western European)".</param>
/// <param name="CodePage">Code page of the source encoding (65001 for UTF-8).</param>
/// <param name="HadBom">Whether the file started with a byte-order mark.</param>
/// <param name="RepairedMojibake">True when UTF-8 text that had been decoded as Windows-1252 and saved again ("CafÃ©") was repaired.</param>
internal sealed record DecodedText(string Text, string EncodingName, int CodePage, bool HadBom, bool RepairedMojibake)
{
    public bool IsUtf8 => CodePage == Encoding.UTF8.CodePage;
}

/// <summary>
/// Detects the encoding of text files: byte-order marks, BOM-less UTF-16, strict UTF-8 validation, then a
/// plausibility score over common legacy code pages (Western, Central European, Cyrillic, Greek, … and the
/// main East Asian multi-byte encodings). Can also undo the classic "UTF-8 read as Windows-1252" mojibake.
/// </summary>
internal static class TextDecoder
{
    /// <summary>Largest file read as text (larger files are refused with a friendly message).</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static readonly int[] SingleByteCandidates = [1252, 1250, 1251, 1253, 1254, 1257, 1255, 1256, 874, 1258];

    static readonly int[] MultiByteCandidates = [932, 936, 949, 950];

    static TextDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Makes sure legacy code pages are available (call before <see cref="Encoding.GetEncoding(int)"/>).</summary>
    public static void EnsureCodePages()
    {
        // Touching the type runs the static constructor.
    }

    public static DecodedText ReadFile(string path, Encoding? forced = null, bool repairMojibake = false)
    {
        var length = new FileInfo(path).Length;
        if (length > MaxBytes)
            throw new OperationException($"This file is too large ({ByteSize.Format(length)}) to convert as text.");
        return Decode(File.ReadAllBytes(path), forced, repairMojibake);
    }

    /// <summary>Decodes <paramref name="bytes"/>, detecting the encoding unless <paramref name="forced"/> is given.</summary>
    public static DecodedText Decode(byte[] bytes, Encoding? forced = null, bool repairMojibake = false)
    {
        if (forced is not null)
            return DecodeForced(bytes, forced);

        if (DetectBom(bytes) is { } bom)
        {
            var text = bom.Encoding.GetString(bytes, bom.Length, bytes.Length - bom.Length);
            if (bom.Encoding.CodePage == Encoding.UTF8.CodePage && repairMojibake && MojibakeRepair.TryRepair(text, out var fixedText))
                return new DecodedText(fixedText, "UTF-8 (repaired)", Encoding.UTF8.CodePage, HadBom: true, RepairedMojibake: true);
            return new DecodedText(text, EncodingCatalog.DisplayName(bom.Encoding.CodePage), bom.Encoding.CodePage, HadBom: true, RepairedMojibake: false);
        }

        if (DetectBomlessUtf16(bytes) is { } utf16)
        {
            var text = utf16.GetString(bytes);
            return new DecodedText(text, EncodingCatalog.DisplayName(utf16.CodePage), utf16.CodePage, HadBom: false, RepairedMojibake: false);
        }

        if (LooksBinary(bytes))
            throw new OperationException("This looks like a binary file, not text.");

        if (TryDecode(StrictUtf8, bytes, out var utf8))
        {
            if (repairMojibake && MojibakeRepair.TryRepair(utf8, out var fixedText))
                return new DecodedText(fixedText, "UTF-8 (repaired)", Encoding.UTF8.CodePage, HadBom: false, RepairedMojibake: true);
            return new DecodedText(utf8, "UTF-8", Encoding.UTF8.CodePage, HadBom: false, RepairedMojibake: false);
        }

        var (codePage, decoded) = GuessLegacy(bytes);
        return new DecodedText(decoded, EncodingCatalog.DisplayName(codePage), codePage, HadBom: false, RepairedMojibake: false);
    }

    static DecodedText DecodeForced(byte[] bytes, Encoding encoding)
    {
        var skip = 0;
        var hadBom = false;
        if (DetectBom(bytes) is { } bom && bom.Encoding.CodePage == encoding.CodePage)
        {
            skip = bom.Length;
            hadBom = true;
        }
        var text = encoding.GetString(bytes, skip, bytes.Length - skip);
        return new DecodedText(text, EncodingCatalog.DisplayName(encoding.CodePage), encoding.CodePage, hadBom, RepairedMojibake: false);
    }

    internal readonly record struct Bom(Encoding Encoding, int Length);

    internal static Bom? DetectBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return new Bom(new UTF8Encoding(false), 3);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
            return new Bom(new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
            return new Bom(new UTF32Encoding(bigEndian: true, byteOrderMark: false), 4);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return new Bom(new UnicodeEncoding(bigEndian: false, byteOrderMark: false), 2);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new Bom(new UnicodeEncoding(bigEndian: true, byteOrderMark: false), 2);
        return null;
    }

    /// <summary>UTF-16 without a BOM: mostly-Latin text has a zero byte in every other position.</summary>
    static Encoding? DetectBomlessUtf16(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes[..(Math.Min(bytes.Length, 16 * 1024) & ~1)];
        var pairs = sample.Length / 2;
        if (pairs < 2)
            return null;
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i + 1 < sample.Length; i += 2)
        {
            if (sample[i] == 0)
                evenZeros++;
            if (sample[i + 1] == 0)
                oddZeros++;
        }
        if (oddZeros >= pairs * 0.4 && evenZeros <= pairs * 0.05)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
        if (evenZeros >= pairs * 0.4 && oddZeros <= pairs * 0.05)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
        return null;
    }

    static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes[..Math.Min(bytes.Length, 64 * 1024)];
        if (sample.Length == 0)
            return false;
        var controls = 0;
        foreach (var b in sample)
        {
            if (b == 0)
                return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1A && b != 0x1B))
                controls++;
        }
        return controls > sample.Length / 10;
    }

    static bool TryDecode(Encoding strict, byte[] bytes, out string text)
    {
        try
        {
            text = strict.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>Picks the legacy code page whose decoding reads most like natural text (scored on a sample).</summary>
    internal static (int CodePage, string Text) GuessLegacy(byte[] bytes)
    {
        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, 256 * 1024));
        var preferred = PreferredCodePage();
        var bestCodePage = preferred;
        var bestScore = TextPlausibility.Score(Encoding.GetEncoding(preferred).GetString(sample));

        foreach (var codePage in SingleByteCandidates)
        {
            if (codePage == preferred)
                continue;
            var score = TextPlausibility.Score(Encoding.GetEncoding(codePage).GetString(sample));
            if (score > bestScore + 0.5)
                (bestCodePage, bestScore) = (codePage, score);
        }

        if (HighByteRatio(sample) > 0.2)
        {
            foreach (var codePage in MultiByteCandidates)
            {
                var strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                if (!TryDecodeSample(strict, sample, isComplete: sample.Length == bytes.Length, out var text))
                    continue;
                var score = TextPlausibility.Score(text);
                if (score > bestScore + 0.5)
                    (bestCodePage, bestScore) = (codePage, score);
            }
        }
        return (bestCodePage, Encoding.GetEncoding(bestCodePage).GetString(bytes));
    }

    /// <summary>Strictly decodes a sample; a multi-byte character cut off at the end of a partial sample is fine.</summary>
    static bool TryDecodeSample(Encoding strict, ReadOnlySpan<byte> sample, bool isComplete, out string text)
    {
        try
        {
            var decoder = strict.GetDecoder();
            var chars = new char[decoder.GetCharCount(sample, flush: isComplete)];
            var count = decoder.GetChars(sample, chars, flush: isComplete);
            text = new string(chars, 0, count);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>The system's ANSI code page when it is one we score, otherwise Windows-1252.</summary>
    static int PreferredCodePage()
    {
        var ansi = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        return SingleByteCandidates.Contains(ansi) ? ansi : 1252;
    }

    static double HighByteRatio(ReadOnlySpan<byte> bytes)
    {
        int high = 0, visible = 0;
        foreach (var b in bytes)
        {
            if (b <= 0x20)
                continue;
            visible++;
            if (b >= 0x80)
                high++;
        }
        return visible == 0 ? 0 : (double)high / visible;
    }
}
