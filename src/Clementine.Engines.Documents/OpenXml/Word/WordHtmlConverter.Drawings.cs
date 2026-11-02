using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using static Clementine.Engines.Documents.OpenXml.Ooxml;

namespace Clementine.Engines.Documents.OpenXml.Word;

internal sealed partial class WordHtmlConverter
{
    readonly Dictionary<string, string?> _imageCache = new(StringComparer.Ordinal);
    long _imageBytes;

    /// <summary>How an anchored (floating) object sits: inline, floated left/right, on its own line, or positioned.</summary>
    enum Placement
    {
        Inline,
        Float,
        Block,
        Absolute,
    }

    /// <summary>A DrawingML object: a picture, a text box, a group of them, or a chart (left out).</summary>
    void RenderDrawing(OpenXmlElement drawing, InlineWriter writer, Scope scope)
    {
        var frame = drawing.Child("inline") ?? drawing.Child("anchor");
        if (frame is null)
            return;
        var properties = frame.Child("docPr");
        if (IsTrue(properties.Attr("hidden")))
            return;
        var width = Emu(frame.Child("extent").Attr("cx")) ?? 0;
        var height = Emu(frame.Child("extent").Attr("cy")) ?? 0;
        var alt = properties.Attr("descr") ?? properties.Attr("title") ?? "";
        var (placement, placementCss) = frame.LocalName == "anchor" ? Anchor(frame, width, scope) : (Placement.Inline, "");
        if (placement == Placement.Absolute)
            writer.NeedsPositioning = true;

        var graphic = frame.Find("graphicData");
        var uri = graphic.Attr("uri") ?? "";
        if (uri.EndsWith("/picture", StringComparison.Ordinal))
        {
            if (PictureHtml(graphic.Find("pic"), width, height, alt, placementCss, scope) is { } picture)
                writer.Raw(picture, visible: true);
            return;
        }
        if (uri.Contains("wordprocessingShape", StringComparison.Ordinal))
        {
            Shape(graphic.Child("wsp") ?? graphic.Find("wsp"), width, height, placement, placementCss, writer, scope);
            return;
        }
        if (uri.Contains("wordprocessingGroup", StringComparison.Ordinal) || uri.Contains("wordprocessingCanvas", StringComparison.Ordinal))
        {
            // Groups lose their arrangement: their pictures and text boxes are shown one after another.
            foreach (var item in graphic!.Descendants().Where(d => d.LocalName is "pic" or "wsp").ToList())
            {
                var extent = item.Find("xfrm").Child("ext");
                var itemWidth = Emu(extent.Attr("cx")) ?? width;
                var itemHeight = Emu(extent.Attr("cy")) ?? height;
                if (item.LocalName == "pic")
                {
                    if (PictureHtml(item, itemWidth, itemHeight, alt, "", scope) is { } picture)
                        writer.Raw(picture, visible: true);
                }
                else
                {
                    Shape(item, itemWidth, itemHeight, Placement.Block, "", writer, scope);
                }
            }
            Warn("Grouped shapes were simplified: their pictures and text are shown one after another.");
            return;
        }
        if (uri.Contains("chart", StringComparison.OrdinalIgnoreCase) || uri.Contains("diagram", StringComparison.OrdinalIgnoreCase))
        {
            Warn("Charts and SmartArt graphics were left out (an empty box marks where they were).");
            var size = FitSize(width, height, scope.Width);
            writer.Raw($"<span class=\"chart\" style=\"width: {Pt(size.Width)}; height: {Pt(size.Height)}; {placementCss}\"></span>", visible: true);
        }
    }

    /// <summary>A DrawingML shape: its text box content (written before the paragraph), or a picture fill.</summary>
    void Shape(OpenXmlElement? shape, double width, double height, Placement placement, string placementCss, InlineWriter writer, Scope scope)
    {
        if (shape is null)
            return;
        var content = shape.Find("txbxContent");
        var shapeProperties = shape.Child("spPr");
        if (content is null)
        {
            if (shape.Child("blipFill") is not null && PictureHtml(shape, width, height, "", placementCss, scope) is { } picture)
                writer.Raw(picture, visible: true);
            return;
        }
        var css = new StringBuilder();
        var size = FitSize(width, height, scope.Width);
        css.Append("width: ").Append(Pt(size.Width)).Append("; ");
        if (DrawingColor(shapeProperties.Child("solidFill")) is { } background)
            css.Append("background-color: ").Append(background).Append("; ");
        var line = shapeProperties.Child("ln");
        if (line is not null && line.Child("noFill") is null && DrawingColor(line.Child("solidFill")) is { } stroke)
            css.Append("border: ").Append(Pt(Math.Max(0.5, Emu(line.Attr("w")) ?? 0.75))).Append(" solid ").Append(stroke).Append("; ");
        css.Append(placement == Placement.Inline ? "display: inline-block; vertical-align: top; " : placementCss);
        var inner = new StringBuilder();
        RenderBlocks(content.ChildElements, inner, new Scope(scope.Story, Math.Max(36, size.Width - 14.4)));
        // "In front of / behind text" boxes hang off a zero-height anchor so they don't push the text down.
        var anchored = placement == Placement.Absolute;
        if (anchored)
            writer.Before.Append("<div style=\"position: relative; height: 0; overflow: visible;\">");
        writer.Before.Append("<div class=\"tbox\" style=\"").Append(css.ToString().Trim()).Append("\">").Append(inner).Append("</div>");
        writer.Before.Append(anchored ? "</div>\n" : "\n");
    }

    /// <summary>A DrawingML colour: srgbClr, or a theme colour with its luminance adjustments.</summary>
    string? DrawingColor(OpenXmlElement? fill)
    {
        if (fill is null)
            return null;
        if (fill.Child("srgbClr") is { } rgb)
            return OfficeTheme.Hex(rgb.Attr("val"));
        if (fill.Child("schemeClr") is { } scheme && _theme.Color(scheme.Attr("val") ?? "") is { } hex)
        {
            var color = "#" + hex;
            var lumMod = Number(scheme.Child("lumMod").Attr("val")) / 100000;
            var lumOff = Number(scheme.Child("lumOff").Attr("val")) / 100000;
            if (lumOff is > 0)
                color = OfficeTheme.Tint(color, lumOff.Value);
            else if (lumMod is > 0 and < 1)
                color = OfficeTheme.Tint(color, lumMod.Value - 1);
            return color;
        }
        return null;
    }

    /// <summary>Placement of an anchored object (float / own line / positioned) from its wrapping and position.</summary>
    static (Placement, string) Anchor(OpenXmlElement anchor, double width, Scope scope)
    {
        var wrap = anchor.ChildElements.FirstOrDefault(c => c.LocalName.StartsWith("wrap", StringComparison.Ordinal))?.LocalName ?? "wrapNone";
        var horizontal = anchor.Child("positionH");
        var align = horizontal.Child("align")?.InnerText.Trim();
        var offset = Emu(horizontal.Child("posOffset")?.InnerText) ?? 0;
        var vertical = anchor.Child("positionV");
        var verticalOffset = vertical.Attr("relativeFrom") is "paragraph" or "line" ? Emu(vertical.Child("posOffset")?.InnerText) ?? 0 : 0;
        double Distance(string name) => Math.Clamp(Emu(anchor.Attr(name)) ?? 0, 0, 72);
        var left = align switch
        {
            "left" or "inside" => 0,
            "center" => (scope.Width - width) / 2,
            "right" or "outside" => scope.Width - width,
            _ => offset,
        };
        // Keep the object inside the text column: Edge clips anything printed into the page margins.
        left = Math.Clamp(left, 0, Math.Max(0, scope.Width - width));

        switch (wrap)
        {
            case "wrapTopAndBottom":
                return (Placement.Block, $"display: block; margin: {Pt(Distance("distT"))} 0 {Pt(Distance("distB"))} {Pt(left)}; ");
            case "wrapNone":
                var layer = IsTrue(anchor.Attr("behindDoc")) ? -1 : 2;
                return (Placement.Absolute, $"position: absolute; left: {Pt(left)}; top: {Pt(Math.Max(0, verticalOffset))}; z-index: {layer}; ");
            default:
                var right = align is "right" or "outside" || (align is null && left + width / 2 > scope.Width / 2);
                if (align == "center")
                    return (Placement.Block, $"display: block; margin: {Pt(Distance("distT"))} auto {Pt(Distance("distB"))}; ");
                return (Placement.Float, right
                    ? $"float: right; margin: {Pt(Distance("distT"))} 0 {Pt(Distance("distB"))} {Pt(Distance("distL"))}; "
                    : $"float: left; margin: {Pt(Distance("distT"))} {Pt(Distance("distR"))} {Pt(Distance("distB"))} 0; ");
        }
    }

    static (double Width, double Height) FitSize(double width, double height, double available)
    {
        if (width <= 0 || height <= 0)
            return (Math.Max(width, 1), Math.Max(height, 1));
        if (available > 36 && width > available)
            return (available, height * available / width);
        return (width, height);
    }

    /// <summary>An &lt;img&gt; for a pic:pic (or a shape with a picture fill), or null when the picture can't be shown.</summary>
    string? PictureHtml(OpenXmlElement? picture, double width, double height, string alt, string placementCss, Scope scope)
    {
        var blip = picture?.Find("blip");
        if (blip is null)
            return null;
        // Word 2016+ keeps SVG pictures with a PNG fallback; Edge shows the SVG (sharper) as an inert image.
        var svg = blip.Find("svgBlip")?.Attr("embed", R);
        var id = blip.Attr("embed", R);
        var size = FitSize(width, height, scope.Width);
        var source = (svg is not null ? ImageSource(scope.Story.Part, svg, size) : null) ?? (id is not null ? ImageSource(scope.Story.Part, id, size) : null);
        if (source is null)
        {
            if (blip.Attr("link", R) is not null)
                Warn("Linked pictures (stored outside the document) were left out.");
            return null;
        }
        return $"<img src=\"{source}\" alt=\"{Html(alt)}\" style=\"width: {Pt(size.Width)}; height: {Pt(size.Height)}; {placementCss}\">";
    }

    /// <summary>VML pictures and text boxes (documents from Word 2007 and older, OLE object previews).</summary>
    void RenderVml(OpenXmlElement container, InlineWriter writer, Scope scope)
    {
        foreach (var shape in container.Descendants().Where(d => d.LocalName is "shape" or "rect" or "roundrect" or "oval").ToList())
        {
            var style = shape.Attr("style") ?? "";
            var width = CssLength(style, "width");
            var height = CssLength(style, "height");
            if (shape.Child("imagedata") is { } image && image.Attr("id", R) is { } id)
            {
                var size = FitSize(width ?? 72, height ?? 72, scope.Width);
                if (ImageSource(scope.Story.Part, id, size) is { } source)
                {
                    var floating = style.Contains("position:absolute", StringComparison.OrdinalIgnoreCase) && style.Contains("mso-position-horizontal:right", StringComparison.OrdinalIgnoreCase)
                        ? " float: right;" : "";
                    writer.Raw($"<img src=\"{source}\" alt=\"{Html(shape.Attr("alt") ?? "")}\" style=\"width: {Pt(size.Width)}; height: {Pt(size.Height)};{floating}\">", visible: true);
                }
            }
            else if (shape.Child("textbox")?.Find("txbxContent") is { } content)
            {
                var inner = new StringBuilder();
                var boxWidth = Math.Min(width ?? scope.Width, scope.Width);
                RenderBlocks(content.ChildElements, inner, new Scope(scope.Story, Math.Max(36, boxWidth - 14.4)));
                var border = shape.Attr("stroked") is "f" or "false" ? "" : " border: 0.75pt solid #000000;";
                writer.Before.Append("<div class=\"tbox\" style=\"width: ").Append(Pt(boxWidth)).Append(';').Append(border).Append("\">").Append(inner).Append("</div>\n");
            }
            else if (shape.Attr("hr", "urn:schemas-microsoft-com:office:office") is "t" or "true")
            {
                writer.Raw("<hr style=\"border: 0; border-top: 1pt solid #a0a0a0; margin: 3pt 0;\">", visible: true);
            }
        }
    }

    /// <summary>"width:451.3pt" in a VML style attribute → points.</summary>
    static double? CssLength(string style, string property)
    {
        var match = Regex.Match(style, $@"(?:^|;)\s*{property}\s*:\s*([0-9.]+)\s*(pt|in|cm|mm|px|pc|em)?", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "in" => value * 72,
            "cm" => value * 72 / 2.54,
            "mm" => value * 72 / 25.4,
            "px" => value * 0.75,
            "pc" => value * 12,
            "em" => value * 12,
            _ => value,
        };
    }

    /// <summary>
    /// A picture part as a data: URI. PNG/JPEG/GIF/BMP/WebP/SVG are embedded as they are; EMF/WMF/TIFF are rendered to
    /// PNG with Windows GDI+. Linked (external) pictures and anything past the size budget return null.
    /// </summary>
    string? ImageSource(OpenXmlPart part, string relationshipId, (double Width, double Height) size)
    {
        OpenXmlPart? target;
        try
        {
            target = part.GetPartById(relationshipId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        if (target is not ImagePart image)
            return null;
        var contentType = image.ContentType.ToLowerInvariant();
        var vector = contentType is "image/x-emf" or "image/x-wmf" or "image/emf" or "image/wmf";
        var key = image.Uri.OriginalString + (vector ? $"@{size.Width:0}x{size.Height:0}" : "");
        if (_imageCache.TryGetValue(key, out var cached))
            return cached;

        string? result = null;
        try
        {
            byte[] bytes;
            using (var stream = image.GetStream(FileMode.Open, FileAccess.Read))
            {
                if (stream.CanSeek && _imageBytes + stream.Length > MaxImageBytes)
                {
                    Warn("Some pictures were left out because the document's pictures are very large.");
                    _imageCache[key] = null;
                    return null;
                }
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                bytes = memory.ToArray();
            }
            var type = contentType switch
            {
                "image/jpg" or "image/pjpeg" => "image/jpeg",
                "image/png" or "image/jpeg" or "image/gif" or "image/bmp" or "image/webp" or "image/svg+xml" or "image/x-icon" or "image/avif" => contentType,
                _ => null,
            };
            if (type is null)
            {
                bytes = RasterImages.ToPng(bytes, size) ?? [];
                type = "image/png";
                if (bytes.Length == 0)
                {
                    Warn("Some pictures in a format Clementine can't show (such as Windows metafiles) were left out.");
                    _imageCache[key] = null;
                    return null;
                }
            }
            _imageBytes += bytes.Length;
            result = $"data:{type};base64,{System.Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OpenXmlPackageException)
        {
            Warn("Some damaged pictures were left out.");
        }
        _imageCache[key] = result;
        return result;
    }
}
