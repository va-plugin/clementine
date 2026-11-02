using System.Text;
using System.Text.Json;
using Clementine.Core;
using Clementine.Engines.Files.Data;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

public class JsonYamlTests
{
    const string TrickyJson = """
        {
          "name": "Clementine – ünïcødé 東京 🎛",
          "looksLikeBool": "yes",
          "looksLikeNull": "null",
          "looksLikeNumber": "1.0",
          "date": "2024-01-01",
          "empty": "",
          "multiline": "line one\nline two\n",
          "leadingSpace": "  indented",
          "colon": "key: value # not a comment",
          "quote": "she said \"hi\"",
          "number": 1.50,
          "exponent": 1e5,
          "big": 123456789012345678901234567890,
          "negative": -0.001,
          "flag": true,
          "nothing": null,
          "list": [1, "two", false, null, {"nested": [[]]}],
          "emptyObject": {},
          "emptyList": [],
          "tab\tkey": "x",
          "~": "tilde key"
        }
        """;

    [Fact]
    public void Json_to_yaml_to_json_keeps_types_order_and_text()
    {
        var original = JsonData.Parse(TrickyJson);
        var yaml = YamlTextWriter.Write(original);
        var (roundTripped, warnings) = YamlReader.Read(yaml);
        Assert.Empty(warnings);

        var expected = JsonTextWriter.Write(original);
        var actual = JsonTextWriter.Write(roundTripped);
        // Exponents are written YAML-1.1-safe ("1.0e+5"), which is the same number.
        Assert.Equal(expected.Replace("1e5", "1.0e+5"), actual);

        void Has(string fragment) => Assert.True(yaml.Contains(fragment, StringComparison.Ordinal), $"Missing <{fragment}> in: {yaml}");
        Has("looksLikeBool: \"yes\"");
        Has("looksLikeNumber: \"1.0\"");
        Has("date: \"2024-01-01\"");
        Has("empty: \"\"");
        Has("multiline: |");
        Has("number: 1.50");
        Has("big: 123456789012345678901234567890");
        Assert.Contains("Clementine – ünïcødé 東京 🎛", yaml);
    }

    [Fact]
    public void Yaml_core_schema_types()
    {
        var (data, _) = YamlReader.Read("""
            a: yes
            b: true
            c: ~
            d: 0x1F
            e: 0o17
            f: +.5
            g: -5.
            h: 007
            i: '123'
            j: !!str 456
            k: !!int "789"
            l: 1e3
            m:
            n: 2024-05-01
            """);
        var json = JsonTextWriter.Write(data, indent: false);
        Assert.Equal("""{"a":"yes","b":true,"c":null,"d":31,"e":15,"f":0.5,"g":-5.0,"h":7,"i":"123","j":"456","k":789,"l":1e3,"m":null,"n":"2024-05-01"}""", json);
        using var _ = JsonDocument.Parse(json); // valid JSON
    }

    [Fact]
    public void Yaml_anchors_aliases_and_merge_keys()
    {
        var (data, _) = YamlReader.Read("""
            defaults: &defaults
              adapter: postgres
              host: localhost
              pool: 5
            development:
              <<: *defaults
              database: dev
              pool: 10
            tags: &tags [a, b]
            copy: *tags
            multi:
              <<: [*defaults, {extra: 1, host: ignored}]
            """);
        var json = JsonTextWriter.Write(data, indent: false);
        Assert.Contains("\"development\":{\"adapter\":\"postgres\",\"host\":\"localhost\",\"database\":\"dev\",\"pool\":10}", json);
        Assert.Contains("\"copy\":[\"a\",\"b\"]", json);
        Assert.Contains("\"multi\":{\"adapter\":\"postgres\",\"host\":\"localhost\",\"pool\":5,\"extra\":1}", json);
    }

    [Fact]
    public void Yaml_multi_document_becomes_an_array()
    {
        var (data, warnings) = YamlReader.Read("a: 1\n---\nb: 2\n---\n");
        Assert.Equal("""[{"a":1},{"b":2},null]""", JsonTextWriter.Write(data, indent: false));
        Assert.Contains(warnings, w => w.Contains("3 documents", StringComparison.Ordinal));
    }

    [Fact]
    public void Yaml_billion_laughs_is_stopped()
    {
        var yaml = new StringBuilder("a: &a [lol, lol, lol, lol, lol, lol, lol, lol, lol]\n");
        for (var level = 'b'; level <= 'j'; level++)
        {
            var previous = (char)(level - 1);
            yaml.Append($"{level}: &{level} [{string.Join(", ", Enumerable.Repeat("*" + previous, 9))}]\n");
        }
        var (data, _) = YamlReader.Read(yaml.ToString()); // parsing shares nodes, so it's cheap
        var error = Assert.Throws<OperationException>(() => JsonTextWriter.Write(data));
        Assert.Contains("enormous", error.Message);
    }

    [Fact]
    public void Yaml_self_reference_and_undefined_aliases_are_errors()
    {
        Assert.Contains("isn't defined", Assert.Throws<OperationException>(() => YamlReader.Read("a: &x [*x]")).Message);
        Assert.Contains("isn't defined", Assert.Throws<OperationException>(() => YamlReader.Read("a: *nope")).Message);
    }

    [Fact]
    public void Deep_nesting_is_refused()
    {
        var deep = new string('[', 300) + new string(']', 300);
        Assert.Throws<OperationException>(() => JsonData.Parse(deep));
        Assert.Throws<OperationException>(() => YamlReader.Read(deep));
    }

    [Fact]
    public void Invalid_inputs_report_line_and_column()
    {
        var json = Assert.Throws<OperationException>(() => JsonData.Parse("{\n  \"a\": 1,\n  \"b\": oops\n}"));
        Assert.Contains("isn't valid JSON (line 3", json.Message);
        var unclosed = Assert.Throws<OperationException>(() => YamlReader.Read("a: [1, 2\nb: c"));
        Assert.Contains("isn't valid YAML (line", unclosed.Message);
        var badIndent = Assert.Throws<OperationException>(() => YamlReader.Read("a:\n  b: 1\n c: 2\n"));
        Assert.Contains("isn't valid YAML (line", badIndent.Message);
    }

    [Fact]
    public void Json_comments_trailing_commas_and_duplicate_keys()
    {
        var data = JsonData.Parse("{ // comment\n \"a\": 1, /* block */ \"a\": 2, \"b\": [1,2,], }");
        Assert.Equal("""{"a":1,"a":2,"b":[1,2]}""", JsonTextWriter.Write(data, indent: false));
    }

    [Fact]
    public void Json_writer_escapes_minimally()
    {
        var data = JsonData.Parse("[\"é 東京 🎛 \\u0001 \\u2028 \\\" \\\\ \\t\"]");
        Assert.Equal("[\"é 東京 🎛 \\u0001 \\u2028 \\\" \\\\ \\t\"]", JsonTextWriter.Write(data, indent: false));
        Assert.Equal("[\n  1,\n  {\n    \"a\": []\n  }\n]\n", JsonTextWriter.Write(JsonData.Parse("[1,{\"a\":[]}]")));
    }

    [Fact]
    public void Json_lines_become_an_array()
    {
        var data = JsonData.ParseFile("{\"a\":1}\n\n{\"a\":2}\r\n", ".jsonl");
        Assert.Equal("""[{"a":1},{"a":2}]""", JsonTextWriter.Write(data, indent: false));
        var error = Assert.Throws<OperationException>(() => JsonData.ParseFile("{\"a\":1}\n{oops}\n", ".ndjson"));
        Assert.Contains("line 2", error.Message);
    }
}

public class CsvTests
{
    [Fact]
    public void Parses_quotes_embedded_newlines_and_crlf()
    {
        var rows = Csv.Parse("name,note\r\n\"Smith, J\",\"said \"\"hi\"\"\r\nthen left\"\r\nplain,\r\n\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Smith, J", "said \"hi\"\r\nthen left"], rows[1]);
        Assert.Equal(["plain", ""], rows[2]);
    }

    [Theory]
    [InlineData("a;b;c\n1;2;3\n", ';')]
    [InlineData("a\tb\n1\t2\n", '\t')]
    [InlineData("a|b\n1|2\n", '|')]
    [InlineData("\"x,y\";z\n\"1,2\";3\n", ';')]
    [InlineData("single\nvalues\n", ',')]
    public void Detects_the_delimiter(string text, char expected) => Assert.Equal(expected, Csv.DetectDelimiter(text));

    [Fact]
    public void Excel_sep_line_and_bom()
    {
        var rows = Csv.Parse("\uFEFFsep=;\na;b\n1;2\n");
        Assert.Equal([["a", "b"], ["1", "2"]], rows);
    }

    [Fact]
    public void Writer_quotes_only_when_needed()
    {
        var text = Csv.Write([["a", "b,c", "say \"x\"", " padded", "multi\nline", ""]]);
        Assert.Equal("a,\"b,c\",\"say \"\"x\"\"\",\" padded\",\"multi\nline\",\r\n", text);
    }

    [Fact]
    public void Json_to_csv_flattens_and_unions_columns_and_guards_formulas()
    {
        var table = TableConversion.ToTable(JsonData.Parse("""
            [
              {"id": 1, "name": "Ann", "address": {"city": "Oslo", "zip": "0150"}, "tags": ["a", "b"]},
              {"id": 2, "name": "=HYPERLINK(\"http://evil\")", "extra": true, "score": -5}
            ]
            """));
        Assert.Equal(["id", "name", "address.city", "address.zip", "tags", "extra", "score"], table.Header);
        Assert.Equal(["1", "Ann", "Oslo", "0150", "[\"a\",\"b\"]", "", ""], table.Rows[0]);
        Assert.Equal(["2", "'=HYPERLINK(\"http://evil\")", "", "", "", "true", "-5"], table.Rows[1]);
        Assert.Contains(table.Warnings, w => w.Contains("formulas", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_to_csv_finds_the_records_inside_an_api_response()
    {
        var table = TableConversion.ToTable(JsonData.Parse("""{"page": 1, "data": [{"a": 1}, {"a": 2}]}"""));
        Assert.Equal(["a"], table.Header);
        Assert.Equal(2, table.Rows.Count);
        Assert.Throws<OperationException>(() => TableConversion.ToTable(JsonData.Parse("42")));
    }

    [Fact]
    public void Csv_to_json_types_values_conservatively_and_nests_dotted_columns()
    {
        var rows = Csv.Parse("id,zip,price,ok,empty,address.city,address.zip,tags,,id\n1,0150,9.99,TRUE,,Oslo,0150,\"[1,2]\",x,dup\n2,12345678901234567,-0.5,no\n");
        var json = JsonTextWriter.Write(TableConversion.FromRows(rows), indent: false);
        Assert.Equal(
            """[{"id":1,"zip":"0150","price":9.99,"ok":true,"empty":null,"address":{"city":"Oslo","zip":"0150"},"tags":[1,2],"column9":"x","id_2":"dup"},{"id":2,"zip":"12345678901234567","price":-0.5,"ok":"no","empty":null,"address":{"city":null,"zip":null},"tags":null,"column9":null,"id_2":null}]""",
            json);
    }

    [Fact]
    public void Json_csv_json_round_trip()
    {
        const string source = """[{"a":1,"b":"text, with comma","c":{"d":true,"e":null},"f":[1,"x"]},{"a":2.5,"b":"line\nbreak","c":{"d":false,"e":"s"},"f":[]}]""";
        var table = TableConversion.ToTable(JsonData.Parse(source));
        var csv = Csv.Write([table.Header!, .. table.Rows]);
        var back = TableConversion.FromRows(Csv.Parse(csv));
        Assert.Equal(source, JsonTextWriter.Write(back, indent: false));
    }
}

public class XmlTests
{
    [Fact]
    public void Xml_to_json_convention()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!-- comment -->
            <library xmlns:dc="http://purl.org/dc/elements/1.1/" open="true">
              <book id="1"><dc:title>Dune</dc:title><year>1965</year></book>
              <book id="2"><title><![CDATA[<Emma>]]></title></book>
              <note lang="en">Mixed <b>text</b> here</note>
              <empty/>
              <blank></blank>
            </library>
            """;
        var json = JsonTextWriter.Write(XmlData.ToData(Encoding.UTF8.GetBytes(xml)), indent: false);
        Assert.Equal(
            """{"library":{"@xmlns:dc":"http://purl.org/dc/elements/1.1/","@open":"true","book":[{"@id":"1","dc:title":"Dune","year":"1965"},{"@id":"2","title":"<Emma>"}],"note":{"@lang":"en","b":"text","#text":"Mixed  here"},"empty":null,"blank":""}}""",
            json);
    }

    [Fact]
    public void External_entities_are_never_resolved()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"clementine-xxe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "TOP SECRET");
        try
        {
            var xml = $"""<?xml version="1.0"?><!DOCTYPE r [<!ENTITY x SYSTEM "file:///{secret.Replace('\\', '/')}">]><r>&x;</r>""";
            try
            {
                var json = JsonTextWriter.Write(XmlData.ToData(Encoding.UTF8.GetBytes(xml)));
                Assert.DoesNotContain("TOP SECRET", json);
            }
            catch (OperationException)
            {
                // Refusing the document is fine too.
            }
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Billion_laughs_is_stopped()
    {
        var xml = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lollollollollollollollollollol\">");
        for (var i = 1; i <= 9; i++)
            xml.Append($"<!ENTITY lol{i} \"{string.Concat(Enumerable.Repeat($"&lol{(i == 1 ? "" : (i - 1).ToString())};", 10))}\">");
        xml.Append("]><lolz>&lol9;</lolz>");
        var error = Assert.Throws<OperationException>(() => XmlData.ToData(Encoding.UTF8.GetBytes(xml.ToString())));
        Assert.Contains("billion laughs", error.Message);
    }

    [Fact]
    public void Pretty_print_indents_element_content_and_leaves_mixed_content_alone()
    {
        var xml = "<?xml version=\"1.0\" encoding=\"windows-1252\"?><root><!--c--><a x=\"1\"><b>t</b></a><p>Hello <i>big</i> world</p><pre xml:space=\"preserve\">  keep\n  me </pre></root>";
        var pretty = XmlData.PrettyPrint(Encoding.GetEncoding(1252).GetBytes(xml));
        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n  <!--c-->\n  <a x=\"1\">\n    <b>t</b>\n  </a>\n  <p>Hello <i>big</i> world</p>\n  <pre xml:space=\"preserve\">  keep\n  me </pre>\n</root>\n",
            pretty);
    }

    [Fact]
    public void Invalid_xml_reports_the_position()
    {
        var error = Assert.Throws<OperationException>(() => XmlData.ToData("<a><b></a>"u8.ToArray()));
        Assert.Contains("isn't valid XML (line 1", error.Message);
    }
}

public class DataOperationTests
{
    static IOperation Op(TestWorkspace workspace, string id) => workspace.Operation(new FilesOperationProvider(), id);

    [Fact]
    public async Task Every_data_conversion_runs_end_to_end()
    {
        using var workspace = new TestWorkspace("data-ops");
        var json = workspace.WriteText("data/people.json", """[{"name":"Åsa","age":31},{"name":"Bo","age":null}]""");
        var yaml = workspace.WriteText("data/config.yml", "server:\n  port: 8080\n  hosts: [a, b]\n");
        var csv = workspace.WriteText("data/table.csv", "name;age\nÅsa;31\n");
        var xml = workspace.WriteText("data/feed.xml", "<feed><item>1</item><item>2</item></feed>");

        var toYaml = Assert.Single((await workspace.RunAsync(Op(workspace, "data.json.yaml"), [json])).Outputs);
        Assert.Equal("people.yaml", Path.GetFileName(toYaml));
        Assert.Contains("- name: Åsa", File.ReadAllText(toYaml));

        var toJson = Assert.Single((await workspace.RunAsync(Op(workspace, "data.yaml.json"), [yaml])).Outputs);
        Assert.Equal("{\n  \"server\": {\n    \"port\": 8080,\n    \"hosts\": [\n      \"a\",\n      \"b\"\n    ]\n  }\n}\n", File.ReadAllText(toJson));

        var toCsv = Assert.Single((await workspace.RunAsync(Op(workspace, "data.json.csv"), [json])).Outputs);
        var csvBytes = File.ReadAllBytes(toCsv);
        Assert.Equal([0xEF, 0xBB, 0xBF], csvBytes[..3]); // Excel-friendly
        Assert.Equal("name,age\r\nÅsa,31\r\nBo,\r\n", Encoding.UTF8.GetString(csvBytes[3..]));

        var fromCsv = Assert.Single((await workspace.RunAsync(Op(workspace, "data.csv.json"), [csv])).Outputs);
        Assert.Equal("table.json", Path.GetFileName(fromCsv));
        Assert.Contains("\"age\": 31", File.ReadAllText(fromCsv));

        var fromXml = Assert.Single((await workspace.RunAsync(Op(workspace, "data.xml.json"), [xml])).Outputs);
        Assert.Contains("\"item\": [", File.ReadAllText(fromXml));

        var pretty = Assert.Single((await workspace.RunAsync(Op(workspace, "data.json.pretty"), [json])).Outputs);
        Assert.Equal("people (pretty).json", Path.GetFileName(pretty));
        var minified = Assert.Single((await workspace.RunAsync(Op(workspace, "data.json.minify"), [pretty])).Outputs);
        Assert.Equal("""[{"name":"Åsa","age":31},{"name":"Bo","age":null}]""", File.ReadAllText(minified));

        var prettyXml = Assert.Single((await workspace.RunAsync(Op(workspace, "data.xml.pretty"), [xml])).Outputs);
        Assert.Equal("<feed>\n  <item>1</item>\n  <item>2</item>\n</feed>\n", File.ReadAllText(prettyXml));
    }

    [Fact]
    public async Task Utf16_json_is_read_and_utf8_written()
    {
        using var workspace = new TestWorkspace("data-utf16");
        var path = workspace.Path("wide.json");
        File.WriteAllText(path, "{\"k\":\"ünïcødé\"}", new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "data.json.yaml"), [path])).Outputs);
        Assert.Equal("k: ünïcødé\n", File.ReadAllText(output, new UTF8Encoding(false)));
        Assert.NotEqual(0xEF, File.ReadAllBytes(output)[0]); // no BOM for YAML
    }

    [Fact]
    public async Task Invalid_json_is_a_friendly_failure_and_writes_nothing()
    {
        using var workspace = new TestWorkspace("data-bad");
        var path = workspace.WriteText("bad/broken.json", "{\"a\": }");
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "data.json.yaml"), [path]));
        Assert.StartsWith("This isn't valid JSON", error.Message);
        Assert.Equal(["broken.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(p => Path.GetFileName(p)).ToArray());
    }

    [Fact]
    public async Task Convert_to_utf8_handles_legacy_already_utf8_and_xml_declarations()
    {
        using var workspace = new TestWorkspace("data-utf8");
        var legacy = workspace.Path("old.txt");
        File.WriteAllBytes(legacy, Encoding.GetEncoding(1252).GetBytes("Über café — “quotes” €5"));
        var result = await workspace.RunAsync(Op(workspace, "data.utf8"), [legacy]);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("old (UTF-8).txt", Path.GetFileName(output));
        Assert.Equal("Über café — “quotes” €5", File.ReadAllText(output));
        Assert.NotEqual(0xEF, File.ReadAllBytes(output)[0]);

        var modern = workspace.WriteText("new.txt", "already ✓ utf-8");
        var already = await workspace.RunAsync(Op(workspace, "data.utf8"), [modern]);
        Assert.Empty(already.Outputs);
        Assert.Equal("Already UTF-8", already.TextTitle);

        var xml = workspace.Path("legacy.xml");
        File.WriteAllBytes(xml, Encoding.GetEncoding(1252).GetBytes("<?xml version=\"1.0\" encoding=\"windows-1252\"?><a>café</a>"));
        var fixedXml = Assert.Single((await workspace.RunAsync(Op(workspace, "data.utf8"), [xml])).Outputs);
        Assert.Equal("<?xml version=\"1.0\" encoding=\"UTF-8\"?><a>café</a>", File.ReadAllText(fixedXml));
        Assert.Contains("café", JsonTextWriter.Write(XmlData.ToData(File.ReadAllBytes(fixedXml))));

        var mojibake = workspace.WriteText("double.txt", "CafÃ© dÃ©jÃ" + (char)0xA0 + " vu â€” donâ€™t");
        var repaired = Assert.Single((await workspace.RunAsync(Op(workspace, "data.utf8"), [mojibake])).Outputs);
        Assert.Equal("Café déjà vu — don’t", File.ReadAllText(repaired));
    }

    [Fact]
    public void Subtitles_use_their_own_fix_encoding_variant()
    {
        using var workspace = new TestWorkspace("data-accepts");
        var srt = InputFile.FromPath(workspace.WriteText("a.srt", "x"));
        var csv = InputFile.FromPath(workspace.WriteText("a.csv", "x"));
        Assert.False(Op(workspace, "data.utf8").Accepts(srt));
        Assert.True(Op(workspace, "subtitle.utf8").Accepts(srt));
        Assert.True(Op(workspace, "data.utf8").Accepts(csv));
        Assert.False(Op(workspace, "data.json.yaml").Accepts(csv));
    }
}
