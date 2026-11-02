using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>Plain text of a Word document: paragraphs, list markers, tables (tab-separated), headers excluded.</summary>
internal static class DocxText
{
    public static string Extract(string path, CancellationToken cancellationToken)
    {
        using var document = OpenXmlFiles.OpenWord(path);
        try
        {
            return Extract(document, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationException or OperationCanceledException))
        {
            // Invalid XML in a part, a workbook renamed to .docx…: never the SDK's own message.
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it's damaged or isn't really a Word document.", ex.ToString(), ex);
        }
    }

    static string Extract(WordprocessingDocument document, CancellationToken cancellationToken)
    {
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new OperationException("This Word document has no body text.");
        var numbering = new ListNumbering(document.MainDocumentPart!.NumberingDefinitionsPart?.Numbering);
        var text = new StringBuilder();
        foreach (var element in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendBlock(element, text, numbering);
        }
        AppendNotes(document.MainDocumentPart.FootnotesPart?.Footnotes, "Footnotes", text);
        return text.ToString().TrimEnd() + "\n";
    }

    static void AppendBlock(OpenXmlElement element, StringBuilder text, ListNumbering numbering)
    {
        switch (element)
        {
            case Paragraph paragraph:
                text.Append(numbering.Marker(paragraph)).Append(ParagraphText(paragraph)).Append('\n');
                break;
            case Table table:
                foreach (var row in table.Elements<TableRow>())
                {
                    var cells = row.Elements<TableCell>().Select(c => string.Join(" ", c.Elements<Paragraph>().Select(ParagraphText)).Trim());
                    text.Append(string.Join('\t', cells)).Append('\n');
                }
                break;
            case SdtBlock sdt when sdt.SdtContentBlock is { } content:
                foreach (var child in content.ChildElements)
                    AppendBlock(child, text, numbering);
                break;
        }
    }

    /// <summary>Text of one paragraph: runs, tabs, breaks; deleted revisions and field codes are skipped.</summary>
    public static string ParagraphText(Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            switch (node)
            {
                case Text t when t.Parent is Run:
                    text.Append(t.Text);
                    break;
                case TabChar or PositionalTab:
                    text.Append('\t');
                    break;
                case Break br when br.Type is null || br.Type.Value == BreakValues.TextWrapping:
                    text.Append('\n');
                    break;
                case CarriageReturn:
                    text.Append('\n');
                    break;
                case NoBreakHyphen:
                    text.Append('-');
                    break;
            }
        }
        return text.ToString();
    }

    static void AppendNotes(Footnotes? notes, string heading, StringBuilder text)
    {
        var bodies = notes?.Elements<Footnote>()
            .Where(n => n.Type is null || n.Type.Value == FootnoteEndnoteValues.Normal)
            .Select(n => string.Join(" ", n.Elements<Paragraph>().Select(ParagraphText)).Trim())
            .Where(t => t.Length > 0)
            .ToList();
        if (bodies is not { Count: > 0 })
            return;
        text.Append('\n').Append(heading).Append('\n');
        for (var i = 0; i < bodies.Count; i++)
            text.Append(i + 1).Append(". ").Append(bodies[i]).Append('\n');
    }

    /// <summary>Computes list markers ("1.", "a.", "•") the way Word displays them, per numbering instance and level.</summary>
    sealed class ListNumbering(Numbering? numbering)
    {
        readonly Dictionary<(int NumId, int Level), int> _counters = [];

        public string Marker(Paragraph paragraph)
        {
            var properties = paragraph.ParagraphProperties?.NumberingProperties;
            if (properties?.NumberingId?.Val is not { } numIdValue || numbering is null)
                return "";
            int numId = numIdValue;
            if (numId == 0)
                return "";
            var level = properties.NumberingLevelReference?.Val?.Value ?? 0;
            var format = FormatOf(numId, level);

            foreach (var key in _counters.Keys.Where(k => k.NumId == numId && k.Level > level).ToList())
                _counters.Remove(key);
            var count = _counters.GetValueOrDefault((numId, level)) + 1;
            _counters[(numId, level)] = count;

            var indent = new string(' ', level * 2);
            var marker = format switch
            {
                "decimal" or "decimalZero" => $"{count}.",
                "lowerLetter" => $"{Letters(count).ToLowerInvariant()}.",
                "upperLetter" => $"{Letters(count)}.",
                "lowerRoman" => $"{Roman(count).ToLowerInvariant()}.",
                "upperRoman" => $"{Roman(count)}.",
                "none" => "",
                _ => "•",
            };
            return marker.Length == 0 ? indent : $"{indent}{marker} ";
        }

        string FormatOf(int numId, int level)
        {
            var instance = numbering!.Elements<NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
            var abstractId = instance?.AbstractNumId?.Val?.Value;
            var abstractNum = numbering.Elements<AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
            var lvl = abstractNum?.Elements<Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
            return lvl?.NumberingFormat?.Val?.ToString() ?? "bullet";
        }

        static string Letters(int n)
        {
            var result = "";
            while (n > 0)
            {
                n--;
                result = (char)('A' + n % 26) + result;
                n /= 26;
            }
            return result;
        }

        static string Roman(int n)
        {
            (int Value, string Symbol)[] table =
                [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
            var result = new StringBuilder();
            foreach (var (value, symbol) in table)
            {
                while (n >= value)
                {
                    result.Append(symbol);
                    n -= value;
                }
            }
            return result.ToString();
        }
    }
}
