using Clementine.Engines.Documents.Tests.Fixtures;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Clementine.TestSupport;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>
/// The built-in Word converter on documents another program wrote: LibreOffice's own templates (a CV, a business letter,
/// an agenda) saved as DOCX by LibreOffice, then printed without LibreOffice. Every word of the document's text must
/// reach the PDF. Skipped where LibreOffice (which makes the inputs) isn't installed.
/// </summary>
[Collection(ExternalToolsCollection.Name)]
public sealed class BuiltInRealWorldTests : IDisposable
{
    static readonly string[] Templates =
    [
        @"personal\CV.ott",
        @"officorr\Modern_business_letter_serif.ott",
        @"wizard\agenda\5blue.ott",
    ];

    readonly TestWorkspace _workspace = new("builtin-real");

    public void Dispose() => _workspace.Dispose();

    [LibreOfficeFact]
    public async Task Documents_written_by_libreoffice_print_with_all_their_text()
    {
        var soffice = LibreOfficeFactAttribute.Tools.LibreOffice!;
        var templates = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(soffice)!, "..", "share", "template", "common"));
        var withLibreOffice = new DocumentOperationProvider();
        var builtIn = new DocumentOperationProvider { IgnoreLibreOffice = true };
        var checkedAny = false;
        foreach (var template in Templates)
        {
            var source = Path.Combine(templates, template);
            if (!File.Exists(source))
                continue;
            var copy = _workspace.Path(Path.GetFileName(source));
            File.Copy(source, copy);
            var docx = Assert.Single((await _workspace.RunAsync(_workspace.Operation(withLibreOffice, "doc.docx"), [copy])).Outputs);
            // Every visible run of text (body, tables, text boxes), word by word.
            List<string> words;
            using (var document = WordprocessingDocument.Open(docx, false))
            {
                words = document.MainDocumentPart!.Document!.Body!.Descendants<Text>()
                    .Where(t => !t.Ancestors<Run>().Any(r => r.RunProperties?.Vanish is not null))
                    .SelectMany(t => t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(w => w.Length > 2)
                    .Distinct()
                    .ToList();
            }

            var pdf = Assert.Single((await _workspace.RunAsync(_workspace.Operation(builtIn, "doc.pdf"), [docx])).Outputs);
            var html = Assert.Single((await _workspace.RunAsync(_workspace.Operation(builtIn, "doc.html"), [docx])).Outputs);
            if (Environment.GetEnvironmentVariable("CLEMENTINE_LOOK_DIR") is { Length: > 0 } look)
            {
                Directory.CreateDirectory(look);
                var name = Path.GetFileNameWithoutExtension(source);
                File.Copy(pdf, Path.Combine(look, name + ".pdf"), overwrite: true);
                File.Copy(html, Path.Combine(look, name + ".html"), overwrite: true);
                File.Copy(docx, Path.Combine(look, name + ".docx"), overwrite: true);
                var pages = Assert.Single((await _workspace.RunAsync(_workspace.Operation(builtIn, "pdf.png.150"), [pdf])).Outputs);
                foreach (var png in Directory.Exists(pages) ? Directory.GetFiles(pages, "*.png") : [pages])
                    File.Copy(png, Path.Combine(look, name + " - " + Path.GetFileName(png)), overwrite: true);
            }
            Assert.True(PageCount(pdf) >= 1);
            var printed = Normalize(string.Join(' ', PageTexts(pdf))).Replace(" ", "", StringComparison.Ordinal);
            var missing = words.Where(w => !printed.Contains(w, StringComparison.Ordinal)).ToList();
            Assert.True(missing.Count == 0, $"{template}: missing from the PDF: {string.Join(", ", missing.Take(20))}");
            checkedAny = true;
        }
        Assert.True(checkedAny, "None of LibreOffice's sample templates were found.");
    }
}
