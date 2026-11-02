using Clementine.Engines.Files.Data;
using Clementine.Engines.Files.Text;

namespace Clementine.Engines.Files.Operations;

/// <summary>JSON ↔ YAML, JSON ↔ CSV, XML → JSON, pretty-print/minify, and "Fix encoding" for text files.</summary>
internal static class DataOperations
{
    static readonly string[] JsonExtensions = [".json", ".jsonl", ".ndjson", ".geojson", ".webmanifest"];
    static readonly string[] YamlExtensions = [".yaml", ".yml"];
    static readonly string[] CsvExtensions = [".csv", ".tsv"];
    static readonly string[] XmlExtensions = [".xml", ".plist", ".gpx", ".kml", ".rss", ".atom", ".xliff", ".xlf", ".resx", ".xaml"];

    /// <summary>Text files "Fix encoding" offers to convert (subtitles have their own variant).</summary>
    static readonly string[] TextExtensions =
    [
        ".txt", ".text", ".log", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".ndjson", ".xml", ".yaml", ".yml",
        ".html", ".htm", ".xhtml", ".ini", ".cfg", ".conf", ".nfo", ".lrc", ".tex", ".properties", ".sql", ".plist", ".gpx", ".kml",
    ];

    public static IEnumerable<IOperation> Create()
    {
        yield return new TextConversion("data.json.yaml", "YAML", "Convert to YAML", "Readable YAML with the same data — keys, order and numbers are kept.",
            "code", ".yaml", 30, JsonExtensions, (text, input) =>
            {
                var data = JsonData.ParseFile(text, input.Extension);
                return (YamlTextWriter.Write(data), []);
            });
        yield return new TextConversion("data.yaml.json", "JSON", "Convert to JSON", "JSON with the same data (YAML 1.2 types; anchors and merge keys resolved).",
            "code", ".json", 30, YamlExtensions, (text, _) =>
            {
                var (data, warnings) = YamlReader.Read(text);
                return (JsonTextWriter.Write(data), warnings);
            }, variant: "From YAML");
        yield return new TextConversion("data.json.csv", "CSV", "Convert to CSV", "A table with one row per record; nested fields become \"parent.child\" columns.",
            "spreadsheet", ".csv", 31, JsonExtensions, (text, input) =>
            {
                var table = TableConversion.ToTable(JsonData.ParseFile(text, input.Extension));
                IEnumerable<IReadOnlyList<string>> rows = table.Header is null ? table.Rows : [table.Header, .. table.Rows];
                return (Csv.Write(rows), table.Warnings);
            });
        yield return new TextConversion("data.csv.json", "JSON", "Convert to JSON", "An array with one object per row, using the first row as keys.",
            "code", ".json", 31, CsvExtensions, (text, input) =>
            {
                var rows = Csv.Parse(text, input.Extension == ".tsv" ? '\t' : null);
                if (rows.Count == 0)
                    throw new OperationException("This CSV file is empty.");
                var data = TableConversion.FromRows(rows.Select(r => (IReadOnlyList<string>)r).ToList());
                IReadOnlyList<string> warnings = rows.Count == 1 ? ["The CSV only has a header row, so the JSON array is empty."] : [];
                return (JsonTextWriter.Write(data), warnings);
            }, variant: "From CSV");
        yield return new BinaryConversion("data.xml.json", "JSON", "Convert to JSON", "JSON using the usual mapping: attributes as \"@name\", repeated elements as arrays.",
            "code", ".json", 32, XmlExtensions, bytes => JsonTextWriter.Write(XmlData.ToData(bytes)), variant: "From XML");

        yield return new TextConversion("data.json.pretty", "Format", "Pretty-print JSON", "Re-indent JSON with 2 spaces so it's easy to read (also fixes comments and trailing commas).",
            "code", null, 50, [".json", ".geojson", ".webmanifest"], (text, _) => (JsonTextWriter.Write(JsonData.Parse(text)), []),
            OperationCategory.Tool, "Pretty-print", "pretty");
        yield return new TextConversion("data.json.minify", "Format", "Minify JSON", "Remove all whitespace to make the JSON as small as possible.",
            "compress", null, 51, [".json", ".geojson", ".webmanifest"], (text, _) => (JsonTextWriter.Write(JsonData.Parse(text), indent: false), []),
            OperationCategory.Tool, "Minify", "minified");
        yield return new BinaryConversion("data.xml.pretty", "Format", "Pretty-print XML", "Re-indent XML with 2 spaces; text, comments and mixed content are kept as they are.",
            "code", null, 52, XmlExtensions, XmlData.PrettyPrint, OperationCategory.Tool, "Pretty-print", "pretty");

        var subtitles = FileTypes.Subtitle;
        var text = Inputs.Extensions(TextExtensions.Where(e => !subtitles.Contains(e)));
        yield return new EncodingOperation("data.utf8", text, manual: false, 60, "file");
        yield return new EncodingOperation("data.utf8.manual", text, manual: true, 60, "file");
    }

    /// <summary>A conversion from decoded text (any encoding) to UTF-8 text.</summary>
    sealed class TextConversion(
        string id, string group, string title, string description, string icon, string? extension, int order, string[] accepts,
        Func<string, InputFile, (string Output, IReadOnlyList<string> Warnings)> convert,
        OperationCategory category = OperationCategory.Convert, string? variant = null, string? suffix = null)
        : FileOperation(Info(id, category, group, variant, title, description, icon, extension, order), Inputs.Extensions(accepts))
    {
        protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var input = request.Input;
            progress.Report(JobProgress.Indeterminate("Converting…"));
            var decoded = TextDecoder.ReadFile(input.Path);
            var (output, warnings) = convert(decoded.Text, input);
            cancellationToken.ThrowIfCancellationRequested();
            var outputExtension = extension ?? input.Extension;
            using var pending = request.Output.BeginFile(input, outputExtension, suffix);
            TextOutput.WriteUtf8(pending.WorkingPath, output, outputExtension);
            return new OperationResult { Outputs = [pending.Commit()], Warnings = warnings };
        }
    }

    /// <summary>A conversion that reads raw bytes (XML decodes itself from its declaration/BOM).</summary>
    sealed class BinaryConversion(
        string id, string group, string title, string description, string icon, string? extension, int order, string[] accepts,
        Func<byte[], string> convert, OperationCategory category = OperationCategory.Convert, string? variant = null, string? suffix = null)
        : FileOperation(Info(id, category, group, variant, title, description, icon, extension, order), Inputs.Extensions(accepts))
    {
        protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var input = request.Input;
            if (input.Length > TextDecoder.MaxBytes)
                throw new OperationException($"This file is too large ({ByteSize.Format(input.Length)}) to convert.");
            progress.Report(JobProgress.Indeterminate("Converting…"));
            var output = convert(File.ReadAllBytes(input.Path));
            cancellationToken.ThrowIfCancellationRequested();
            var outputExtension = extension ?? input.Extension;
            using var pending = request.Output.BeginFile(input, outputExtension, suffix);
            TextOutput.WriteUtf8(pending.WorkingPath, output, outputExtension);
            return OperationResult.FromOutputs(pending.Commit());
        }
    }

    static OperationInfo Info(string id, OperationCategory category, string group, string? variant, string title, string description, string icon, string? extension, int order) =>
        category == OperationCategory.Tool
            ? Infos.Tool(id, group, title, description, icon, extension, order, variant)
            : Infos.Convert(id, group, title, description, icon, extension, order, variant);
}
