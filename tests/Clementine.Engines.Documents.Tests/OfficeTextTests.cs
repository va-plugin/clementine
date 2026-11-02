using Clementine.Core;
using Clementine.Engines.Documents.Tests.Fixtures;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

/// <summary>Native OpenXML readers (no LibreOffice needed).</summary>
public sealed class OfficeTextTests() : DocumentTestBase("officetext")
{
    [Fact]
    public async Task Docx_to_text_has_list_markers_and_tables()
    {
        var input = Docx(Workspace.Path("report.docx"));
        var text = await File.ReadAllTextAsync(Single(await RunAsync("doc.txt", input)));

        Assert.Equal($"""
            Quarterly report
            Intro with bold text and {Unicode}.
            1. First step
            2. Second step
            • A bullet
            Name	Value
            Alpha	42

            The end.

            """.Replace("\r\n", "\n").Replace("\n", "\r\n"), text);
    }

    [Fact]
    public async Task Pptx_to_text_lists_slides_in_order()
    {
        var input = PptxFixture.Create(Workspace.Path("deck.pptx"), ("Welcome", "Hello " + Unicode), ("Agenda", "Plans for 2027"));
        var text = await File.ReadAllTextAsync(Single(await RunAsync("slides.txt", input)));

        Assert.Equal($"""
            Slide 1
            -------
            Welcome
            Hello {Unicode}

            Slide 2
            -------
            Agenda
            Plans for 2027

            """.Replace("\r\n", "\n").Replace("\n", "\r\n"), text);
    }

    [Fact]
    public async Task Password_protected_office_files_get_a_friendly_error()
    {
        var input = Workspace.Path("locked.docx");
        await File.WriteAllBytesAsync(input, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);
        var error = await Assert.ThrowsAsync<OperationException>(() => RunAsync("doc.txt", input));
        Assert.Contains("password-protected", error.Message);
    }

    [Fact]
    public void Legacy_formats_need_libreoffice_and_say_so_when_it_is_missing()
    {
        using var workspace = new Clementine.TestSupport.TestWorkspace("nolo");
        workspace.Settings.Update(s => s.LibreOfficePath = Path.Combine(workspace.Root, "missing"));
        var operation = workspace.Operation(Provider, "doc.pdf.lo");
        // On this machine LibreOffice may be installed in Program Files, so only check the reason text when unavailable.
        var availability = operation.CheckAvailability();
        if (!availability.IsAvailable)
            Assert.Equal("Needs LibreOffice (free, optional) for PowerPoint and older Office files — libreoffice.org", availability.Reason);
        Assert.Equal(OperationWeight.Heavy, operation.Info.Weight);
    }
}
