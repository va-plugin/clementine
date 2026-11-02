using System.Text;

namespace Clementine.Engines.Files.Text;

/// <summary>Writes text outputs as UTF-8 with the BOM policy each file family expects.</summary>
internal static class TextOutput
{
    static readonly UTF8Encoding WithBom = new(encoderShouldEmitUTF8Identifier: true);
    static readonly UTF8Encoding WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    static readonly HashSet<string> BomExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Excel only reads UTF-8 CSV correctly with a BOM; many hardware/TV players need it for subtitles.
        ".csv", ".tsv", ".srt", ".ass", ".ssa", ".sbv", ".sub",
    };

    /// <summary>Whether files with this extension get a UTF-8 BOM.</summary>
    public static bool UsesBom(string extension) => BomExtensions.Contains(extension);

    public static Encoding Utf8For(string extension) => UsesBom(extension) ? WithBom : WithoutBom;

    public static void WriteUtf8(string path, string text, string extension) =>
        File.WriteAllText(path, text, Utf8For(extension));
}
