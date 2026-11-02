using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Clementine.Engines.Files.Data;

/// <summary>Reads JSON (comments and trailing commas allowed; JSON Lines too) into a <see cref="DataNode"/> tree.</summary>
internal static class JsonData
{
    static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = DataLimits.MaxDepth,
    };

    public static DataNode Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, Options);
            return FromElement(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw Invalid(ex, lineOffset: 0);
        }
        catch (InvalidOperationException ex)
        {
            throw new OperationException("This JSON contains a string with an invalid \\u escape.", ex.Message, ex);
        }
    }

    /// <summary>JSON Lines / NDJSON: one value per line, returned as an array.</summary>
    public static DataArray ParseLines(string text)
    {
        var array = new DataArray();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            try
            {
                using var document = JsonDocument.Parse(line, Options);
                array.Items.Add(FromElement(document.RootElement));
            }
            catch (JsonException ex)
            {
                throw Invalid(ex, lineOffset: i);
            }
        }
        return array;
    }

    /// <summary>JSON Lines for .jsonl/.ndjson files, otherwise a single JSON document.</summary>
    public static DataNode ParseFile(string text, string extension) =>
        extension is ".jsonl" or ".ndjson" ? ParseLines(text) : Parse(text);

    public static DataNode FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new DataObject();
                foreach (var property in element.EnumerateObject())
                    obj.Add(property.Name, FromElement(property.Value));
                return obj;
            case JsonValueKind.Array:
                return new DataArray(element.EnumerateArray().Select(FromElement));
            case JsonValueKind.String:
                return new DataString(element.GetString() ?? "");
            case JsonValueKind.Number:
                return new DataNumber(element.GetRawText());
            case JsonValueKind.True:
                return DataBool.True;
            case JsonValueKind.False:
                return DataBool.False;
            default:
                return DataNull.Instance;
        }
    }

    static OperationException Invalid(JsonException ex, int lineOffset)
    {
        var where = ex.LineNumber is { } line
            ? $" (line {line + 1 + lineOffset}{(ex.BytePositionInLine is { } column ? $", column {column + 1}" : "")})"
            : "";
        var reason = ex.Message.Split(" LineNumber:", 2)[0].Split(" Path:", 2)[0].TrimEnd('.');
        return new OperationException($"This isn't valid JSON{where}: {reason}.", ex.Message, ex);
    }
}

/// <summary>
/// Writes a <see cref="DataNode"/> tree as JSON — pretty (2-space indent) or minified. Strings are escaped minimally
/// so accented letters, CJK and emoji stay readable; numbers are written exactly as read.
/// </summary>
internal sealed class JsonTextWriter
{
    readonly StringBuilder _text = new();
    readonly bool _indent;
    readonly long _maxChars;
    long _nodes;

    JsonTextWriter(bool indent, long maxChars) => (_indent, _maxChars) = (indent, maxChars);

    /// <param name="node">The tree to write.</param>
    /// <param name="indent">Pretty (2-space indent) or minified.</param>
    /// <param name="maxChars">Stop with <see cref="DataLimits.TooLarge"/> once the text grows past this many characters.</param>
    public static string Write(DataNode node, bool indent = true, long maxChars = DataLimits.MaxOutputChars)
    {
        var writer = new JsonTextWriter(indent, maxChars);
        writer.WriteNode(node, 0);
        if (indent)
            writer._text.Append('\n');
        return writer._text.ToString();
    }

    void WriteNode(DataNode node, int depth)
    {
        if (++_nodes > DataLimits.MaxNodes || _text.Length > _maxChars)
            throw DataLimits.TooLarge();
        if (depth > DataLimits.MaxDepth)
            throw DataLimits.TooDeep();

        switch (node)
        {
            case DataObject obj:
                if (obj.Properties.Count == 0)
                {
                    _text.Append("{}");
                    return;
                }
                _text.Append('{');
                for (var i = 0; i < obj.Properties.Count; i++)
                {
                    if (i > 0)
                        _text.Append(',');
                    NewLine(depth + 1);
                    WriteString(obj.Properties[i].Key);
                    _text.Append(_indent ? ": " : ":");
                    WriteNode(obj.Properties[i].Value, depth + 1);
                }
                NewLine(depth);
                _text.Append('}');
                return;
            case DataArray array:
                if (array.Items.Count == 0)
                {
                    _text.Append("[]");
                    return;
                }
                _text.Append('[');
                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                        _text.Append(',');
                    NewLine(depth + 1);
                    WriteNode(array.Items[i], depth + 1);
                }
                NewLine(depth);
                _text.Append(']');
                return;
            case DataString s:
                WriteString(s.Value);
                return;
            case DataNumber n:
                _text.Append(n.Raw);
                return;
            case DataBool b:
                _text.Append(b.Value ? "true" : "false");
                return;
            default:
                _text.Append("null");
                return;
        }
    }

    void NewLine(int depth)
    {
        if (!_indent)
            return;
        _text.Append('\n').Append(' ', depth * 2);
    }

    void WriteString(string value) => AppendQuoted(_text, value);

    /// <summary>JSON string literal escaping only what JSON requires (plus U+2028/2029 and lone surrogates).</summary>
    public static void AppendQuoted(StringBuilder text, string value)
    {
        text.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || c is '\u2028' or '\u2029' or '\u007F')
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        text.Append(c).Append(value[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
            }
        }
        text.Append('"');
    }
}
