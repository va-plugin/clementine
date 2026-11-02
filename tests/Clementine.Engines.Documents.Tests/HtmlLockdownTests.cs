using System.Text;
using Clementine.Engines.Documents.Markup;

namespace Clementine.Engines.Documents.Tests;

/// <summary>The copy of an untrusted page that Edge prints: nothing in it may reach the network or run.</summary>
public sealed class HtmlLockdownTests() : DocumentTestBase("lockdown")
{
    [Theory]
    [InlineData("//cdn.example.com/a.png", true)]
    [InlineData(@"\\attacker\share\a.png", true)]
    [InlineData(" \\/attacker/share/a.png", true)]
    [InlineData("file://attacker/share/a.png", true)]
    [InlineData("file:////attacker/share/a.png", true)]
    [InlineData("FILE://10.0.0.1/c$/a.png", true)]
    [InlineData("javascript:alert(1)", true)]
    [InlineData(" java\tscript:alert(1)", true)]
    [InlineData("file:///C:/Pages/a.png", false)]
    [InlineData("file://localhost/C:/Pages/a.png", false)]
    [InlineData("file://C:/Pages/a.png", false)]
    [InlineData("images/a.png", false)]
    [InlineData("/images/a.png", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    [InlineData("https://example.com/a.png", false)]
    [InlineData("#top", false)]
    public void Network_share_links_are_spotted(string url, bool expected) =>
        Assert.Equal(expected, HtmlLockdown.IsUnsafeUrl(url));

    [Fact]
    public void Active_content_and_share_links_are_removed()
    {
        var html = HtmlLockdown.Sanitize("""
            <!doctype html><html><head><meta charset="windows-1252"><meta http-equiv="refresh" content="0;url=//evil/x">
            <link rel="dns-prefetch" href="https://tracker.example"><link rel="stylesheet" href="//cdn.example.com/site.css">
            <style>@import url("//evil/a.css"); @import '\\evil\b.css'; body { background: url(//evil/bg.png) } h1 { background: url('pics/h.png') }</style>
            </head><body>
            <h1 onclick="steal()">Title</h1>
            <img src="//cdn.example.com/a.png" srcset="//cdn.example.com/a2.png 2x"><img src="\\attacker\share\b.png"><img src="pics/c d.png">
            <svg><image xlink:href="file://attacker/x.png"/></svg>
            <a href="//example.com/page">link</a> <a href="#notes">notes</a> <a href="javascript:steal()">js</a>
            <iframe src="other.html"></iframe><object data="x.swf"></object><script>steal()</script>
            <p style="background:url(//evil/p.png)">Body</p>
            </body></html>
            """, @"C:\Pages\Saved");

        Assert.DoesNotContain("evil", html);
        Assert.DoesNotContain("cdn.example.com", html);
        Assert.DoesNotContain("attacker", html);
        Assert.DoesNotContain("steal", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<iframe", html);
        Assert.DoesNotContain("<object", html);
        Assert.DoesNotContain("refresh", html);
        Assert.DoesNotContain("dns-prefetch", html);
        Assert.DoesNotContain("windows-1252", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("http-equiv=\"Content-Security-Policy\"", html);
        Assert.Contains("script-src 'none'", html);
        // Relative pictures resolve beside the original; in-page links stay in-page; protocol-relative links become https.
        Assert.Contains("src=\"file:///C:/Pages/Saved/pics/c%20d.png\"", html);
        Assert.Contains("url(\"file:///C:/Pages/Saved/pics/h.png\")", html);
        Assert.Contains("href=\"#notes\"", html);
        Assert.Contains("href=\"https://example.com/page\"", html);
        Assert.DoesNotContain("<base", html);
        Assert.Contains("Body", html);
    }

    [Fact]
    public void A_page_with_its_own_base_keeps_it()
    {
        var html = HtmlLockdown.Sanitize("<html><head><base href=\"file:///D:/Site/\"></head><body><img src=\"a.png\"></body></html>", @"C:\Other");
        Assert.Contains("<base href=\"file:///D:/Site/\">", html);
        Assert.Contains("src=\"a.png\"", html);
    }

    // Every case names a share; "leak" marks the part that must not survive, however the CSS spells it.
    [Theory]
    [InlineData("""body { background: url("\2f\2f evil/leak.png") }""")]
    [InlineData("""body { background: url(\2f\2f evil/leak.png) }""")]
    [InlineData("""body { background: url( '\5c\5c evil\5c share\5c leak.png' ) }""")]
    [InlineData("""body { background: url(//\65 vil/leak.png) }""")]
    [InlineData("""body { background: \75 rl(//evil/leak.png) }""")]
    [InlineData("""body { background: U\52L( "//evil/leak.png" ) }""")]
    [InlineData("""body { background: url("file:\2f\2f evil/share/leak.png") }""")]
    [InlineData("""body { background: url(fi\6c e://evil/share/leak.png) }""")]
    [InlineData("""body { background: url("file:////evil/share/leak.png") }""")]
    [InlineData("""body { background-image: image-set("//evil/leak.png" 1x, '\\\\evil\\leak2.png' 2x) }""")]
    [InlineData("""body { background-image: -webkit-image-set("\2f\2f evil/leak.png" 1x) }""")]
    [InlineData("""body { background-image: image-set(url(//evil/leak.png) 1x) }""")]
    [InlineData("""body { --bg: "//evil/leak.png"; background-image: image-set(var(--bg) 1x) }""")]
    [InlineData("""body { cursor: url(//evil/leak.cur), auto }""")]
    [InlineData("""@import "\2f\2f evil/leak.css"; p { color: red }""")]
    [InlineData("""@\69mport url(//evil/leak.css); p { color: red }""")]
    [InlineData("""@import/**/'//evil/leak.css' screen; p { color: red }""")]
    [InlineData("body { background: url(\"//evil/leak.png")]
    [InlineData("@font-face { src: url(\"//ev\\\nil/leak.woff\") }")]
    public void Css_share_links_are_caught_however_they_are_spelled(string css)
    {
        var clean = HtmlLockdown.SanitizeCss(css);
        Assert.DoesNotContain("leak", clean);
        if (css.Contains("p { color: red }"))
            Assert.Contains("p { color: red }", clean);
    }

    [Theory]
    [InlineData("""a[href^="//"]::after { content: " // " }""")]
    [InlineData("""h1 { background: url("pics/a.png"), url(pics/b.png); --sep: " / " }""")]
    [InlineData("""q::before { content: "\201C" } h1 { background-image: image-set("pics/a.png" 1x) }""")]
    [InlineData("""@import "local.css" print; p { color: red }""")]
    public void Ordinary_css_is_left_alone(string css) => Assert.Equal(css, HtmlLockdown.SanitizeCss(css));

    [Fact]
    public void Local_stylesheets_are_inlined_cleaned_and_everything_else_dropped()
    {
        Workspace.WriteText("site/css/more.css", """p { background: url("\2f\2f evil/leak1.png") } em { color: red }""");
        Workspace.WriteText("site/css/site.css", """
            @import "more.css" print;
            @import url(//evil/leak2.css);
            body { background: image-set("\5c\5c evil\5c leak3.png" 1x) } h1 { background: url(../pics/h.png) }
            """);
        var html = HtmlLockdown.Sanitize("""
            <html><head>
            <link rel="stylesheet" href="css/site.css" media="screen">
            <link rel="stylesheet" href="css/missing.css">
            <link rel="stylesheet" href="file://evil/share/leak4.css">
            <link rel="stylesheet" href="https://example.com/leak5.css">
            <link rel="alternate stylesheet" href="css/site.css" title="Alt">
            <style>@import "css/more.css"; @import "https://example.com/leak6.css";</style>
            </head><body><h1>Title</h1></body></html>
            """, Path.Combine(Workspace.Root, "site"));

        Assert.DoesNotContain("leak", html);
        Assert.DoesNotContain("evil", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("@import", html);
        Assert.Contains("<style media=\"screen\">", html);
        Assert.Contains("@media print {", html);
        Assert.Contains("em { color: red }", html);
        Assert.Contains($"url(\"{new Uri(Path.Combine(Workspace.Root, "site", "pics", "h.png")).AbsoluteUri}\")", html);
    }

    [Fact]
    public void Share_links_hidden_in_templates_noscript_svg_and_srcset_are_removed()
    {
        var html = HtmlLockdown.Sanitize("""
            <html><body>
            <div><template shadowrootmode="open"><img src="//evil/leak1.png"><style>p { background: url(//evil/leak2.png) }</style><p>Shadow</p></template></div>
            <template><img src="\\evil\leak3.png"></template>
            <noscript><p title="</noscript><img src=//evil/leak4.png>"></p></noscript>
            <svg><image href="x.png"><set attributeName="href" to="//evil/leak5.png"/></image>
              <rect filter="url(\2f\2f evil/leak6.svg#f)" fill="url(#local)"/></svg>
            <img srcset="a.png 1x,&#9;//evil/leak7.png 2x">
            <p style="background:url('\2f\2f evil/leak8.png')">Body</p>
            </body></html>
            """, @"C:\Pages");

        Assert.DoesNotContain("leak", html);
        Assert.Contains("Body", html);
        Assert.Contains("fill=\"url(#local)\"", html);
    }

    [Fact]
    public void Stylesheet_text_cannot_close_its_own_style_element()
    {
        var html = HtmlLockdown.Sanitize("""
            <html><body><svg><style>&lt;/style&gt;&lt;img src="//evil/x.png"&gt;</style></svg><p>Body</p></body></html>
            """, @"C:\Pages");

        // Read back the way Edge reads it (scripting on): no picture may come out of the stylesheet.
        var reparsed = new AngleSharp.Html.Parser.HtmlParser(new AngleSharp.Html.Parser.HtmlParserOptions { IsScripting = true }).ParseDocument(html);
        Assert.Empty(reparsed.QuerySelectorAll("img"));
        Assert.Contains("Body", reparsed.Body!.TextContent);
    }

    [Fact]
    public void Declared_legacy_charsets_are_honoured()
    {
        var path = Workspace.Path("legacy.html");
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("<html><head><meta charset=\"windows-1252\"><title>t</title></head><body><p>caf"));
        bytes.Add(0xE9); // é in Windows-1252
        bytes.AddRange(Encoding.ASCII.GetBytes(" na"));
        bytes.Add(0xEF); // ï
        bytes.AddRange(Encoding.ASCII.GetBytes("ve</p></body></html>"));
        File.WriteAllBytes(path, [.. bytes]);

        var html = HtmlLockdown.ForPrinting(path);
        Assert.Contains("café naïve", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
    }
}
