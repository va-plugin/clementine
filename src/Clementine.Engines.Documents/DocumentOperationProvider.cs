using Clementine.Engines.Documents.Apple;
using Clementine.Engines.Documents.Ebook;
using Clementine.Engines.Documents.Markup;
using Clementine.Engines.Documents.Office;
using Clementine.Engines.Documents.Pdf;
using Clementine.Engines.Documents.Spreadsheets;

namespace Clementine.Engines.Documents;

/// <summary>
/// PDF, Office, text and e-book operations: PDF tools with PDFsharp / PdfPig / PDFium; Word and Excel files to PDF and
/// HTML with Clementine's built-in converters (printed by Edge), or LibreOffice when it is installed (headless, private
/// profile) — LibreOffice alone handles PowerPoint and older Office formats; HTML/Markdown/EPUB printing through Edge;
/// and native OpenXML readers and writers for DOCX, XLSX and PPTX text.
/// </summary>
public sealed class DocumentOperationProvider : IOperationProvider
{
    public string Name => "Documents";

    /// <summary>
    /// Ignore an installed LibreOffice, as on a machine without it (tests use this to exercise the built-in Word and
    /// Excel converters). LibreOffice-only operations then report it as missing.
    /// </summary>
    internal bool IgnoreLibreOffice { get; init; }

    public IEnumerable<IOperation> CreateOperations(EngineContext context)
    {
        var services = new DocumentServices(context, useLibreOffice: !IgnoreLibreOffice);
        return
        [
            .. PdfConversions.Create(),
            .. PdfPageTools.Create(),
            .. PdfCompression.Create(),
            .. PdfSecurity.Create(),
            .. PdfMetadataTools.Create(),
            .. OfficeOperations.Create(services),
            .. SpreadsheetOperations.Create(services),
            .. MarkupOperations.Create(services),
            .. EpubOperations.Create(services),
            .. AppleOperations.Create(),
        ];
    }
}
