using System.Globalization;
using System.Text.Json;

namespace Clementine.Core;

/// <summary>
/// Option values keyed by <see cref="OptionField.Key"/> (case-insensitive). Getters are tolerant: values may be
/// typed (double, bool, TimeSpan), invariant strings (from the CLI or remembered settings) or JSON elements.
/// </summary>
public sealed class OptionValues : Dictionary<string, object?>
{
    public OptionValues()
        : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public OptionValues(IDictionary<string, object?> values)
        : base(values, StringComparer.OrdinalIgnoreCase)
    {
    }

    /// <summary>Every field of <paramref name="schema"/> set to its default.</summary>
    public static OptionValues Defaults(OptionSchema? schema)
    {
        var values = new OptionValues();
        if (schema is null)
            return values;
        foreach (var field in schema.Fields)
        {
            values[field.Key] = field switch
            {
                NumberField n => n.Default,
                ChoiceField c => c.Default,
                BoolField b => b.Default,
                TextField t => t.Default,
                TimeField t => t.Default,
                FolderField => null,
                _ => null,
            };
        }
        return values;
    }

    /// <summary>Copy of <paramref name="baseValues"/> with every entry of <paramref name="overrides"/> applied on top.</summary>
    public static OptionValues Merge(OptionValues baseValues, IReadOnlyDictionary<string, object?>? overrides)
    {
        var merged = new OptionValues(baseValues);
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
                merged[key] = value;
        }
        return merged;
    }

    public OptionValues With(string key, object? value)
    {
        this[key] = value;
        return this;
    }

    public string? GetStringOrNull(string key)
    {
        if (!TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } j => j.GetString(),
            JsonElement j => j.GetRawText(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    public string GetString(string key, string fallback) => GetStringOrNull(key) ?? fallback;

    public double GetDouble(string key, double fallback)
    {
        if (!TryGetValue(key, out var value) || value is null)
            return fallback;
        switch (value)
        {
            case double d: return d;
            case float f: return f;
            case int i: return i;
            case long l: return l;
            case decimal m: return (double)m;
            case JsonElement { ValueKind: JsonValueKind.Number } j: return j.GetDouble();
        }
        var text = GetStringOrNull(key);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    public int GetInt(string key, int fallback) => (int)Math.Round(GetDouble(key, fallback));

    public bool GetBool(string key, bool fallback)
    {
        if (!TryGetValue(key, out var value) || value is null)
            return fallback;
        switch (value)
        {
            case bool b: return b;
            case JsonElement { ValueKind: JsonValueKind.True }: return true;
            case JsonElement { ValueKind: JsonValueKind.False }: return false;
        }
        return GetStringOrNull(key)?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback,
        };
    }

    public TimeSpan GetTime(string key, TimeSpan fallback)
    {
        if (!TryGetValue(key, out var value) || value is null)
            return fallback;
        switch (value)
        {
            case TimeSpan t: return t;
            case double d: return TimeSpan.FromSeconds(d);
            case int i: return TimeSpan.FromSeconds(i);
            case JsonElement { ValueKind: JsonValueKind.Number } j: return TimeSpan.FromSeconds(j.GetDouble());
        }
        return TryParseTime(GetStringOrNull(key), out var parsed) ? parsed : fallback;
    }

    /// <summary>Parses "hh:mm:ss.fff", "mm:ss", "ss.fff" or a plain number of seconds.</summary>
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3)
            return false;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
                return false;
            seconds = seconds * 60 + n;
        }
        time = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Formats as "h:mm:ss.fff" (hours omitted when zero).</summary>
    public static string FormatTime(TimeSpan time)
    {
        var text = time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
        return text;
    }

    /// <summary>Invariant string form of every value (for persisting remembered options).</summary>
    public Dictionary<string, string> ToInvariantStrings()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in this)
        {
            if (value is null)
                continue;
            result[key] = value switch
            {
                TimeSpan t => t.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => GetStringOrNull(key) ?? "",
            };
        }
        return result;
    }

    public static OptionValues FromInvariantStrings(IReadOnlyDictionary<string, string>? values)
    {
        var result = new OptionValues();
        if (values is not null)
        {
            foreach (var (key, value) in values)
                result[key] = value;
        }
        return result;
    }
}
