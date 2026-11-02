using System.Collections.Concurrent;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using SkiaSharp;

namespace Clementine.Engines.Documents.Typography;

/// <summary>An installed font face that PDFsharp can embed. <see cref="Key"/> is the family name PDFsharp is given.</summary>
internal sealed class FontFace
{
    readonly SKFont _probe;

    internal FontFace(string key, SKTypeface typeface)
    {
        Key = key;
        Typeface = typeface;
        _probe = new SKFont(typeface);
    }

    public string Key { get; }

    public string FamilyName => Typeface.FamilyName;

    /// <summary>Kept alive for glyph-coverage checks and fallback matching.</summary>
    internal SKTypeface Typeface { get; }

    public bool Covers(int codePoint)
    {
        lock (_probe)
            return _probe.ContainsGlyph(codePoint);
    }
}

/// <summary>
/// Finds Windows fonts through Skia (DirectWrite on Windows), including per-character fallback for scripts the main
/// font lacks (CJK, Arabic, emoji…), and serves their bytes to PDFsharp as its font resolver. TrueType collections
/// (.ttc, e.g. Yu Gothic) are split into single faces because PDFsharp can't read collections.
/// </summary>
internal sealed class FontLibrary : IFontResolver
{
    public static readonly FontLibrary Instance = new();

    static readonly object InstallGate = new();
    static bool _installed;

    readonly ConcurrentDictionary<string, byte[]> _fontData = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, FontFace> _facesByRequest = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<(string Face, int CodePoint), FontFace> _fallbacks = new();
    readonly object _registerGate = new();

    FontLibrary()
    {
    }

    /// <summary>
    /// Registers this resolver with PDFsharp once per process. PDFsharp allows a single global resolver; if another
    /// component installed one first we become the fallback resolver instead.
    /// </summary>
    public static void EnsureInstalled()
    {
        lock (InstallGate)
        {
            if (_installed)
                return;
            if (GlobalFontSettings.FontResolver is null)
                GlobalFontSettings.FontResolver = Instance;
            else if (!ReferenceEquals(GlobalFontSettings.FontResolver, Instance) && GlobalFontSettings.FallbackFontResolver is null)
                GlobalFontSettings.FallbackFontResolver = Instance;
            _installed = true;
        }
    }

    /// <summary>The installed face closest to <paramref name="family"/> (DirectWrite substitutes a default if missing).</summary>
    public FontFace Face(string family, bool bold = false, bool italic = false)
    {
        EnsureInstalled();
        var request = $"{family}|{bold}|{italic}";
        return _facesByRequest.GetOrAdd(request, _ =>
        {
            var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
            var typeface = SKFontManager.Default.MatchFamily(family, style)
                ?? SKFontManager.Default.MatchFamily("Segoe UI", style)
                ?? SKFontManager.Default.MatchFamily("Arial", style)
                ?? SKTypeface.Default;
            return Register(typeface);
        });
    }

    /// <summary>A face that can draw <paramref name="codePoint"/>: <paramref name="preferred"/> if it has the glyph, else a system fallback.</summary>
    public FontFace FaceFor(int codePoint, FontFace preferred)
    {
        if (codePoint < 0x80 || preferred.Covers(codePoint))
            return preferred;
        return _fallbacks.GetOrAdd((preferred.Key, codePoint), key =>
        {
            var match = SKFontManager.Default.MatchCharacter(preferred.FamilyName, preferred.Typeface.FontStyle, null, key.CodePoint);
            return match is null ? preferred : Register(match);
        });
    }

    /// <summary>A PDFsharp font (Unicode encoding, embedded subset) for <paramref name="face"/>.</summary>
    public static XFont CreateFont(FontFace face, double size) =>
        new(face.Key, size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

    // IFontResolver ---------------------------------------------------------------------------------------------

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        if (_fontData.ContainsKey(familyName))
            return new FontResolverInfo(familyName);
        // Someone asked for an ordinary family name ("Arial"): look it up so we behave like a normal resolver.
        try
        {
            var face = Face(familyName, bold, italic);
            return new FontResolverInfo(face.Key);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public byte[]? GetFont(string faceName) => _fontData.TryGetValue(faceName, out var data) ? data : null;

    // ------------------------------------------------------------------------------------------------------------

    FontFace Register(SKTypeface typeface)
    {
        var key = $"Clementine {typeface.FamilyName} {typeface.FontWeight} {(int)typeface.FontSlant}";
        lock (_registerGate)
        {
            if (!_fontData.ContainsKey(key))
                _fontData[key] = ReadFace(typeface);
        }
        return new FontFace(key, typeface);
    }

    static byte[] ReadFace(SKTypeface typeface)
    {
        using var stream = typeface.OpenStream(out var collectionIndex)
            ?? throw new OperationException($"Couldn't load the font \"{typeface.FamilyName}\".");
        var bytes = new byte[stream.Length];
        var read = stream.Read(bytes, bytes.Length);
        if (read != bytes.Length)
            Array.Resize(ref bytes, read);
        return TrueTypeCollection.IsCollection(bytes) ? TrueTypeCollection.ExtractFace(bytes, collectionIndex) : bytes;
    }
}
