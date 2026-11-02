using System.Text;
using Clementine.Core;

namespace Clementine.Engines.Documents.Common;

/// <summary>Reads text files in whatever encoding they use and writes Clementine's text outputs.</summary>
internal static class TextFiles
{
    /// <summary>Largest text input we load into memory (keeps a mistaken 2 GB log from freezing the machine).</summary>
    public const long MaxTextBytes = 200L * 1024 * 1024;

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 without a byte order mark (plain text, HTML, Markdown outputs).</summary>
    public static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>UTF-8 with a byte order mark (CSV, so Excel recognises Unicode).</summary>
    public static readonly Encoding Utf8Bom = new UTF8Encoding(true);

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Reads a text file: BOM → UTF-8/16/32, else valid UTF-8, else Windows-1252.</summary>
    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxTextBytes)
            throw new OperationException($"This file is too large to convert ({info.Length / (1024 * 1024)} MB). The limit is {MaxTextBytes / (1024 * 1024)} MB.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(bytes);
    }

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (LooksLikeUtf16(bytes, out var bigEndian))
            return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>Normalises CRLF / CR to LF so line handling is uniform.</summary>
    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Writes text with Windows line endings.</summary>
    public static Task WriteAsync(string path, string text, CancellationToken cancellationToken, Encoding? encoding = null) =>
        File.WriteAllTextAsync(path, NormalizeNewlines(text).Replace("\n", "\r\n", StringComparison.Ordinal), encoding ?? Utf8, cancellationToken);

    /// <summary>BOM-less UTF-16 is recognisable by the many zero bytes in ASCII-heavy text.</summary>
    static bool LooksLikeUtf16(byte[] bytes, out bool bigEndian)
    {
        bigEndian = false;
        var sample = Math.Min(bytes.Length, 4096) & ~1;
        if (sample < 8)
            return false;
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < sample; i += 2)
        {
            if (bytes[i] == 0)
                evenZeros++;
            if (bytes[i + 1] == 0)
                oddZeros++;
        }
        var pairs = sample / 2;
        if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05)
            return true;
        if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05)
        {
            bigEndian = true;
            return true;
        }
        return false;
    }
}
