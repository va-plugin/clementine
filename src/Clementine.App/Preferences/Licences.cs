using System.IO;
using System.Text.Json;

namespace Clementine.App.Preferences;

/// <summary>A bundled library or program, or an external program, with its licence, for the About page.</summary>
/// <param name="IsExternal">Not included with Clementine: used when it is on this PC (LibreOffice, Edge).</param>
public sealed record ComponentLicence(string Name, string Version, string Licence, bool IsExternal = false, string? Url = null, string? Note = null);

/// <summary>
/// Third-party components for the About page and <see cref="NoticesFileName"/>: the programs release builds bundle
/// (FFmpeg and 7-Zip, separate executables in the tools folder), the bundled NuGet packages (read from
/// Clementine.deps.json, grouped into one row per library family) with their licences, and the programs Clementine
/// uses when they are installed but doesn't include (LibreOffice, Edge).
/// </summary>
public static class Licences
{
    /// <summary>Plain-text notices copied next to Clementine.exe (licences, links, what each component does).</summary>
    public const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    /// <summary>The folder next to Clementine.exe with the full licence texts (publish.ps1 fills it for release builds).</summary>
    public const string LicencesFolderName = "licenses";

    /// <summary>Magick.NET's own notice (ImageMagick and every native library it bundles), copied at build time.</summary>
    public static readonly string MagickNoticeRelativePath = Path.Combine(LicencesFolderName, "Magick.NET-Notice.txt");

    /// <summary>The FFmpeg release publish.ps1 bundles (tools\ffmpeg\bin). Tests keep this in step with publish.ps1.</summary>
    public const string BundledFfmpegVersion = "8.1.2";

    /// <summary>The 7-Zip release publish.ps1 bundles (tools\7zip\7za.exe). Tests keep this in step with publish.ps1.</summary>
    public const string BundledSevenZipVersion = "25.01";

    /// <summary>The About page's licence line for Clementine itself.</summary>
    public const string OpenSourceLine = "Clementine is free and open source under the MIT licence";

    /// <summary>Text of the link to the project on GitHub.</summary>
    public const string ProjectLinkText = "Source code on GitHub";

    /// <summary>Under the licence lists on the About page.</summary>
    public const string NoticesNote =
        "Licences, links and full texts are in " + NoticesFileName + " and the licenses folder next to Clementine.exe. "
        + "The source code of the bundled FFmpeg and 7-Zip is attached to every Clementine release on GitHub (see licenses\\FFmpeg-SOURCE.txt).";

    static readonly (string Prefix, string Licence)[] Known =
    [
        ("Magick.NET", "Apache-2.0"),
        ("AngleSharp", "MIT"),
        ("PDFsharp", "MIT"),
        ("PdfSharp", "MIT"),
        ("UglyToad.PdfPig", "Apache-2.0"),
        ("PdfPig", "Apache-2.0"),
        ("PDFtoImage", "MIT"),
        ("bblanchon.PDFium", "Apache-2.0 / BSD-3-Clause"),
        ("SkiaSharp", "MIT"),
        ("HarfBuzzSharp", "MIT"),
        ("DocumentFormat.OpenXml", "MIT"),
        ("Markdig", "BSD-2-Clause"),
        ("ClosedXML", "MIT"),
        ("ExcelNumberFormat", "MIT"),
        ("SixLabors.Fonts", "Apache-2.0"),
        ("RBush", "MIT"),
        ("SharpCompress", "MIT"),
        ("ZstdSharp", "MIT"),
        ("YamlDotNet", "MIT"),
        ("ZXing.Net", "Apache-2.0"),
        ("Velopack", "MIT"),
        ("Microsoft.", "MIT"),
        ("System.", "MIT"),
        ("Newtonsoft.Json", "MIT"),
    ];

    /// <summary>One About-page row per library family; packages are matched by id prefix.</summary>
    sealed record Family(string Name, string[] Prefixes, string Licence, string Url, string? Note = null);

    static readonly Family[] Families =
    [
        new("Magick.NET · ImageMagick", ["Magick.NET"], "Apache-2.0 · ImageMagick License", "https://github.com/dlemstra/Magick.NET",
            "Images and PSD. Its native library bundles open-source codecs under their own licences, some LGPL (libheif, libde265, libraw, glib, pango, librsvg…)."),
        new("PDFsharp", ["PDFsharp", "PdfSharp"], "MIT", "https://docs.pdfsharp.net/"),
        new("PdfPig", ["PdfPig", "UglyToad.PdfPig"], "Apache-2.0", "https://github.com/UglyToad/PdfPig"),
        new("PDFtoImage", ["PDFtoImage"], "MIT", "https://github.com/sungaila/PDFtoImage"),
        new("PDFium", ["bblanchon.PDFium"], "Apache-2.0 · BSD-3-Clause", "https://github.com/bblanchon/pdfium-binaries"),
        new("SkiaSharp · Skia", ["SkiaSharp", "HarfBuzzSharp"], "MIT · BSD-3-Clause", "https://github.com/mono/SkiaSharp"),
        new("Open XML SDK", ["DocumentFormat.OpenXml"], "MIT", "https://github.com/dotnet/Open-XML-SDK"),
        new("ClosedXML", ["ClosedXML", "ExcelNumberFormat", "SixLabors.Fonts", "RBush"], "MIT · Apache-2.0", "https://github.com/ClosedXML/ClosedXML"),
        new("Markdig", ["Markdig"], "BSD-2-Clause", "https://github.com/xoofx/markdig"),
        new("AngleSharp", ["AngleSharp"], "MIT", "https://github.com/AngleSharp/AngleSharp"),
        new("SharpCompress", ["SharpCompress", "ZstdSharp"], "MIT", "https://github.com/adamhathcock/sharpcompress"),
        new("YamlDotNet", ["YamlDotNet"], "MIT", "https://github.com/aaubry/YamlDotNet"),
        new("ZXing.Net", ["ZXing.Net"], "Apache-2.0", "https://github.com/micjahn/ZXing.Net"),
        new("Velopack", ["Velopack"], "MIT", "https://github.com/velopack/velopack",
            "The installer and updates: checks Clementine's GitHub Releases for a new version (can be turned off in Settings › General)."),
        new(".NET libraries", ["Microsoft.", "System.", "Newtonsoft.Json"], "MIT", "https://github.com/dotnet/runtime"),
    ];

    /// <summary>
    /// Programs bundled with release builds: separate executables in the tools folder that Clementine starts as processes
    /// (never linked into it), with their licences and source information in the licenses folder.
    /// </summary>
    public static readonly IReadOnlyList<ComponentLicence> BundledPrograms =
    [
        new("FFmpeg", BundledFfmpegVersion, "GPL-3.0 (build by gyan.dev)", Url: "https://ffmpeg.org/legal.html",
            Note: "Video and audio. Included as separate programs in tools\\ffmpeg. Its licence, build notes and where its source "
                + "comes from (FFmpeg-SOURCE.txt) are in the licenses folder; the FFmpeg source code is attached to every GitHub "
                + "release, and FFmpeg-SOURCE.txt has a written offer of the source of the libraries in this build."),
        new("7-Zip (7za.exe)", BundledSevenZipVersion, "LGPL-2.1 · BSD-3-Clause · BSD-2-Clause", Url: "https://www.7-zip.org/license.txt",
            Note: "Writing .7z archives. Included as a separate program in tools\\7zip; licence in licenses\\7-Zip-License.txt, "
                + "source code attached to every GitHub release."),
    ];

    /// <summary>Programs Clementine uses when they are on this PC; not included with it.</summary>
    public static readonly IReadOnlyList<ComponentLicence> ExternalPrograms =
    [
        new("LibreOffice", "optional", "MPL-2.0", IsExternal: true, Url: "https://www.libreoffice.org/about-us/licenses/",
            Note: "Optional — PowerPoint and older Office formats (.doc, .xls, .ppt, OpenDocument). Not included; install it if you need those."),
        new("Microsoft Edge", "part of Windows", "Microsoft software licence", IsExternal: true, Url: "https://www.microsoft.com/edge",
            Note: "Part of Windows. Prints HTML, Markdown, e-books and Word and Excel conversions to PDF (headless, offline)."),
    ];

    public static string LicenceFor(string packageId) =>
        Known.FirstOrDefault(k => packageId.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase)).Licence ?? "See the package's licence on nuget.org";

    /// <summary>
    /// The bundled libraries for the About page: one row per family that is actually in <paramref name="depsJsonPath"/>
    /// (version of its main package), plus a row for any package no family covers. When the deps file can't be read
    /// (e.g. a single-file build) every family is listed without a version.
    /// </summary>
    public static IReadOnlyList<ComponentLicence> BundledComponents(string? depsJsonPath = null)
    {
        var packages = BundledPackages(depsJsonPath);
        if (packages.Count == 0)
            return Families.Select(f => new ComponentLicence(f.Name, "", f.Licence, Url: f.Url, Note: f.Note)).ToList();

        var result = new List<ComponentLicence>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Families)
        {
            var members = packages.Where(p => family.Prefixes.Any(prefix => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
            if (members.Count == 0)
                continue;
            foreach (var member in members)
                covered.Add(member.Name);
            // The family's main package is the one whose id is shortest ("SkiaSharp" rather than "SkiaSharp.NativeAssets.Win32").
            var version = family.Prefixes[0] is "Microsoft." ? Environment.Version.ToString() : members.OrderBy(m => m.Name.Length).First().Version;
            result.Add(new ComponentLicence(family.Name, version, family.Licence, Url: family.Url, Note: family.Note));
        }
        foreach (var package in packages.Where(p => !covered.Contains(p.Name)))
            result.Add(package);
        return result;
    }

    /// <summary>Full path of <see cref="NoticesFileName"/> next to the app, or null when it isn't there.</summary>
    public static string? NoticesFile(string? baseDirectory = null)
    {
        var path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, NoticesFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Packages listed in the app's deps.json (empty when it can't be read).</summary>
    public static IReadOnlyList<ComponentLicence> BundledPackages(string? depsJsonPath = null)
    {
        depsJsonPath ??= Path.Combine(AppContext.BaseDirectory, "Clementine.deps.json");
        try
        {
            if (!File.Exists(depsJsonPath))
                return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(depsJsonPath));
            if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
                return [];
            var result = new List<ComponentLicence>();
            foreach (var library in libraries.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package")
                    continue;
                var slash = library.Name.IndexOf('/');
                var id = slash > 0 ? library.Name[..slash] : library.Name;
                var version = slash > 0 ? library.Name[(slash + 1)..] : "";
                if (id.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(new ComponentLicence(id, version, LicenceFor(id)));
            }
            return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
