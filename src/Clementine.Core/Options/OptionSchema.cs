namespace Clementine.Core;

/// <summary>
/// Declarative description of the parameters an operation asks for. The app renders it as a dialog,
/// the CLI accepts the same keys via <c>--set key=value</c>.
/// </summary>
public sealed record OptionSchema(string Title, IReadOnlyList<OptionField> Fields)
{
    public string? Description { get; init; }

    /// <summary>Label of the confirm button ("Trim", "Compress"). Defaults to "Convert".</summary>
    public string? ConfirmLabel { get; init; }
}

/// <summary>Shows a field only while another field has one of the given values.</summary>
public sealed record OptionCondition(string Key, params string[] Values);

public abstract record OptionField(string Key, string Label)
{
    public string? Help { get; init; }

    public OptionCondition? VisibleWhen { get; init; }
}

public sealed record NumberField(string Key, string Label, double Default, double Min, double Max) : OptionField(Key, Label)
{
    public double Step { get; init; } = 1;

    public int Decimals { get; init; }

    /// <summary>Unit suffix shown after the box ("MB", "%", "px", "dB").</summary>
    public string? Unit { get; init; }
}

public sealed record OptionChoice(string Value, string Label);

public sealed record ChoiceField(string Key, string Label, IReadOnlyList<OptionChoice> Choices, string Default) : OptionField(Key, Label);

public sealed record BoolField(string Key, string Label, bool Default) : OptionField(Key, Label);

public sealed record TextField(string Key, string Label, string Default) : OptionField(Key, Label)
{
    public string? Placeholder { get; init; }

    public bool IsPassword { get; init; }

    public bool Multiline { get; init; }
}

/// <summary>A time position/duration, edited as "hh:mm:ss.fff".</summary>
public sealed record TimeField(string Key, string Label, TimeSpan Default) : OptionField(Key, Label);

/// <summary>A folder picker; the value is the chosen path.</summary>
public sealed record FolderField(string Key, string Label) : OptionField(Key, Label);
