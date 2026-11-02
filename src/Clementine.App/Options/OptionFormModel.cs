using System.Globalization;
using System.IO;
using Clementine.Core;

namespace Clementine.App.Options;

/// <summary>
/// UI-independent state of an options dialog: typed values, which fields are visible (<see cref="OptionField.VisibleWhen"/>),
/// text-entry validation and the overall "can confirm" state. The WPF dialog is a thin view over this.
/// </summary>
public sealed class OptionFormModel
{
    readonly OptionValues _initial;
    readonly Dictionary<string, string> _textErrors = new(StringComparer.OrdinalIgnoreCase);

    public OptionFormModel(OptionSchema schema, OptionValues initial)
    {
        Schema = schema;
        _initial = new OptionValues(initial);
        Values = new OptionValues(initial);
    }

    public OptionSchema Schema { get; }

    /// <summary>Current typed values (double, string, bool, TimeSpan, folder path).</summary>
    public OptionValues Values { get; private set; }

    /// <summary>Raised after a value changes (argument: the field key). Visibility of other fields may have changed.</summary>
    public event EventHandler<string>? ValueChanged;

    public object? Get(string key) => Values.GetValueOrDefault(key);

    /// <summary>Sets a typed value (coerced to the field's type) and clears any pending text error for it.</summary>
    public void Set(string key, object? value)
    {
        var field = Schema.Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        var coerced = field switch
        {
            null => value,
            TextField => value as string ?? OptionMerging.ConditionText(value) ?? "",
            _ => OptionMerging.Coerce(field, value),
        };
        var hadError = _textErrors.Remove(key);
        if (Equals(Values.GetValueOrDefault(key), coerced))
        {
            // Same value, but a pending text error may just have been fixed: let the UI re-validate.
            if (hadError)
                ValueChanged?.Invoke(this, key);
            return;
        }
        Values[key] = coerced;
        ValueChanged?.Invoke(this, key);
    }

    /// <summary>Parses text typed into a number/time/folder box. Returns an error message, or null if accepted.</summary>
    public string? SetFromText(OptionField field, string text)
    {
        switch (field)
        {
            case NumberField number:
                if (!TryParseNumber(text, number.Unit, out var value))
                    return Fail("Enter a number.");
                if (value < number.Min - 1e-9 || value > number.Max + 1e-9)
                    return Fail($"Enter a value from {FormatNumber(number, number.Min)} to {FormatNumber(number, number.Max)}.");
                Set(field.Key, value);
                return null;
            case TimeField:
                if (!OptionValues.TryParseTime(text, out var time))
                    return Fail("Use mm:ss.fff, for example 01:30.500.");
                Set(field.Key, time);
                return null;
            case FolderField:
                Set(field.Key, text.Trim().Trim('"'));
                return Validate(field);
            default:
                Set(field.Key, text);
                return null;
        }

        string Fail(string message)
        {
            _textErrors[field.Key] = message;
            ValueChanged?.Invoke(this, field.Key);
            return message;
        }
    }

    /// <summary>Whether <paramref name="field"/> is shown given the current values.</summary>
    public bool IsVisible(OptionField field)
    {
        if (field.VisibleWhen is not { } condition)
            return true;
        var current = OptionMerging.ConditionText(Values.GetValueOrDefault(condition.Key));
        return condition.Values.Any(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<OptionField> VisibleFields => Schema.Fields.Where(IsVisible).ToList();

    /// <summary>The problem with a field's current value, or null. Hidden fields are never invalid.</summary>
    public string? Validate(OptionField field)
    {
        if (!IsVisible(field))
            return null;
        if (_textErrors.TryGetValue(field.Key, out var textError))
            return textError;
        if (field is FolderField)
        {
            var folder = Values.GetStringOrNull(field.Key);
            if (string.IsNullOrWhiteSpace(folder))
                return "Choose a folder.";
            if (!IsUsableFolderPath(folder))
                return "Enter a full folder path, for example D:\\Exports.";
        }
        return null;
    }

    public bool IsValid => Schema.Fields.All(f => Validate(f) is null);

    /// <summary>Restores the values the dialog opened with.</summary>
    public void Reset()
    {
        _textErrors.Clear();
        Values = new OptionValues(_initial);
        foreach (var field in Schema.Fields)
            ValueChanged?.Invoke(this, field.Key);
    }

    /// <summary>The final values (a copy) to hand to the job queue.</summary>
    public OptionValues Result() => new(Values);

    /// <summary>Number as shown in the edit box: the field's decimals, no thousands separators.</summary>
    public static string FormatNumber(NumberField field, double value) =>
        value.ToString("F" + Math.Clamp(field.Decimals, 0, 6), CultureInfo.CurrentCulture);

    /// <summary>Accepts the current culture's and invariant notation, with or without the unit ("50 %", "12,5").</summary>
    public static bool TryParseNumber(string? text, string? unit, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        if (!string.IsNullOrEmpty(unit) && trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^unit.Length].Trim();
        trimmed = trimmed.Replace("\u00A0", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
               || double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    static bool IsUsableFolderPath(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
