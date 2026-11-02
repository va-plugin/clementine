using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>Character formatting of a <see cref="RichRun"/>.</summary>
[Flags]
internal enum RunFormat
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strike = 8,
}

/// <summary>A piece of paragraph content: text with formatting, optionally a link; or a line break.</summary>
internal readonly record struct RichRun(string Text, RunFormat Format = RunFormat.None, string? Link = null)
{
    public static readonly RichRun Break = new("\n");

    public static RichRun Plain(string text) => new(text);
}

/// <summary>
/// Writes a clean, style-based Word document with OpenXML: real Heading 1–6 styles (so Word's navigation pane and
/// table of contents work), lists with proper numbering, code blocks, quotes, tables, links and images.
/// </summary>
internal sealed class DocxBuilder : IDisposable
{
    const long MaxImageWidthEmu = 5_943_600; // 6.5 in
    const string BulletAbstract = "0";
    const string DecimalAbstract = "1";

    readonly WordprocessingDocument _document;
    readonly MainDocumentPart _main;
    readonly Body _body;
    readonly List<(int NumId, string AbstractId, int Start)> _lists = [];
    uint _drawingId = 1;
    bool _saved;

    DocxBuilder(string path, string? title)
    {
        _document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        _main = _document.AddMainDocumentPart();
        _main.Document = new Document(new Body());
        _body = _main.Document.Body!;
        using (var styles = new MemoryStream(Encoding.UTF8.GetBytes(StylesXml)))
            _main.AddNewPart<StyleDefinitionsPart>().FeedData(styles);
        _document.PackageProperties.Title = title;
        _document.PackageProperties.Creator = "Clementine";
        _document.PackageProperties.Created = DateTime.UtcNow;
        _lists.Add((1, BulletAbstract, 1));
    }

    public static DocxBuilder Create(string path, string? title = null) => new(path, title);

    /// <summary>Numbering instance id for plain bullet lists.</summary>
    public int BulletList => 1;

    public void Heading(int level, IEnumerable<RichRun> content) =>
        Paragraph(content, $"Heading{Math.Clamp(level, 1, 6)}");

    public void Title(string text) => Paragraph([RichRun.Plain(text)], "Title");

    public void Paragraph(IEnumerable<RichRun> content, string? style = null)
    {
        var paragraph = new Paragraph();
        if (style is not null)
            paragraph.Append(new ParagraphProperties(new ParagraphStyleId { Val = style }));
        AppendInlines(paragraph, content);
        _body.Append(paragraph);
    }

    /// <summary>One line of plain text with spaces and tabs preserved and no paragraph spacing.</summary>
    public void PlainLine(string text) => Paragraph([RichRun.Plain(text)], "PlainText");

    /// <summary>Starts a new list and returns its numbering id (ordered lists restart at <paramref name="start"/>).</summary>
    public int NewList(bool ordered, int start = 1)
    {
        if (!ordered)
            return BulletList;
        var id = _lists.Count + 1;
        _lists.Add((id, DecimalAbstract, Math.Max(0, start)));
        return id;
    }

    public void ListItem(int numId, int level, IEnumerable<RichRun> content)
    {
        var paragraph = new Paragraph(new ParagraphProperties(
            new ParagraphStyleId { Val = "ListParagraph" },
            new NumberingProperties(new NumberingLevelReference { Val = Math.Clamp(level, 0, 8) }, new NumberingId { Val = numId })));
        AppendInlines(paragraph, content);
        _body.Append(paragraph);
    }

    public void CodeBlock(string code)
    {
        var lines = TextFiles.NormalizeNewlines(code).TrimEnd('\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var properties = new ParagraphProperties(new ParagraphStyleId { Val = "Code" });
            if (i == lines.Length - 1)
                properties.Append(new SpacingBetweenLines { After = "200" }); // breathing room below the block
            var paragraph = new Paragraph(properties);
            AppendInlines(paragraph, [RichRun.Plain(lines[i])]);
            _body.Append(paragraph);
        }
    }

    public void Quote(IEnumerable<RichRun> content) => Paragraph(content, "Quote");

    public void HorizontalRule() =>
        _body.Append(new Paragraph(new ParagraphProperties(new ParagraphBorders(
            new BottomBorder { Val = BorderValues.Single, Size = 6, Space = 1, Color = "BFBFBF" }))));

    public void PageBreak() => _body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

    /// <summary>A grid table; every cell is a list of inlines. The first row is styled as a header when asked.</summary>
    public void Table(IReadOnlyList<IReadOnlyList<IReadOnlyList<RichRun>>> rows, bool header)
    {
        if (rows.Count == 0)
            return;
        var columns = rows.Max(r => r.Count);
        var table = new Table(new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableLook { Val = "04A0", FirstRow = header, NoVerticalBand = true }));
        var grid = new TableGrid();
        for (var c = 0; c < columns; c++)
            grid.Append(new GridColumn());
        table.Append(grid);

        for (var r = 0; r < rows.Count; r++)
        {
            var isHeader = header && r == 0;
            var row = new TableRow();
            if (isHeader)
                row.Append(new TableRowProperties(new TableHeader()));
            for (var c = 0; c < columns; c++)
            {
                var content = c < rows[r].Count ? rows[r][c] : [];
                var paragraph = new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" }));
                AppendInlines(paragraph, isHeader ? content.Select(i => i with { Format = i.Format | RunFormat.Bold }) : content);
                var cell = new TableCell(paragraph);
                if (isHeader)
                    cell.PrependChild(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = "F2F2F2" }));
                row.Append(cell);
            }
            table.Append(row);
        }
        _body.Append(table);
        _body.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" })));
    }

    /// <summary>Embeds an image (PNG/JPEG/GIF/BMP) scaled to fit the text width.</summary>
    public void Image(byte[] data, string contentType, int widthPx, int heightPx, string? description)
    {
        if (widthPx <= 0 || heightPx <= 0)
            return;
        var partType = contentType switch
        {
            "image/jpeg" => ImagePartType.Jpeg,
            "image/gif" => ImagePartType.Gif,
            "image/bmp" => ImagePartType.Bmp,
            _ => ImagePartType.Png,
        };
        var part = _main.AddImagePart(partType);
        using (var stream = new MemoryStream(data))
            part.FeedData(stream);
        var relationshipId = _main.GetIdOfPart(part);

        long cx = widthPx * 9525L, cy = heightPx * 9525L; // 96 DPI
        if (cx > MaxImageWidthEmu)
        {
            cy = (long)(cy * (MaxImageWidthEmu / (double)cx));
            cx = MaxImageWidthEmu;
        }
        var id = _drawingId++;
        var name = $"Picture {id}";
        var drawing = new Drawing(new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = id, Name = name, Description = description ?? "" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(
                        new PIC.NonVisualDrawingProperties { Id = 0U, Name = name },
                        new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = relationshipId }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U,
        });
        _body.Append(new Paragraph(new Run(drawing)));
    }

    public void Save()
    {
        if (_saved)
            return;
        using (var numbering = new MemoryStream(Encoding.UTF8.GetBytes(NumberingXml())))
            _main.AddNewPart<NumberingDefinitionsPart>().FeedData(numbering);
        _body.Append(SectionProperties());
        _main.Document!.Save();
        _document.Dispose();
        _saved = true;
    }

    public void Dispose()
    {
        if (!_saved)
            _document.Dispose();
    }

    void AppendInlines(OpenXmlElement paragraph, IEnumerable<RichRun> content)
    {
        foreach (var inline in content)
        {
            if (inline.Text == "\n")
            {
                paragraph.Append(new Run(new Break()));
                continue;
            }
            if (inline.Text.Length == 0)
                continue;
            var run = CreateRun(inline);
            if (inline.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "file")
            {
                var relationship = _main.AddHyperlinkRelationship(uri, true);
                run.RunProperties ??= new RunProperties();
                run.RunProperties.PrependChild(new RunStyle { Val = "Hyperlink" });
                paragraph.Append(new Hyperlink(run) { Id = relationship.Id, History = true });
            }
            else
            {
                paragraph.Append(run);
            }
        }
    }

    static Run CreateRun(RichRun inline)
    {
        var run = new Run();
        var properties = new RunProperties();
        if (inline.Format.HasFlag(RunFormat.Code))
            properties.Append(new RunStyle { Val = "CodeChar" });
        if (inline.Format.HasFlag(RunFormat.Bold))
            properties.Append(new Bold());
        if (inline.Format.HasFlag(RunFormat.Italic))
            properties.Append(new Italic());
        if (inline.Format.HasFlag(RunFormat.Strike))
            properties.Append(new Strike());
        if (properties.HasChildren)
            run.Append(properties);

        var parts = inline.Text.Split('\t');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                run.Append(new TabChar());
            if (parts[i].Length > 0)
                run.Append(new Text(SanitizeXml(parts[i])) { Space = SpaceProcessingModeValues.Preserve });
        }
        return run;
    }

    /// <summary>XML 1.0 forbids most control characters; drop them rather than writing a corrupt document.</summary>
    static string SanitizeXml(string text)
    {
        if (!text.Any(c => c < 0x20 && c != '\t' || c is '￾' or '￿'))
            return text;
        return new string(text.Where(c => !(c < 0x20 && c != '\t') && c is not ('￾' or '￿')).ToArray());
    }

    static SectionProperties SectionProperties()
    {
        var metric = RegionInfo.CurrentRegion.IsMetric;
        return new SectionProperties(
            new PageSize { Width = metric ? 11906U : 12240U, Height = metric ? 16838U : 15840U },
            new PageMargin { Top = 1440, Bottom = 1440, Left = 1440U, Right = 1440U, Header = 708U, Footer = 708U, Gutter = 0U });
    }

    string NumberingXml()
    {
        var xml = new StringBuilder();
        xml.Append("<w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
        xml.Append($"<w:abstractNum w:abstractNumId=\"{BulletAbstract}\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        string[] bullets = ["•", "◦", "▪"];
        for (var level = 0; level < 9; level++)
        {
            xml.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{bullets[level % 3]}\"/>"
                + $"<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{720 + level * 360}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        }
        xml.Append("</w:abstractNum>");
        xml.Append($"<w:abstractNum w:abstractNumId=\"{DecimalAbstract}\"><w:multiLevelType w:val=\"multilevel\"/>");
        string[] formats = ["decimal", "lowerLetter", "lowerRoman"];
        for (var level = 0; level < 9; level++)
        {
            xml.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{formats[level % 3]}\"/><w:lvlText w:val=\"%{level + 1}.\"/>"
                + $"<w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{720 + level * 360}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        }
        xml.Append("</w:abstractNum>");
        foreach (var (numId, abstractId, start) in _lists)
        {
            xml.Append($"<w:num w:numId=\"{numId}\"><w:abstractNumId w:val=\"{abstractId}\"/>");
            if (abstractId == DecimalAbstract)
                xml.Append($"<w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"{start}\"/></w:lvlOverride>");
            xml.Append("</w:num>");
        }
        xml.Append("</w:numbering>");
        return xml.ToString();
    }

    const string StylesXml = """
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults>
            <w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Yu Gothic" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="en-US" w:eastAsia="ja-JP"/></w:rPr></w:rPrDefault>
            <w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault>
          </w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>
          <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:after="240"/></w:pPr><w:rPr><w:sz w:val="52"/><w:szCs w:val="52"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="360" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:color w:val="1F3864"/><w:sz w:val="36"/><w:szCs w:val="36"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="280" w:after="100"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:color w:val="1F3864"/><w:sz w:val="30"/><w:szCs w:val="30"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:color w:val="2F5496"/><w:sz w:val="26"/><w:szCs w:val="26"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading4"><w:name w:val="heading 4"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="60"/><w:outlineLvl w:val="3"/></w:pPr><w:rPr><w:b/><w:i/><w:color w:val="2F5496"/><w:sz w:val="24"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading5"><w:name w:val="heading 5"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="40"/><w:outlineLvl w:val="4"/></w:pPr><w:rPr><w:b/><w:color w:val="404040"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Heading6"><w:name w:val="heading 6"/><w:basedOn w:val="Normal"/><w:next w:val="Normal"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before="200" w:after="40"/><w:outlineLvl w:val="5"/></w:pPr><w:rPr><w:i/><w:color w:val="404040"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Quote"><w:name w:val="Quote"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:pBdr><w:left w:val="single" w:sz="18" w:space="8" w:color="D0D0D0"/></w:pBdr><w:ind w:left="567"/></w:pPr><w:rPr><w:i/><w:color w:val="595959"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="Code"><w:name w:val="Code"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:shd w:val="clear" w:color="auto" w:fill="F4F4F4"/><w:spacing w:after="0" w:line="240" w:lineRule="auto"/><w:ind w:left="113" w:right="113"/></w:pPr><w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/><w:sz w:val="19"/><w:szCs w:val="19"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="PlainText"><w:name w:val="Plain Text"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:after="0" w:line="264" w:lineRule="auto"/></w:pPr></w:style>
          <w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:qFormat/><w:pPr><w:spacing w:after="60"/><w:ind w:left="720"/><w:contextualSpacing/></w:pPr></w:style>
          <w:style w:type="character" w:default="1" w:styleId="DefaultParagraphFont"><w:name w:val="Default Paragraph Font"/><w:uiPriority w:val="1"/><w:semiHidden/></w:style>
          <w:style w:type="character" w:styleId="Hyperlink"><w:name w:val="Hyperlink"/><w:basedOn w:val="DefaultParagraphFont"/><w:rPr><w:color w:val="0563C1"/><w:u w:val="single"/></w:rPr></w:style>
          <w:style w:type="character" w:styleId="CodeChar"><w:name w:val="Code Char"/><w:basedOn w:val="DefaultParagraphFont"/><w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/><w:sz w:val="20"/><w:shd w:val="clear" w:color="auto" w:fill="F0F0F0"/></w:rPr></w:style>
          <w:style w:type="table" w:default="1" w:styleId="TableNormal"><w:name w:val="Normal Table"/><w:semiHidden/><w:tblPr><w:tblInd w:w="0" w:type="dxa"/><w:tblCellMar><w:top w:w="0" w:type="dxa"/><w:left w:w="108" w:type="dxa"/><w:bottom w:w="0" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
          <w:style w:type="table" w:styleId="TableGrid"><w:name w:val="Table Grid"/><w:basedOn w:val="TableNormal"/><w:tblPr><w:tblBorders><w:top w:val="single" w:sz="4" w:space="0" w:color="BFBFBF"/><w:left w:val="single" w:sz="4" w:space="0" w:color="BFBFBF"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="BFBFBF"/><w:right w:val="single" w:sz="4" w:space="0" w:color="BFBFBF"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="BFBFBF"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="BFBFBF"/></w:tblBorders><w:tblCellMar><w:top w:w="40" w:type="dxa"/><w:bottom w:w="40" w:type="dxa"/></w:tblCellMar></w:tblPr></w:style>
        </w:styles>
        """;
}
