namespace Clementine.Engines.Files.Subtitles;

/// <summary>Subtitle file formats Clementine reads and writes.</summary>
internal enum SubtitleFormat
{
    Srt,
    Vtt,
    Ass,
    Ssa,
    Sbv,

    /// <summary>Plain text (write only): the dialogue without timings.</summary>
    Txt,
}

/// <summary>
/// One subtitle. <see cref="Text"/> uses a neutral markup that every format can be converted from: SRT-style tags
/// (&lt;i&gt; &lt;b&gt; &lt;u&gt; &lt;s&gt; &lt;font color&gt;), '\n' line breaks and an optional leading "{\anN}" position.
/// </summary>
internal sealed class SubtitleCue
{
    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    public string Text { get; set; } = "";

    /// <summary>The cue as written in its source format, so re-writing the same format keeps every detail.</summary>
    public SourceText? Source { get; set; }

    /// <summary>WebVTT cue identifier and settings ("align:start line:0").</summary>
    public string? Identifier { get; set; }

    public string? Settings { get; set; }

    /// <summary>ASS/SSA event fields (Layer, Style, Name, margins, Effect…) by lower-case field name.</summary>
    public Dictionary<string, string>? AssFields { get; set; }

    /// <summary>ASS "Comment:" events: kept when re-writing ASS, never shown or converted.</summary>
    public bool IsComment { get; set; }

    /// <summary>The source-format text, if <see cref="Text"/> hasn't been changed since parsing.</summary>
    public string? UnchangedSourceText(SubtitleFormat format) =>
        Source is { } source && source.Format == format && source.Neutral == Text ? source.Raw : null;
}

/// <param name="Format">Format the raw text is written in.</param>
/// <param name="Raw">Text exactly as in the source file (VTT tags, ASS override blocks…).</param>
/// <param name="Neutral">What it converted to, to detect later edits.</param>
internal sealed record SourceText(SubtitleFormat Format, string Raw, string Neutral)
{
    public SourceText WithRaw(string raw) => this with { Raw = raw };
}

/// <summary>An ASS/SSA script's sections, kept verbatim except for [Events].</summary>
internal sealed class AssScript
{
    /// <summary>Sections in file order; the [Events] section's lines are regenerated from the cues.</summary>
    public List<(string Header, List<string> Lines)> Sections { get; } = [];

    /// <summary>Event field names from the "Format:" line, lower-case, in order.</summary>
    public List<string> EventFormat { get; set; } = [];

    /// <summary>The original "Format:" line of [Events].</summary>
    public string EventFormatLine { get; set; } = "";

    /// <summary>Other [Events] lines that aren't Dialogue/Comment (Picture, Sound, Movie, Command), kept as is.</summary>
    public List<string> OtherEventLines { get; } = [];

    public int WrapStyle { get; set; }

    public bool IsSsa { get; set; }
}

/// <summary>A parsed subtitle file.</summary>
internal sealed class SubtitleDocument
{
    public SubtitleFormat Format { get; set; }

    public List<SubtitleCue> Cues { get; set; } = [];

    /// <summary>WebVTT: the rest of the "WEBVTT" line and header lines, then STYLE/REGION/NOTE blocks before the first cue.</summary>
    public string? VttHeader { get; set; }

    public List<string> VttBlocks { get; } = [];

    public AssScript? Ass { get; set; }

    /// <summary>Problems worth telling the user (skipped blocks, drawings dropped…).</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Dialogue only (ASS comments excluded).</summary>
    public IEnumerable<SubtitleCue> Dialogue => Cues.Where(c => !c.IsComment);
}
