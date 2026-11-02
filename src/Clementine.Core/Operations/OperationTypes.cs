namespace Clementine.Core;

/// <summary>Everything an operation needs for one run.</summary>
public sealed class OperationRequest
{
    /// <summary>The inputs. Exactly one for <see cref="InputMode.PerFile"/> operations.</summary>
    public required IReadOnlyList<InputFile> Inputs { get; init; }

    /// <summary>Option values, already merged with the schema defaults.</summary>
    public required OptionValues Options { get; init; }

    /// <summary>Allocates unique, never-overwriting output paths.</summary>
    public required OutputPlanner Output { get; init; }

    public required EngineContext Context { get; init; }

    /// <summary>Private scratch folder for this run; deleted afterwards.</summary>
    public required string TempDirectory { get; init; }

    /// <summary>The first (for per-file operations: the only) input.</summary>
    public InputFile Input => Inputs[0];
}

/// <summary>Progress report. <see cref="Fraction"/> is 0..1, or null when the amount of work is unknown.</summary>
public readonly record struct JobProgress(double? Fraction, string? Status = null)
{
    public static JobProgress Indeterminate(string status) => new(null, status);
}

/// <summary>What an operation produced.</summary>
public sealed record OperationResult
{
    /// <summary>Final paths of created files or folders.</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>Non-fatal issues worth telling the user ("Audio track dropped: DXV MOV only supports PCM").</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Text output (QR contents, metadata report, checksum). The UI offers View / Copy.</summary>
    public string? Text { get; init; }

    /// <summary>Heading for <see cref="Text"/>.</summary>
    public string? TextTitle { get; init; }

    public static OperationResult FromOutputs(params string[] outputs) => new() { Outputs = outputs };

    public static OperationResult FromText(string title, string text) => new() { TextTitle = title, Text = text };
}

/// <summary>An expected failure with a message fit to show the user ("This video has no audio track").</summary>
public class OperationException : Exception
{
    public OperationException(string userMessage, string? details = null, Exception? inner = null)
        : base(userMessage, inner)
    {
        Details = details;
    }

    /// <summary>Technical detail (tool stderr, stack) for the "Details" view and the log.</summary>
    public string? Details { get; }
}
