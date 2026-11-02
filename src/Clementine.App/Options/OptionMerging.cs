using System.Globalization;
using Clementine.Core;

namespace Clementine.App.Options;

/// <summary>
/// How option dialogs are pre-filled and what is remembered afterwards.
/// <para>
/// Pre-fill order (later wins): schema defaults → remembered values (<see cref="ClementineSettings.RememberedOptions"/>)
/// → dynamic defaults from the operation. Dynamic defaults describe <i>these</i> inputs (trim end = this clip's
/// duration), so a value remembered from another file must not hide them. Remembered values that no longer fit the
/// schema (unknown choice, out of range, unparsable) are ignored. Passwords are never remembered.
/// </para>
/// </summary>
public static class OptionMerging
{
    /// <summary>Typed values for every field of <paramref name="schema"/>.</summary>
    public static OptionValues Prefill(OptionSchema schema, IReadOnlyDictionary<string, string>? remembered, IReadOnlyDictionary<string, object?>? dynamicDefaults)
    {
        var values = OptionValues.Defaults(schema);
        foreach (var field in schema.Fields)
        {
            if (field is TextField { IsPassword: true })
                continue;
            if (remembered is not null && remembered.TryGetValue(field.Key, out var text) && Coerce(field, text) is { } fromMemory)
                values[field.Key] = fromMemory;
        }
        if (dynamicDefaults is not null)
        {
            foreach (var field in schema.Fields)
            {
                if (dynamicDefaults.TryGetValue(field.Key, out var raw) && Coerce(field, raw) is { } dynamicValue)
                    values[field.Key] = dynamicValue;
            }
        }
        return values;
    }

    /// <summary>What to persist for next time: every field except passwords, as invariant strings.</summary>
    public static Dictionary<string, string> ToRemember(OptionSchema schema, OptionValues values)
    {
        var keep = new OptionValues();
        foreach (var field in schema.Fields)
        {
            if (field is TextField { IsPassword: true })
                continue;
            if (values.TryGetValue(field.Key, out var value) && value is not null)
                keep[field.Key] = value;
        }
        return keep.ToInvariantStrings();
    }

    /// <summary>
    /// Converts a raw value (typed, invariant string, JSON element) into the field's native type
    /// (double, string, bool, TimeSpan), or null if it doesn't fit the field.
    /// </summary>
    public static object? Coerce(OptionField field, object? raw)
    {
        if (raw is null)
            return null;
        var probe = new OptionValues { [field.Key] = raw };
        switch (field)
        {
            case NumberField number:
            {
                var value = probe.GetDouble(field.Key, double.NaN);
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return null;
                return Math.Round(Math.Clamp(value, number.Min, number.Max), Math.Clamp(number.Decimals, 0, 10));
            }
            case ChoiceField choice:
            {
                var text = probe.GetStringOrNull(field.Key);
                return choice.Choices.FirstOrDefault(c => string.Equals(c.Value, text, StringComparison.OrdinalIgnoreCase))?.Value;
            }
            case BoolField:
            {
                var parsed = probe.GetBool(field.Key, false);
                var inverse = probe.GetBool(field.Key, true);
                return parsed == inverse ? parsed : null; // unparsable text gives different fallbacks
            }
            case TimeField:
            {
                var fallback = TimeSpan.FromTicks(-1);
                var time = probe.GetTime(field.Key, fallback);
                return time == fallback || time < TimeSpan.Zero ? null : time;
            }
            case TextField:
                return probe.GetStringOrNull(field.Key);
            case FolderField:
                return probe.GetStringOrNull(field.Key) is { Length: > 0 } folder ? folder : null;
            default:
                return raw;
        }
    }

    /// <summary>The string a <see cref="OptionCondition"/> compares against.</summary>
    public static string? ConditionText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        TimeSpan t => t.TotalSeconds.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
