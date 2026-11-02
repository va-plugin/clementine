using System.Text;
using Clementine.Engines.Documents.Spreadsheets;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using static Clementine.Engines.Documents.Tests.Fixtures.DocumentFixtures;

namespace Clementine.Engines.Documents.Tests;

public sealed class SpreadsheetTests() : DocumentTestBase("sheets")
{
    [Fact]
    public async Task Csv_to_xlsx_types_numbers_and_back_round_trips()
    {
        var csv = "Name,Amount,Zip,Note\r\n\"Smith, Jane\",12.5,00123,\"said \"\"hi\"\"\nthen left\"\r\nÜnïcødé,-3,90210,東京\r\n";
        var input = Workspace.WriteText("people.csv", csv);
        var xlsx = Single(await RunAsync("sheet.xlsx", input));

        using (var document = SpreadsheetDocument.Open(xlsx, false))
        {
            var sheet = document.WorkbookPart!.WorksheetParts.Single().Worksheet!;
            var cells = sheet.Descendants<Cell>().ToDictionary(c => c.CellReference!.Value!);
            Assert.Null(cells["B2"].DataType); // numeric
            Assert.Equal("12.5", cells["B2"].CellValue!.Text);
            Assert.Equal(CellValues.SharedString, cells["C2"].DataType!.Value); // "00123" stays text
            Assert.NotNull(sheet.Descendants<Pane>().SingleOrDefault()); // header frozen
        }

        var back = Single(await RunAsync("sheet.csv", xlsx));
        var bytes = await File.ReadAllBytesAsync(back);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(csv, Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Semicolon_and_tab_delimiters_are_detected()
    {
        var semicolon = Workspace.WriteText("euro.csv", "a;b;c\n1;2;3\n4;5;6\n");
        var tsv = Workspace.WriteText("data.tsv", "x\ty\n1\t2\n");
        var fromSemicolon = Single(await RunAsync("sheet.csv", Single(await RunAsync("sheet.xlsx", semicolon))));
        var fromTsv = Single(await RunAsync("sheet.csv", Single(await RunAsync("sheet.xlsx", tsv))));

        Assert.Equal("a,b,c\r\n1,2,3\r\n4,5,6\r\n", (await File.ReadAllTextAsync(fromSemicolon)).TrimStart('﻿'));
        Assert.Equal("x,y\r\n1,2\r\n", (await File.ReadAllTextAsync(fromTsv)).TrimStart('﻿'));
    }

    [Fact]
    public async Task Multi_sheet_workbook_becomes_a_folder_with_typed_values()
    {
        var input = Xlsx(Workspace.Path("book.xlsx"),
            ("Sales", [["Date", "Units", "Paid"], [new DateTime(2026, 3, 14), 42, true], [new DateTime(2026, 3, 15), 0.1 + 0.2, false]]),
            ("Notes", [["Ünïcødé – 東京", null, "gap"]]));
        var folder = Single(await RunAsync("sheet.csv", input));

        Assert.Equal("book (CSV)", Path.GetFileName(folder));
        Assert.Equal("Date,Units,Paid\r\n2026-03-14,42,TRUE\r\n2026-03-15,0.3,FALSE\r\n", (await File.ReadAllTextAsync(Path.Combine(folder, "Sales.csv"))).TrimStart('﻿'));
        Assert.Equal("Ünïcødé – 東京,,gap\r\n", (await File.ReadAllTextAsync(Path.Combine(folder, "Notes.csv"))).TrimStart('﻿'));
    }

    [Fact]
    public async Task Single_sheet_workbook_becomes_one_csv()
    {
        var input = Xlsx(Workspace.Path("one.xlsx"), ("Only", [["a", 1]]));
        var output = Single(await RunAsync("sheet.csv", input));
        Assert.Equal("one.csv", Path.GetFileName(output));
    }

    [Theory]
    [InlineData("42", true)]
    [InlineData("-3.5", true)]
    [InlineData("1e3", true)]
    [InlineData("00123", false)]
    [InlineData("1234567890123456789", false)]
    [InlineData("1,000", false)]
    [InlineData(" 7", false)]
    [InlineData("0.5", true)]
    public void Only_round_trippable_numbers_become_numeric_cells(string text, bool numeric) =>
        Assert.Equal(numeric, XlsxWriter.TryNumber(text, out _));

    [Fact]
    public void Csv_parser_handles_quotes_and_newlines()
    {
        var rows = Csv.Parse("a,\"b,c\",\"d\"\"e\"\n\"multi\nline\",x\n", ',').ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b,c", "d\"e" }, rows[0]);
        Assert.Equal(new[] { "multi\nline", "x" }, rows[1]);
    }
}
