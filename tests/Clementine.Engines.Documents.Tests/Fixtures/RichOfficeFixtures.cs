using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;

namespace Clementine.Engines.Documents.Tests.Fixtures;

/// <summary>
/// Feature-rich Word and Excel files built with the OpenXML SDK (parts fed from WordprocessingML / SpreadsheetML), for the
/// built-in converters: styles, run formatting, lists, tables with merges, pictures, links, notes, headers/footers,
/// page setup; number formats, fills, borders, merges, hidden rows/columns, print areas and several sheets.
/// </summary>
internal static class RichOfficeFixtures
{
    public const string Unicode = "Ünïcødé – 東京 – Ελληνικά";

    /// <summary>The paragraphs and cells of <see cref="RichDocx"/> in reading order.</summary>
    public static readonly string[] DocxTextInOrder =
    [
        "Quarterly Report 2026",
        "Overview",
        "This report covers revenue, costs, growth and old plans.",
        "Red text highlighted big monospace",
        "Unicode: " + Unicode,
        "Centered line",
        "Right aligned line",
        "Justified paragraph:",
        "Indented by one inch",
        "Lists",
        "First step", "Second step", "Sub-step a", "Sub-step b", "Third step",
        "Apples", "Green apples", "Pears",
        "Table",
        "Region", "Q1", "Q2", "North", "1,200", "1,350", "Merged across", "Total", "Tall cell", "a", "b", "c", "d",
        "Picture",
        "Figure 1 – orange block",
        "Visit example.com or jump to the overview.",
        "Chapter One", "7",
        "Second page",
        "The end of the document.",
        "Footnote text with ünïcødé.",
    ];

    public static string RichDocx(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var image = main.AddImagePart(ImagePartType.Png, "rIdImage1");
        using (var png = new MemoryStream(Png(200, 100, new SKColor(0xFF, 0x8A, 0x1F))))
            image.FeedData(png);
        main.AddHyperlinkRelationship(new Uri("https://example.com/report"), true, "rIdLink1");
        Feed(main.AddNewPart<StyleDefinitionsPart>("rIdStyles"), Styles);
        Feed(main.AddNewPart<NumberingDefinitionsPart>("rIdNumbering"), Numbering);
        Feed(main.AddNewPart<FootnotesPart>("rIdFootnotes"), Footnotes);
        Feed(main.AddNewPart<HeaderPart>("rIdHeader1"), Header);
        Feed(main.AddNewPart<FooterPart>("rIdFooter1"), Footer);
        Feed(main, Body);
        document.PackageProperties.Title = "Quarterly Report";
        return path;
    }

    /// <summary>
    /// Two sections: portrait Letter with a different first-page header and two pages, then a landscape A5 section in
    /// two columns that inherits the running header; plus an endnote.
    /// </summary>
    public static string SectionsDocx(string path)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        Feed(main.AddNewPart<StyleDefinitionsPart>("rIdStyles"), Styles);
        Feed(main.AddNewPart<HeaderPart>("rIdFirst"), HeaderWith("First page header"));
        Feed(main.AddNewPart<HeaderPart>("rIdRunning"), HeaderWith("Running header"));
        Feed(main.AddNewPart<EndnotesPart>("rIdEndnotes"), """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:endnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:endnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:endnote>
              <w:endnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:endnote>
              <w:endnote w:id="1"><w:p><w:r><w:endnoteRef/></w:r><w:r><w:t xml:space="preserve"> Endnote text.</w:t></w:r></w:p></w:endnote>
            </w:endnotes>
            """);
        var columnText = string.Concat(Enumerable.Range(1, 12).Select(i =>
            $"<w:p><w:r><w:t>Column paragraph {i} with enough words to fill a narrow newspaper-style column on a landscape page.</w:t></w:r></w:p>"));
        Feed(main, $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document {{Namespaces}}><w:body>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Cover page</w:t></w:r></w:p>
              <w:p><w:r><w:t xml:space="preserve">A sentence with an endnote.</w:t></w:r><w:r><w:rPr><w:vertAlign w:val="superscript"/></w:rPr><w:endnoteReference w:id="1"/></w:r></w:p>
              <w:p><w:pPr><w:pageBreakBefore/></w:pPr><w:r><w:t>Body page</w:t></w:r></w:p>
              <w:p><w:pPr><w:sectPr>
                <w:headerReference w:type="first" r:id="rIdFirst"/>
                <w:headerReference w:type="default" r:id="rIdRunning"/>
                <w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="720" w:footer="720" w:gutter="0"/>
                <w:titlePg/>
              </w:sectPr></w:pPr></w:p>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Landscape section</w:t></w:r></w:p>
              {{columnText}}
              <w:sectPr>
                <w:type w:val="nextPage"/>
                <w:pgSz w:w="11906" w:h="8391" w:orient="landscape"/><w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720" w:header="360" w:footer="360" w:gutter="0"/>
                <w:cols w:num="2" w:space="720"/>
              </w:sectPr>
            </w:body></w:document>
            """);
        return path;
    }

    static string HeaderWith(string text) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:p><w:pPr><w:pStyle w:val="Header"/><w:jc w:val="center"/></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p></w:hdr>
        """;

    static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }

    static byte[] Png(int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.White };
        canvas.DrawCircle(width / 2f, height / 2f, height / 3f, paint);
        using var encoded = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"";

    static readonly string Body = $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:document {{Namespaces}}><w:body>
          <w:p><w:pPr><w:pStyle w:val="Title"/></w:pPr><w:r><w:t>Quarterly Report 2026</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:bookmarkStart w:id="0" w:name="_Overview"/><w:r><w:t>Overview</w:t></w:r><w:bookmarkEnd w:id="0"/></w:p>
          <w:p>
            <w:r><w:t xml:space="preserve">This report covers </w:t></w:r>
            <w:r><w:rPr><w:b/></w:rPr><w:t>revenue</w:t></w:r>
            <w:r><w:t xml:space="preserve">, </w:t></w:r>
            <w:r><w:rPr><w:i/></w:rPr><w:t>costs</w:t></w:r>
            <w:r><w:t xml:space="preserve">, </w:t></w:r>
            <w:r><w:rPr><w:u w:val="single"/></w:rPr><w:t>growth</w:t></w:r>
            <w:r><w:t xml:space="preserve"> and </w:t></w:r>
            <w:r><w:rPr><w:strike/></w:rPr><w:t>old plans</w:t></w:r>
            <w:r><w:t xml:space="preserve">. </w:t></w:r>
            <w:r><w:rPr><w:color w:val="C00000"/></w:rPr><w:t>Red text</w:t></w:r>
            <w:r><w:t xml:space="preserve"> </w:t></w:r>
            <w:r><w:rPr><w:highlight w:val="yellow"/></w:rPr><w:t>highlighted</w:t></w:r>
            <w:r><w:t xml:space="preserve"> </w:t></w:r>
            <w:r><w:rPr><w:sz w:val="32"/></w:rPr><w:t>big</w:t></w:r>
            <w:r><w:t xml:space="preserve"> </w:t></w:r>
            <w:r><w:rPr><w:rFonts w:ascii="Courier New" w:hAnsi="Courier New"/></w:rPr><w:t>monospace</w:t></w:r>
            <w:r><w:t xml:space="preserve"> Unicode: {{Unicode}}.</w:t></w:r>
            <w:r><w:rPr><w:rStyle w:val="FootnoteReference"/></w:rPr><w:footnoteReference w:id="1"/></w:r>
          </w:p>
          <w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:t>Centered line</w:t></w:r></w:p>
          <w:p><w:pPr><w:jc w:val="right"/></w:pPr><w:r><w:t>Right aligned line</w:t></w:r></w:p>
          <w:p><w:pPr><w:spacing w:line="360" w:lineRule="auto"/><w:ind w:firstLine="720"/><w:jc w:val="both"/></w:pPr>
            <w:r><w:t>Justified paragraph: the built-in converter keeps alignment, first-line indents and one-and-a-half line spacing, so long paragraphs wrap across several lines the way they do in Word and keep reading naturally.</w:t></w:r></w:p>
          <w:p><w:pPr><w:ind w:left="1440"/></w:pPr><w:r><w:t>Indented by one inch</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Lists</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="2"/></w:numPr></w:pPr><w:r><w:t>First step</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="2"/></w:numPr></w:pPr><w:r><w:t>Second step</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="1"/><w:numId w:val="2"/></w:numPr></w:pPr><w:r><w:t>Sub-step a</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="1"/><w:numId w:val="2"/></w:numPr></w:pPr><w:r><w:t>Sub-step b</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="2"/></w:numPr></w:pPr><w:r><w:t>Third step</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Apples</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="1"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Green apples</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Pears</w:t></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Table</w:t></w:r></w:p>
          <w:tbl>
            <w:tblPr><w:tblStyle w:val="TableGrid"/><w:tblW w:w="0" w:type="auto"/><w:tblLook w:val="04A0" w:firstRow="1" w:lastRow="0" w:firstColumn="1" w:lastColumn="0" w:noHBand="0" w:noVBand="1"/></w:tblPr>
            <w:tblGrid><w:gridCol w:w="3000"/><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/></w:tblGrid>
            <w:tr><w:trPr><w:tblHeader/></w:trPr>
              <w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/><w:shd w:val="clear" w:color="auto" w:fill="D9E2F3"/></w:tcPr><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Region</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/><w:shd w:val="clear" w:color="auto" w:fill="D9E2F3"/></w:tcPr><w:p><w:pPr><w:jc w:val="right"/></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>Q1</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/><w:shd w:val="clear" w:color="auto" w:fill="D9E2F3"/></w:tcPr><w:p><w:pPr><w:jc w:val="right"/></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>Q2</w:t></w:r></w:p></w:tc>
            </w:tr>
            <w:tr>
              <w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>North</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/></w:tcPr><w:p><w:pPr><w:jc w:val="right"/></w:pPr><w:r><w:t>1,200</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/><w:shd w:val="clear" w:color="auto" w:fill="FFF2CC"/></w:tcPr><w:p><w:pPr><w:jc w:val="right"/></w:pPr><w:r><w:t>1,350</w:t></w:r></w:p></w:tc>
            </w:tr>
            <w:tr>
              <w:tc><w:tcPr><w:tcW w:w="5000" w:type="dxa"/><w:gridSpan w:val="2"/></w:tcPr><w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:t>Merged across</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/><w:tcBorders><w:top w:val="double" w:sz="6" w:space="0" w:color="000000"/></w:tcBorders></w:tcPr><w:p><w:pPr><w:jc w:val="right"/></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>Total</w:t></w:r></w:p></w:tc>
            </w:tr>
            <w:tr>
              <w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/><w:vMerge w:val="restart"/><w:vAlign w:val="center"/></w:tcPr><w:p><w:r><w:t>Tall cell</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>a</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>b</w:t></w:r></w:p></w:tc>
            </w:tr>
            <w:tr>
              <w:tc><w:tcPr><w:tcW w:w="3000" w:type="dxa"/><w:vMerge/></w:tcPr><w:p/></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>c</w:t></w:r></w:p></w:tc>
              <w:tc><w:tcPr><w:tcW w:w="2000" w:type="dxa"/></w:tcPr><w:p><w:r><w:t>d</w:t></w:r></w:p></w:tc>
            </w:tr>
          </w:tbl>
          <w:p><w:pPr><w:pStyle w:val="Heading2"/></w:pPr><w:r><w:t>Picture</w:t></w:r></w:p>
          <w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:drawing>
            <wp:inline distT="0" distB="0" distL="0" distR="0"><wp:extent cx="1905000" cy="952500"/><wp:docPr id="1" name="Picture 1" descr="Orange block"/>
              <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <pic:pic><pic:nvPicPr><pic:cNvPr id="1" name="orange.png"/><pic:cNvPicPr/></pic:nvPicPr>
                  <pic:blipFill><a:blip r:embed="rIdImage1"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                  <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1905000" cy="952500"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>
              </a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="Caption"/><w:jc w:val="center"/></w:pPr><w:r><w:t>Figure 1 – orange block</w:t></w:r></w:p>
          <w:p><w:r><w:t xml:space="preserve">Visit </w:t></w:r><w:hyperlink r:id="rIdLink1"><w:r><w:rPr><w:rStyle w:val="Hyperlink"/></w:rPr><w:t>example.com</w:t></w:r></w:hyperlink><w:r><w:t xml:space="preserve"> or jump to </w:t></w:r><w:hyperlink w:anchor="_Overview"><w:r><w:rPr><w:rStyle w:val="Hyperlink"/></w:rPr><w:t>the overview</w:t></w:r></w:hyperlink><w:r><w:t>.</w:t></w:r></w:p>
          <w:p><w:pPr><w:tabs><w:tab w:val="right" w:leader="dot" w:pos="9350"/></w:tabs></w:pPr><w:r><w:t>Chapter One</w:t></w:r><w:r><w:tab/></w:r><w:r><w:t>7</w:t></w:r></w:p>
          <w:p><w:r><w:br w:type="page"/></w:r></w:p>
          <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Second page</w:t></w:r></w:p>
          <w:p><w:r><w:t>The end of the document.</w:t></w:r></w:p>
          <w:sectPr>
            <w:headerReference w:type="default" r:id="rIdHeader1"/>
            <w:footerReference w:type="default" r:id="rIdFooter1"/>
            <w:pgSz w:w="11906" w:h="16838"/>
            <w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1418" w:header="567" w:footer="567" w:gutter="0"/>
          </w:sectPr>
        </w:body></w:document>
        """;

    static readonly string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Yu Gothic" w:cs="Arial"/><w:sz w:val="22"/><w:lang w:val="en-US"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="259" w:lineRule="auto"/></w:pPr></w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
          <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="240"/></w:pPr><w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:color w:val="1F3864"/><w:sz w:val="56"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="240" w:after="0"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:rFonts w:ascii="Calibri Light" w:hAnsi="Calibri Light"/><w:color w:val="2F5496"/><w:sz w:val="32"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:keepNext/><w:spacing w:before="40" w:after="0"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:color w:val="2F5496"/><w:sz w:val="26"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="720"/><w:contextualSpacing/></w:pPr></w:style>
          <w:style w:type="paragraph" w:styleId="Caption"><w:name w:val="caption"/><w:basedOn w:val="Normal"/><w:rPr><w:i/><w:color w:val="44546A"/><w:sz w:val="18"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="FootnoteText"><w:name w:val="footnote text"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr><w:rPr><w:sz w:val="20"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Header"><w:name w:val="header"/><w:basedOn w:val="Normal"/><w:pPr><w:tabs><w:tab w:val="center" w:pos="4680"/><w:tab w:val="right" w:pos="9360"/></w:tabs><w:spacing w:after="0"/></w:pPr></w:style>
          <w:style w:type="paragraph" w:styleId="Footer"><w:name w:val="footer"/><w:basedOn w:val="Normal"/><w:pPr><w:tabs><w:tab w:val="center" w:pos="4680"/><w:tab w:val="right" w:pos="9360"/></w:tabs><w:spacing w:after="0"/></w:pPr></w:style>
          <w:style w:type="character" w:default="1" w:styleId="DefaultParagraphFont"><w:name w:val="Default Paragraph Font"/></w:style>
          <w:style w:type="character" w:styleId="Hyperlink"><w:name w:val="Hyperlink"/><w:rPr><w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr></w:style>
          <w:style w:type="character" w:styleId="FootnoteReference"><w:name w:val="footnote reference"/><w:rPr><w:vertAlign w:val="superscript"/></w:rPr></w:style>
          <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
          <w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:basedOn w:val="TableNormal"/><w:pPr><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr>
            <w:tblPr><w:tblBorders><w:top w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:left w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:right w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="auto"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="auto"/></w:tblBorders></w:tblPr></w:style>
        </w:styles>
        """;

    /// <summary>Word's Symbol-font bullet (a private-use character).</summary>
    const string Bullet = "\uF0B7";

    static readonly string Numbering = $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:abstractNum w:abstractNumId="0">
            <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="{{Bullet}}"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Symbol" w:hAnsi="Symbol" w:hint="default"/></w:rPr></w:lvl>
            <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="bullet"/><w:lvlText w:val="o"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr><w:rPr><w:rFonts w:ascii="Courier New" w:hAnsi="Courier New" w:cs="Courier New" w:hint="default"/></w:rPr></w:lvl>
          </w:abstractNum>
          <w:abstractNum w:abstractNumId="1">
            <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
            <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%2)"/><w:lvlJc w:val="left"/><w:pPr><w:ind w:left="1440" w:hanging="360"/></w:pPr></w:lvl>
          </w:abstractNum>
          <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
          <w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
        </w:numbering>
        """;

    static readonly string Footnotes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
          <w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>
          <w:footnote w:id="1"><w:p><w:pPr><w:pStyle w:val="FootnoteText"/></w:pPr><w:r><w:rPr><w:rStyle w:val="FootnoteReference"/></w:rPr><w:footnoteRef/></w:r><w:r><w:t xml:space="preserve"> Footnote text with ünïcødé.</w:t></w:r></w:p></w:footnote>
        </w:footnotes>
        """;

    static readonly string Header = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:p><w:pPr><w:pStyle w:val="Header"/></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>Clementine Fixture Co.</w:t></w:r><w:r><w:tab/></w:r><w:r><w:tab/></w:r><w:r><w:t>Quarterly report</w:t></w:r></w:p>
        </w:hdr>
        """;

    static readonly string Footer = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:p><w:pPr><w:pStyle w:val="Footer"/><w:jc w:val="center"/></w:pPr>
            <w:r><w:t xml:space="preserve">Page </w:t></w:r>
            <w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>1</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r>
            <w:r><w:t xml:space="preserve"> of </w:t></w:r>
            <w:fldSimple w:instr=" NUMPAGES "><w:r><w:t>1</w:t></w:r></w:fldSimple>
          </w:p>
        </w:ftr>
        """;

    // Excel ---------------------------------------------------------------------------------------------------------

    /// <summary>The OLE serial of 14 March 2026.</summary>
    public static readonly double March14 = new DateTime(2026, 3, 14).ToOADate();

    public static string RichXlsx(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        Feed(workbook.AddNewPart<WorkbookStylesPart>("rIdStyles"), WorkbookStyles);
        Feed(workbook.AddNewPart<SharedStringTablePart>("rIdStrings"), SharedStrings);
        var sales = workbook.AddNewPart<WorksheetPart>("rIdSales");
        sales.AddHyperlinkRelationship(new Uri("https://example.com/sales"), true, "rIdLink1");
        Feed(sales, SalesSheet);
        Feed(workbook.AddNewPart<WorksheetPart>("rIdWide"), WideSheet());
        Feed(workbook.AddNewPart<WorksheetPart>("rIdHidden"), SimpleSheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Secret sheet</t></is></c></row>"));
        Feed(workbook.AddNewPart<WorksheetPart>("rIdNotes"), SimpleSheet("<cols><col min=\"1\" max=\"1\" width=\"20\" customWidth=\"1\"/></cols>",
            "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Note</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Owner</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Print area only</t></is></c><c r=\"B2\" t=\"inlineStr\"><is><t>Ada</t></is></c></row>" +
            "<row r=\"5\"><c r=\"C5\" t=\"inlineStr\"><is><t>Outside print area</t></is></c></row>"));
        Feed(workbook, Workbook);
        return path;
    }

    const string Main = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    static readonly string Workbook = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook {Main}>
          <sheets>
            <sheet name="Sales" sheetId="1" r:id="rIdSales"/>
            <sheet name="Wide" sheetId="2" r:id="rIdWide"/>
            <sheet name="Hidden" sheetId="3" state="hidden" r:id="rIdHidden"/>
            <sheet name="Notes" sheetId="4" r:id="rIdNotes"/>
          </sheets>
          <definedNames><definedName name="_xlnm.Print_Area" localSheetId="3">Notes!$A$1:$B$2</definedName></definedNames>
        </workbook>
        """;

    static readonly string WorkbookStyles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="5">
            <numFmt numFmtId="164" formatCode="#,##0.00"/>
            <numFmt numFmtId="165" formatCode="d mmm yyyy"/>
            <numFmt numFmtId="166" formatCode="[$€-407]#,##0.00"/>
            <numFmt numFmtId="167" formatCode="0.0%"/>
            <numFmt numFmtId="168" formatCode="#,##0;[Red](#,##0)"/>
          </numFmts>
          <fonts count="4">
            <font><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/></font>
            <font><b/><sz val="11"/><color theme="1"/><name val="Calibri"/></font>
            <font><i/><sz val="11"/><color rgb="FFFF0000"/><name val="Calibri"/></font>
            <font><b/><sz val="16"/><color rgb="FF1F4E79"/><name val="Calibri"/></font>
          </fonts>
          <fills count="4">
            <fill><patternFill patternType="none"/></fill>
            <fill><patternFill patternType="gray125"/></fill>
            <fill><patternFill patternType="solid"><fgColor rgb="FFFFF2CC"/><bgColor indexed="64"/></patternFill></fill>
            <fill><patternFill patternType="solid"><fgColor theme="4" tint="0.79998168889431442"/><bgColor indexed="64"/></patternFill></fill>
          </fills>
          <borders count="2">
            <border><left/><right/><top/><bottom/><diagonal/></border>
            <border><left style="thin"><color indexed="64"/></left><right style="thin"><color indexed="64"/></right><top style="thin"><color indexed="64"/></top><bottom style="thin"><color indexed="64"/></bottom><diagonal/></border>
          </borders>
          <cellXfs count="10">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
            <xf numFmtId="0" fontId="1" fillId="3" borderId="1" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center"/></xf>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="1" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="165" fontId="0" fillId="0" borderId="1" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="166" fontId="0" fillId="0" borderId="1" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="167" fontId="0" fillId="0" borderId="1" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="0" fontId="2" fillId="2" borderId="1" applyFont="1" applyFill="1" applyBorder="1"/>
            <xf numFmtId="0" fontId="3" fillId="0" borderId="0" applyFont="1" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf>
            <xf numFmtId="168" fontId="0" fillId="0" borderId="1" applyNumberFormat="1" applyBorder="1"/>
            <xf numFmtId="0" fontId="0" fillId="0" borderId="1" applyBorder="1" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>
          </cellXfs>
        </styleSheet>
        """;

    static readonly string SharedStrings = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <sst {Main} count="12" uniqueCount="12">
          <si><t>Sales Report – 東京</t></si>
          <si><t>Region</t></si>
          <si><t>Units</t></si>
          <si><t>Revenue</t></si>
          <si><t>Date</t></si>
          <si><t>Share</t></si>
          <si><t>North</t></si>
          <si><t>South</t></si>
          <si><t>Hidden row secret</t></si>
          <si><t>Hidden column secret</t></si>
          <si><r><rPr><b/><sz val="11"/><color rgb="FF00B050"/><rFont val="Calibri"/></rPr><t>Rich bold</t></r><r><t xml:space="preserve"> and normal</t></r></si>
          <si><t>This long note wraps inside its cell and keeps going for a while.</t></si>
        </sst>
        """;

    static readonly string SalesSheet = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet {Main}>
          <dimension ref="A1:F8"/>
          <sheetFormatPr defaultRowHeight="15"/>
          <cols>
            <col min="1" max="1" width="30" customWidth="1"/>
            <col min="2" max="2" width="22" customWidth="1"/>
            <col min="3" max="3" width="14" customWidth="1"/>
            <col min="4" max="4" width="16" customWidth="1"/>
            <col min="5" max="5" width="10" customWidth="1"/>
            <col min="6" max="6" width="20" hidden="1" customWidth="1"/>
          </cols>
          <sheetData>
            <row r="1" ht="24" customHeight="1"><c r="A1" s="7" t="s"><v>0</v></c><c r="B1" s="7"/><c r="C1" s="7"/><c r="D1" s="7"/><c r="E1" s="7"/></row>
            <row r="3"><c r="A3" s="1" t="s"><v>1</v></c><c r="B3" s="1" t="s"><v>2</v></c><c r="C3" s="1" t="s"><v>3</v></c><c r="D3" s="1" t="s"><v>4</v></c><c r="E3" s="1" t="s"><v>5</v></c></row>
            <row r="4"><c r="A4" t="s"><v>6</v></c><c r="B4" s="2"><v>1234.5</v></c><c r="C4" s="4"><v>99</v></c><c r="D4" s="3"><v>{March14.ToString(CultureInfo.InvariantCulture)}</v></c><c r="E4" s="5"><v>0.125</v></c><c r="F4" t="s"><v>9</v></c></row>
            <row r="5"><c r="A5" t="s"><v>7</v></c><c r="B5" s="8"><v>-4321</v></c><c r="C5" s="4"><f>C4*2</f><v>198</v></c><c r="D5" s="3"><v>{(March14 + 1).ToString(CultureInfo.InvariantCulture)}</v></c><c r="E5" s="5"><v>0.875</v></c></row>
            <row r="6" hidden="1"><c r="A6" t="s"><v>8</v></c></row>
            <row r="7"><c r="A7" s="6" t="inlineStr"><is><t>{Unicode}</t></is></c><c r="B7" t="s"><v>10</v></c><c r="C7" t="b"><v>1</v></c></row>
            <row r="8"><c r="A8" s="9" t="s"><v>11</v></c><c r="B8" t="e"><v>#DIV/0!</v></c></row>
          </sheetData>
          <mergeCells count="1"><mergeCell ref="A1:E1"/></mergeCells>
          <hyperlinks><hyperlink ref="A4" r:id="rIdLink1"/></hyperlinks>
          <pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/>
          <pageSetup paperSize="9" orientation="portrait"/>
          <headerFooter><oddFooter>&amp;LConfidential&amp;CPage &amp;P of &amp;N&amp;R&amp;A</oddFooter></headerFooter>
        </worksheet>
        """;

    static string WideSheet()
    {
        var rows = new StringBuilder();
        for (var r = 1; r <= 5; r++)
        {
            rows.Append($"<row r=\"{r}\">");
            for (var c = 1; c <= 30; c++)
                rows.Append($"<c r=\"{Column(c)}{r}\" t=\"inlineStr\"><is><t>R{r}C{c}</t></is></c>");
            rows.Append("</row>");
        }
        return SimpleSheet(rows.ToString());
    }

    static string Column(int index)
    {
        var name = "";
        for (; index > 0; index = (index - 1) / 26)
            name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    static string SimpleSheet(string rows) => SimpleSheet("", rows);

    static string SimpleSheet(string columns, string rows) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <worksheet {Main}>{columns}<sheetData>{rows}</sheetData></worksheet>
        """;
}
