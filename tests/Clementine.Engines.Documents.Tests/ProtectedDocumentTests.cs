using System.IO.Compression;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Documents.Office;
using Clementine.Engines.Documents.Tests.Fixtures;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>Password-protected Office files are recognised before LibreOffice is ever started.</summary>
public sealed class ProtectedDocumentTests() : DocumentTestBase("protected")
{
    [Fact]
    public void Encrypted_word_excel_and_powerpoint_files_are_recognised()
    {
        Assert.True(ProtectedDocuments.IsProtected(EncryptedOfficeFixtures.Docx(Workspace.Path("secret.docx"))));
        Assert.True(ProtectedDocuments.IsProtected(EncryptedOfficeFixtures.Xlsx(Workspace.Path("secret.xlsx"))));
        Assert.True(ProtectedDocuments.IsProtected(EncryptedOfficeFixtures.Pptx(Workspace.Path("secret.pptx"))));
    }

    [Fact]
    public void Ordinary_and_mislabelled_files_are_not_flagged()
    {
        Assert.False(ProtectedDocuments.IsProtected(Docx(Workspace.Path("plain.docx"))));
        Assert.False(ProtectedDocuments.IsProtected(Workspace.WriteText("garbage.docx", "not a document")));
        Assert.False(ProtectedDocuments.IsProtected(Workspace.WriteText("empty.odt", "")));

        // An old binary .doc renamed to .docx is an OLE file too, but without the encryption streams: LibreOffice can read it.
        var bytes = File.ReadAllBytes(EncryptedOfficeFixtures.Docx(Workspace.Path("source.docx")));
        Rename(bytes, "EncryptedPackage", "WordDocumentXXXX");
        Rename(bytes, "EncryptionInfo", "SummaryInfoXXX");
        var renamed = Workspace.Path("old word.docx");
        File.WriteAllBytes(renamed, bytes);
        Assert.False(ProtectedDocuments.IsProtected(renamed));
    }

    [Fact]
    public void Encrypted_opendocument_files_are_recognised()
    {
        Assert.True(ProtectedDocuments.IsProtected(Odf(Workspace.Path("secret.odt"), encrypted: true)));
        Assert.False(ProtectedDocuments.IsProtected(Odf(Workspace.Path("open.odt"), encrypted: false)));
    }

    [LibreOfficeFact]
    public async Task Libreoffice_is_not_started_for_protected_files()
    {
        var input = Odf(Workspace.Path("secret.odt"), encrypted: true);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("doc.pdf.lo", input));
        Assert.Equal(ProtectedDocuments.Message, error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed}.");
        Assert.Empty(Directory.GetDirectories(Workspace.Root, "lo-*", SearchOption.AllDirectories));
    }

    static void Rename(byte[] bytes, string from, string to)
    {
        var needle = Encoding.Unicode.GetBytes(from);
        var replacement = Encoding.Unicode.GetBytes(to);
        var index = bytes.AsSpan().IndexOf(needle);
        Assert.True(index >= 0, $"{from} not found");
        replacement.CopyTo(bytes, index);
    }

    static string Odf(string path, bool encrypted)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "mimetype", "application/vnd.oasis.opendocument.text");
        var encryption = encrypted
            ? """<manifest:encryption-data manifest:checksum-type="SHA1/1K" manifest:checksum="AAAA"><manifest:algorithm manifest:algorithm-name="Blowfish CFB" manifest:initialisation-vector="AAAA"/></manifest:encryption-data>"""
            : "";
        Write(zip, "META-INF/manifest.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
              <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/>
              <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml">{encryption}</manifest:file-entry>
            </manifest:manifest>
            """);
        Write(zip, "content.xml", "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\"/>");
        return path;
    }

    static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
