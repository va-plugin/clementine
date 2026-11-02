using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Clementine.Engines.Documents.OpenXml;

/// <summary>Plain text of a PowerPoint deck: every slide in order, its text boxes and tables, then speaker notes.</summary>
internal static class PptxText
{
    public static string Extract(string path, CancellationToken cancellationToken)
    {
        using var document = OpenXmlFiles.OpenPresentation(path);
        try
        {
            return Extract(document, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationException or OperationCanceledException))
        {
            // A damaged part, another Office file renamed to .pptx…: never the SDK's own message.
            throw new OperationException($"Couldn't read \"{Path.GetFileName(path)}\" — it's damaged or isn't really a PowerPoint presentation.", ex.ToString(), ex);
        }
    }

    static string Extract(PresentationDocument document, CancellationToken cancellationToken)
    {
        var presentationPart = document.PresentationPart
            ?? throw new OperationException("This presentation has no slides.");
        var slideIds = presentationPart.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];
        if (slideIds.Count == 0)
            throw new OperationException("This presentation has no slides.");

        var text = new StringBuilder();
        var number = 0;
        foreach (var slideId in slideIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            if (slideId.RelationshipId?.Value is not { } relationship || presentationPart.GetPartById(relationship) is not SlidePart slide)
                continue;
            if (text.Length > 0)
                text.Append('\n');
            var heading = $"Slide {number}";
            text.Append(heading).Append('\n').Append(new string('-', heading.Length)).Append('\n');
            AppendShapes(slide.Slide?.CommonSlideData?.ShapeTree, text);

            var notes = NotesText(slide);
            if (notes.Length > 0)
                text.Append("Notes: ").Append(notes).Append('\n');
        }
        return text.ToString();
    }

    static void AppendShapes(OpenXmlElement? tree, StringBuilder text)
    {
        if (tree is null)
            return;
        foreach (var element in tree.ChildElements)
        {
            switch (element)
            {
                case P.Shape shape when shape.TextBody is { } body:
                    foreach (var paragraph in body.Elements<A.Paragraph>())
                    {
                        var line = ParagraphText(paragraph);
                        if (line.Trim().Length > 0)
                            text.Append(line).Append('\n');
                    }
                    break;
                case P.GraphicFrame frame:
                    foreach (var row in frame.Descendants<A.TableRow>())
                    {
                        var cells = row.Elements<A.TableCell>().Select(c => string.Join(" ", c.Descendants<A.Paragraph>().Select(ParagraphText)).Trim());
                        text.Append(string.Join('\t', cells)).Append('\n');
                    }
                    break;
                case P.GroupShape group:
                    AppendShapes(group, text);
                    break;
            }
        }
    }

    static string NotesText(SlidePart slide)
    {
        var shapes = slide.NotesSlidePart?.NotesSlide?.CommonSlideData?.ShapeTree?.Elements<P.Shape>();
        if (shapes is null)
            return "";
        var lines = new List<string>();
        foreach (var shape in shapes)
        {
            var placeholder = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value;
            if (placeholder is not null && placeholder != P.PlaceholderValues.Body)
                continue; // slide image, slide number, header…
            if (shape.TextBody is null)
                continue;
            lines.AddRange(shape.TextBody.Elements<A.Paragraph>().Select(ParagraphText).Where(l => l.Trim().Length > 0));
        }
        return string.Join(" ", lines).Trim();
    }

    static string ParagraphText(A.Paragraph paragraph)
    {
        var text = new StringBuilder();
        foreach (var child in paragraph.ChildElements)
        {
            switch (child)
            {
                case A.Run run:
                    text.Append(run.Text?.Text);
                    break;
                case A.Break:
                    text.Append('\n');
                    break;
                case A.Field field:
                    text.Append(field.Text?.Text);
                    break;
            }
        }
        return text.ToString();
    }
}
