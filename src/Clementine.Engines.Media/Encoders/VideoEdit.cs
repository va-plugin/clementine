using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Encoders;

/// <summary>A width × height in pixels.</summary>
public readonly record struct FrameSize(int Width, int Height)
{
    public double Aspect => Height == 0 ? 1 : (double)Width / Height;

    public override string ToString() => $"{Width}x{Height}";
}

/// <summary>Where an encode's output goes.</summary>
public enum EncodeOutputKind
{
    /// <summary>A normal file; container flags (faststart, loop) apply.</summary>
    File,

    /// <summary>FFmpeg's null muxer (analysis / first pass).</summary>
    Null,

    /// <summary>A muxer chosen by <see cref="VideoEdit.ExtraOutputArguments"/> (segment muxer): no container flags.</summary>
    Custom,
}

/// <summary>
/// What a tool changes on top of a plain re-encode: a time range, video/audio filters, a new size or frame rate.
/// Video filters see upright, square-pixel, deinterlaced frames; the encoder's own scaling/format stage comes after.
/// </summary>
public sealed record VideoEdit
{
    public static readonly VideoEdit None = new();

    /// <summary>Seek here before decoding (frame-accurate when re-encoding).</summary>
    public TimeSpan? Start { get; init; }

    /// <summary>Read only this much of the input.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Filters applied in order to the upright, square-pixel frames.</summary>
    public IReadOnlyList<string> VideoFilters { get; init; } = [];

    /// <summary>
    /// A custom graph instead of <see cref="VideoFilters"/>: receives the input label (e.g. "[0:0]") and must return graph
    /// text whose final output label is "[edited]". Used for split/concat constructions such as ping-pong loops.
    /// </summary>
    public Func<string, string>? VideoGraph { get; init; }

    /// <summary>Frame size after the filters (null = unchanged).</summary>
    public FrameSize? FrameSize { get; init; }

    /// <summary>Scale the result to this size in the encoder stage (rounded to what the codec needs).</summary>
    public FrameSize? ScaleTo { get; init; }

    /// <summary>Audio filters (atempo, areverse…). Any audio edit forces re-encoding the audio.</summary>
    public IReadOnlyList<string> AudioFilters { get; init; } = [];

    /// <summary>Custom audio graph: receives the input label and must end with "[edited_a]".</summary>
    public Func<string, string>? AudioGraph { get; init; }

    /// <summary>Resample to this constant frame rate (fps filter).</summary>
    public Rational? FrameRate { get; init; }

    /// <summary>Playback speed factor, used for the expected output duration.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>Exact expected output duration when the tool knows better (ping-pong doubles the length).</summary>
    public TimeSpan? OutputDuration { get; init; }

    /// <summary>Leave the audio out.</summary>
    public bool DropAudio { get; init; }

    /// <summary>Extra output arguments (e.g. -force_key_frames for precise splitting).</summary>
    public IReadOnlyList<string> ExtraOutputArguments { get; init; } = [];

    /// <summary>Options placed before "-i" (image-sequence frame rate, EXR transfer…).</summary>
    public IReadOnlyList<string> InputOptions { get; init; } = [];

    public EncodeOutputKind OutputKind { get; init; } = EncodeOutputKind.File;

    internal bool ChangesAudio => AudioFilters.Count > 0 || AudioGraph is not null;
}
