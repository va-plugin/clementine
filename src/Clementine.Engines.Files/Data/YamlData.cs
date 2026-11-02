using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Clementine.Engines.Files.Data;

/// <summary>
/// YAML → <see cref="DataNode"/> using the YAML 1.2 core schema (so "yes"/"no" stay strings), with anchors and
/// aliases, "&lt;&lt;" merge keys, standard tags, multi-document streams (→ an array) and guards against deep nesting and
/// alias bombs. Anchors are registered only once their node is complete, so self-referencing aliases can't loop.
/// </summary>
internal sealed partial class YamlReader
{
    const string TagPrefix = "tag:yaml.org,2002:";

    readonly IParser _parser;
    readonly Dictionary<string, DataNode> _anchors = new(StringComparer.Ordinal);
    readonly HashSet<string> _warnings = [];
    int _depth;
    long _lastLine = 1;
    long _complexKeyChars;

    YamlReader(IParser parser) => _parser = parser;

    public static (DataNode Value, IReadOnlyList<string> Warnings) Read(string text)
    {
        var reader = new YamlReader(new Parser(new StringReader(text)));
        try
        {
            return (reader.ReadStream(), reader._warnings.ToList());
        }
        catch (YamlException ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            reason = reason.Split("): ", 2) is [_, var tail] ? tail : reason;
            throw new OperationException($"This isn't valid YAML (line {ex.Start.Line}, column {ex.Start.Column}): {reason.TrimEnd('.')}.", ex.ToString(), ex);
        }
        catch (InvalidOperationException ex)
        {
            // The parser reports some unterminated structures (an unclosed "[") by running out of events.
            throw new OperationException(
                $"This isn't valid YAML (line {reader._lastLine} or after): a list, mapping or quoted string is never closed.", ex.ToString(), ex);
        }
    }

    DataNode ReadStream()
    {
        _parser.Consume<StreamStart>();
        var documents = new List<DataNode>();
        while (_parser.TryConsume<DocumentStart>(out _))
        {
            _anchors.Clear(); // anchors are scoped to their document
            documents.Add(_parser.Accept<DocumentEnd>(out _) ? DataNull.Instance : ReadNode());
            _parser.Consume<DocumentEnd>();
        }
        _parser.Consume<StreamEnd>();

        if (documents.Count > 1)
            _warnings.Add($"The YAML holds {documents.Count} documents; they were combined into a JSON array.");
        return documents.Count switch
        {
            0 => DataNull.Instance,
            1 => documents[0],
            _ => new DataArray(documents),
        };
    }

    DataNode ReadNode()
    {
        if (++_depth > DataLimits.MaxDepth)
            throw DataLimits.TooDeep();
        try
        {
            if (_parser.Current is { } current)
                _lastLine = current.Start.Line;
            switch (_parser.Current)
            {
                case AnchorAlias alias:
                    _parser.MoveNext();
                    return _anchors.TryGetValue(alias.Value.Value, out var target)
                        ? target
                        : throw new OperationException($"The YAML alias *{alias.Value.Value} refers to an anchor that isn't defined before it.");

                case Scalar scalar:
                    _parser.MoveNext();
                    return Register(scalar, Resolve(scalar));

                case SequenceStart start:
                    _parser.MoveNext();
                    var array = new DataArray();
                    while (!_parser.TryConsume<SequenceEnd>(out _))
                        array.Items.Add(ReadNode());
                    NoteTag(start.Tag);
                    return Register(start, array);

                case MappingStart start:
                    _parser.MoveNext();
                    var mapping = ReadMapping();
                    NoteTag(start.Tag);
                    return Register(start, mapping);

                default:
                    throw new OperationException("This YAML has a structure Clementine couldn't read.");
            }
        }
        finally
        {
            _depth--;
        }
    }

    DataObject ReadMapping()
    {
        var entries = new List<(string? Key, DataNode Value)>(); // Key null = a "<<" merge
        while (!_parser.TryConsume<MappingEnd>(out _))
        {
            if (_parser.Current is Scalar { Value: "<<", Style: ScalarStyle.Plain } merge
                && (merge.Tag.IsEmpty || merge.Tag.Value == TagPrefix + "merge"))
            {
                _parser.MoveNext();
                entries.Add((null, ReadNode()));
                continue;
            }
            var key = ReadKey();
            entries.Add((key, ReadNode()));
        }

        var explicitKeys = new HashSet<string>(entries.Where(e => e.Key is not null).Select(e => e.Key!), StringComparer.Ordinal);
        var result = new DataObject();
        // Position of each key in result.Properties: DataObject.Set/ContainsKey scan the list, which made big
        // mappings quadratic (150 000 keys took minutes).
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            if (key is not null)
            {
                if (positions.TryGetValue(key, out var existing))
                {
                    _warnings.Add($"Duplicate key \"{key}\": the last value was kept.");
                    result.Properties[existing] = new(key, value);
                }
                else
                {
                    positions[key] = result.Properties.Count;
                    result.Add(key, value);
                }
                continue;
            }
            // Merge: earlier sources win over later ones; explicit keys always win.
            var sources = value switch
            {
                DataObject single => new List<DataObject> { single },
                DataArray list when list.Items.All(i => i is DataObject) => list.Items.Cast<DataObject>().ToList(),
                _ => throw new OperationException("A YAML merge key (<<) must point to a mapping or a list of mappings."),
            };
            foreach (var source in sources)
            {
                foreach (var (k, v) in source.Properties)
                {
                    if (!explicitKeys.Contains(k) && positions.TryAdd(k, result.Properties.Count))
                        result.Add(k, v);
                }
            }
        }
        return result;
    }

    /// <summary>JSON keys are strings: scalars use their text, complex keys become compact JSON.</summary>
    string ReadKey()
    {
        if (_parser.Current is Scalar scalar)
        {
            _parser.MoveNext();
            Register(scalar, Resolve(scalar));
            return scalar.Value;
        }
        switch (ReadNode())
        {
            case DataString s:
                return s.Value;
            case DataNumber n:
                return n.Raw;
            case DataBool b:
                return b.Value ? "true" : "false";
            case DataNull:
                return "null";
            case var complex:
                // Every complex key is expanded separately, so a tiny file of aliased keys could otherwise build
                // gigabytes of key text; all of them share one small budget.
                _warnings.Add("Some keys were lists or mappings; they were written as JSON text.");
                var text = JsonTextWriter.Write(complex, indent: false, maxChars: DataLimits.MaxComplexKeyChars - _complexKeyChars);
                _complexKeyChars += text.Length;
                return text;
        }
    }

    DataNode Register(NodeEvent node, DataNode value)
    {
        if (!node.Anchor.IsEmpty)
            _anchors[node.Anchor.Value] = value;
        return value;
    }

    void NoteTag(TagName tag)
    {
        if (!tag.IsEmpty && !tag.IsNonSpecific && !tag.Value.StartsWith(TagPrefix, StringComparison.Ordinal))
            _warnings.Add($"Custom YAML tags such as {tag.Value} have no JSON equivalent and were dropped.");
    }

    DataNode Resolve(Scalar scalar)
    {
        var value = scalar.Value;
        if (!scalar.Tag.IsEmpty && !scalar.Tag.IsNonSpecific)
        {
            switch (scalar.Tag.Value)
            {
                case TagPrefix + "str" or TagPrefix + "binary" or TagPrefix + "timestamp":
                    return new DataString(value);
                case TagPrefix + "null":
                    return DataNull.Instance;
                case TagPrefix + "bool":
                    return ResolvePlain(value) is DataBool flag ? flag : new DataString(value);
                case TagPrefix + "int" or TagPrefix + "float":
                    return ResolvePlain(value) is DataNumber number ? number : new DataString(value);
                default:
                    NoteTag(scalar.Tag);
                    break;
            }
        }
        else if (scalar.Tag.IsNonSpecific && scalar.Tag.Value == "!")
        {
            return new DataString(value); // "! 123" forces a string
        }

        return scalar.Style is ScalarStyle.Plain or ScalarStyle.Any ? ResolvePlain(value) : new DataString(value);
    }

    /// <summary>YAML 1.2 core schema resolution of a plain scalar.</summary>
    internal DataNode ResolvePlain(string value)
    {
        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return DataNull.Instance;
            case "true" or "True" or "TRUE":
                return DataBool.True;
            case "false" or "False" or "FALSE":
                return DataBool.False;
            case ".inf" or ".Inf" or ".INF" or "+.inf" or "+.Inf" or "+.INF" or "-.inf" or "-.Inf" or "-.INF" or ".nan" or ".NaN" or ".NAN":
                _warnings.Add("JSON has no infinity or NaN; such values were written as strings.");
                return new DataString(value);
        }

        if (DecimalInteger().IsMatch(value))
            return new DataNumber(NormalizeDecimal(value));
        var radix = value.StartsWith("0x", StringComparison.Ordinal) && HexInteger().IsMatch(value) ? 16
            : value.StartsWith("0o", StringComparison.Ordinal) && OctalInteger().IsMatch(value) ? 8
            : 0;
        if (radix != 0)
        {
            var digits = value[2..].TrimStart('0');
            if (digits.Length > DataLimits.MaxRadixDigits)
            {
                _warnings.Add("Extremely long hexadecimal or octal numbers were kept as text.");
                return new DataString(value);
            }
            var number = radix == 16
                ? BigInteger.Parse("0" + digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                : digits.Aggregate(BigInteger.Zero, (n, d) => n * 8 + (d - '0'));
            return new DataNumber(number.ToString(CultureInfo.InvariantCulture));
        }
        if (FloatNumber().Match(value) is { Success: true } match)
            return new DataNumber(JsonFloat(match));
        return new DataString(value);
    }

    /// <summary>"+0042" → "42", "-007" → "-7", "-0" → "0" — done on the text, so a huge integer costs linear time.</summary>
    static string NormalizeDecimal(string value)
    {
        var negative = value[0] == '-';
        var digits = value.TrimStart('+', '-').TrimStart('0');
        return digits.Length == 0 ? "0" : negative ? "-" + digits : digits;
    }

    /// <summary>"+.5" → "0.5", "-5." → "-5.0", "1E3" → "1E3" (already valid JSON).</summary>
    static string JsonFloat(Match match)
    {
        var sign = match.Groups["sign"].Value == "-" ? "-" : "";
        var integer = match.Groups["int"].Value;
        var fraction = match.Groups["frac"].Value;
        var exponent = match.Groups["exp"].Value;
        if (integer.Length == 0)
            integer = "0";
        integer = integer.TrimStart('0');
        if (integer.Length == 0)
            integer = "0";
        var text = sign + integer;
        if (match.Groups["dot"].Success)
            text += "." + (fraction.Length == 0 ? "0" : fraction);
        return text + exponent;
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex DecimalInteger();

    [GeneratedRegex(@"^0x[0-9a-fA-F]+$")]
    private static partial Regex HexInteger();

    [GeneratedRegex(@"^0o[0-7]+$")]
    private static partial Regex OctalInteger();

    [GeneratedRegex(@"^(?<sign>[-+]?)(?:(?<dot>\.)(?<frac>[0-9]+)|(?<int>[0-9]+)(?:(?<dot>\.)(?<frac>[0-9]*))?)(?<exp>[eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatNumber();
}

/// <summary>
/// <see cref="DataNode"/> → YAML. Strings that a YAML 1.1 or 1.2 parser would read as something else ("yes", "1.0",
/// "null", "2024-01-01", "") are quoted; multi-line strings use literal blocks; numbers use a form both versions read.
/// </summary>
internal static partial class YamlTextWriter
{
    public static string Write(DataNode node)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        var emitter = new Emitter(writer, new EmitterSettings().WithBestIndent(2).WithBestWidth(int.MaxValue).WithIndentedSequences().WithNewLine("\n"));
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        Emit(emitter, node, 0);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return KeepEmoji(writer.ToString());
    }

    /// <summary>
    /// YamlDotNet escapes characters outside the BMP ("\U0001F39B"); YAML allows them raw in double-quoted strings,
    /// which reads far better. The unescaped text is only used if it parses to exactly the same data.
    /// </summary>
    static string KeepEmoji(string yaml)
    {
        if (!yaml.Contains("\\U", StringComparison.Ordinal))
            return yaml;
        var candidate = LongEscape().Replace(yaml, m =>
            m.Groups["slashes"].Value + char.ConvertFromUtf32(int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        try
        {
            var same = JsonTextWriter.Write(YamlReader.Read(candidate).Value) == JsonTextWriter.Write(YamlReader.Read(yaml).Value);
            return same ? candidate : yaml;
        }
        catch (Exception ex) when (ex is OperationException or ArgumentException)
        {
            return yaml;
        }
    }

    /// <summary>A "\UXXXXXXXX" escape not preceded by another (unescaped) backslash.</summary>
    [GeneratedRegex(@"(?<!\\)(?<slashes>(?:\\\\)*)\\U(?<hex>0001[0-9A-Fa-f]{4}|00(?:0[2-9A-Fa-f]|10)[0-9A-Fa-f]{4})")]
    private static partial Regex LongEscape();

    static void Emit(IEmitter emitter, DataNode node, int depth)
    {
        if (depth > DataLimits.MaxDepth)
            throw DataLimits.TooDeep();
        switch (node)
        {
            case DataObject obj:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, obj.Properties.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in obj.Properties)
                {
                    EmitString(emitter, key, isKey: true);
                    Emit(emitter, value, depth + 1);
                }
                emitter.Emit(new MappingEnd());
                break;
            case DataArray array:
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, array.Items.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in array.Items)
                    Emit(emitter, item, depth + 1);
                emitter.Emit(new SequenceEnd());
                break;
            case DataString s:
                EmitString(emitter, s.Value, isKey: false);
                break;
            case DataNumber n:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, YamlNumber(n.Raw), ScalarStyle.Plain, true, false));
                break;
            case DataBool b:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, b.Value ? "true" : "false", ScalarStyle.Plain, true, false));
                break;
            default:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, "null", ScalarStyle.Plain, true, false));
                break;
        }
    }

    static void EmitString(IEmitter emitter, string value, bool isKey)
    {
        ScalarStyle style;
        if (LooksLikeAnotherType(value))
            style = ScalarStyle.DoubleQuoted;
        else if (!isKey && value.Contains('\n') && !value.Contains('\r') && !value.Contains('\t'))
            style = ScalarStyle.Literal; // the emitter falls back to quotes if a block isn't possible
        else
            style = ScalarStyle.Any; // plain when that's unambiguous, quoted otherwise
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, style, isPlainImplicit: style == ScalarStyle.Any, isQuotedImplicit: true));
    }

    /// <summary>Would a YAML 1.1 or 1.2 parser read this plain text as null, a boolean, a number or a date?</summary>
    internal static bool LooksLikeAnotherType(string value) =>
        value.Length == 0 || Keyword().IsMatch(value) || NumberLike().IsMatch(value);

    /// <summary>Exponents get a dot and an explicit sign ("1e5" → "1.0e+5"): YAML 1.1 parsers need both.</summary>
    internal static string YamlNumber(string raw)
    {
        var e = raw.IndexOfAny(['e', 'E']);
        if (e < 0)
            return raw;
        var mantissa = raw[..e];
        var exponent = raw[(e + 1)..];
        if (!mantissa.Contains('.'))
            mantissa += ".0";
        if (!exponent.StartsWith('+') && !exponent.StartsWith('-'))
            exponent = "+" + exponent;
        return $"{mantissa}e{exponent}";
    }

    [GeneratedRegex(@"^(?:~|null|Null|NULL|y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF|<<|=)$")]
    private static partial Regex Keyword();

    /// <summary>Anything starting like a number, hex/octal/binary literal, infinity/NaN or timestamp.</summary>
    [GeneratedRegex(@"^[-+]?(?:\.?[0-9]|\.(?:inf|Inf|INF|nan|NaN|NAN)$)")]
    private static partial Regex NumberLike();
}
