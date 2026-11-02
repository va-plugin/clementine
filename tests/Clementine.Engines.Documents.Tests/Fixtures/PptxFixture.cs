using System.Net;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Clementine.Engines.Documents.Tests.Fixtures;

/// <summary>The smallest PowerPoint package PowerPoint and LibreOffice accept: theme, master, layout and text slides (16:9).</summary>
internal static class PptxFixture
{
    const string Namespaces = """xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" """;

    public static string Create(string path, params (string Title, string Body)[] slides)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = document.AddPresentationPart();
        var master = presentation.AddNewPart<SlideMasterPart>("rId1");
        var layout = master.AddNewPart<SlideLayoutPart>("rId1");
        var theme = master.AddNewPart<ThemePart>("rId2");
        layout.AddPart(master, "rId1");
        presentation.AddPart(theme, "rIdTheme");

        Feed(theme, Theme);
        Feed(master, $"""
            <p:sldMaster {Namespaces}><p:cSld><p:spTree>{GroupHeader}</p:spTree></p:cSld>
            <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
            <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst></p:sldMaster>
            """);
        Feed(layout, $"""
            <p:sldLayout {Namespaces} type="blank" preserve="1"><p:cSld name="Blank"><p:spTree>{GroupHeader}</p:spTree></p:cSld>
            <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>
            """);

        var slideIds = new StringBuilder();
        for (var i = 0; i < slides.Length; i++)
        {
            var relationship = $"rId{i + 10}";
            var slide = presentation.AddNewPart<SlidePart>(relationship);
            slide.AddPart(layout, "rId1");
            Feed(slide, $"""
                <p:sld {Namespaces}><p:cSld><p:spTree>{GroupHeader}
                {TextBox(2, "Title", 457200, 274638, 4000, slides[i].Title)}
                {TextBox(3, "Body", 457200, 1600200, 2400, slides[i].Body)}
                </p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>
                """);
            slideIds.Append($"""<p:sldId id="{256 + i}" r:id="{relationship}"/>""");
        }

        Feed(presentation, $"""
            <p:presentation {Namespaces}><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst>
            <p:sldIdLst>{slideIds}</p:sldIdLst><p:sldSz cx="12192000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>
            """);
        return path;
    }

    const string GroupHeader = """<p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>""";

    static string TextBox(int id, string name, long x, long y, int size, string text) => $"""
        <p:sp><p:nvSpPr><p:cNvPr id="{id}" name="{name}"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
        <p:spPr><a:xfrm><a:off x="{x}" y="{y}"/><a:ext cx="11277600" cy="1143000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
        <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US" sz="{size}"/><a:t>{WebUtility.HtmlEncode(text)}</a:t></a:r></a:p></p:txBody></p:sp>
        """;

    static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }

    const string Theme = """
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Fixture"><a:themeElements>
        <a:clrScheme name="Fixture"><a:dk1><a:srgbClr val="000000"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="1F497D"/></a:dk2><a:lt2><a:srgbClr val="EEECE1"/></a:lt2>
        <a:accent1><a:srgbClr val="4F81BD"/></a:accent1><a:accent2><a:srgbClr val="C0504D"/></a:accent2><a:accent3><a:srgbClr val="9BBB59"/></a:accent3><a:accent4><a:srgbClr val="8064A2"/></a:accent4>
        <a:accent5><a:srgbClr val="4BACC6"/></a:accent5><a:accent6><a:srgbClr val="F79646"/></a:accent6><a:hlink><a:srgbClr val="0000FF"/></a:hlink><a:folHlink><a:srgbClr val="800080"/></a:folHlink></a:clrScheme>
        <a:fontScheme name="Fixture"><a:majorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme>
        <a:fmtScheme name="Fixture">
        <a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst>
        <a:lnStyleLst><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="25400"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="38100"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
        <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
        <a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst>
        </a:fmtScheme></a:themeElements></a:theme>
        """;
}
