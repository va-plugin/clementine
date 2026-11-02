using DocumentFormat.OpenXml.Packaging;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>Opens Office Open XML packages read-only from a memory copy, with friendly errors.</summary>
internal static class OpenXmlFiles
{
    public static WordprocessingDocument OpenWord(string path) =>
        Open(path, stream => WordprocessingDocument.Open(stream, false), "Word document");

    public static SpreadsheetDocument OpenSpreadsheet(string path) =>
        Open(path, stream => SpreadsheetDocument.Open(stream, false), "spreadsheet");

    public static PresentationDocument OpenPresentation(string path) =>
        Open(path, stream => PresentationDocument.Open(stream, false), "presentation");

    static T Open<T>(string path, Func<Stream, T> open, string kind)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it may be open in another program.", ex.Message, ex);
        }

        // Password-protected Office files are OLE compound files ("D0 CF 11 E0"), not zip packages.
        if (bytes.Length >= 4 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0)
            throw new OperationException($"This {kind} is password-protected or in an old binary format, so Clementine can't read it.");
        try
        {
            return open(new MemoryStream(bytes, writable: false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it doesn't look like a valid {kind}.", ex.ToString(), ex);
        }
    }
}
