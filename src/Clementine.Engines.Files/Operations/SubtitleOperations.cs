using System.Globalization;
using Clementine.Engines.Files.Subtitles;
using Clementine.Engines.Files.Text;

namespace Clementine.Engines.Files.Operations;

/// <summary>Subtitle conversions (SRT · VTT · ASS · TXT) and tools (shift, frame rate, clean, fix encoding).</summary>
internal static class SubtitleOperations
{
    public static readonly Func<InputFile, bool> AnySubtitle = Inputs.Extensions(FileTypes.Subtitle);

    public static IEnumerable<IOperation> Create()
    {
        yield return new ConvertOperation(SubtitleFormat.Srt, "subtitle.srt", "SRT", 10,
            "The most widely supported subtitle format — plays almost everywhere.", [".srt"]);
        yield return new ConvertOperation(SubtitleFormat.Vtt, "subtitle.vtt", "VTT", 20,
            "WebVTT, for web players and HTML5 video.", [".vtt"]);
        yield return new ConvertOperation(SubtitleFormat.Ass, "subtitle.ass", "ASS", 30,
            "Advanced SubStation Alpha, for styled subtitles in mpv, VLC and Aegisub.", [".ass"]);
        yield return new ConvertOperation(SubtitleFormat.Txt, "subtitle.txt", "TXT", 40,
            "Just the dialogue as plain text, without timings.", []);

        yield return new ShiftOperation();
        yield return new FrameRateOperation();
        yield return new CleanOperation("subtitle.clean.all", "Formatting + SDH", true, true, 20,
            "Remove styling and hearing-impaired notes like [door slams] or (laughs).");
        yield return new CleanOperation("subtitle.clean.formatting", "Formatting only", true, false, 21,
            "Remove italics, colours and other styling; keep the text.");
        yield return new CleanOperation("subtitle.clean.sdh", "SDH only", false, true, 22,
            "Remove [sound effects], (descriptions), ♪ lines and SPEAKER: labels.");
        yield return new EncodingOperation("subtitle.utf8", AnySubtitle, manual: false, 40, "subtitle");
        yield return new EncodingOperation("subtitle.utf8.manual", AnySubtitle, manual: true, 40, "subtitle");
    }

    /// <summary>Reads a subtitle file in whatever encoding it uses.</summary>
    internal static (SubtitleDocument Document, DecodedText Decoded) Load(InputFile input)
    {
        var decoded = TextDecoder.ReadFile(input.Path);
        var hint = SubtitleParser.FormatFromExtension(input.Extension) ?? SubtitleFormat.Srt;
        return (SubtitleParser.Parse(decoded.Text, hint), decoded);
    }

    static OperationResult Save(OperationRequest request, SubtitleDocument document, DecodedText decoded, SubtitleFormat format, string? suffix, IEnumerable<string>? extraWarnings = null)
    {
        var extension = SubtitleWriter.Extension(format);
        var text = SubtitleWriter.Write(document, format);
        using var output = request.Output.BeginFile(request.Input, extension, suffix);
        TextOutput.WriteUtf8(output.WorkingPath, text, extension);

        var warnings = new List<string>(document.Warnings);
        if (extraWarnings is not null)
            warnings.AddRange(extraWarnings);
        if (!decoded.IsUtf8 && decoded.CodePage is not (1200 or 1201 or 12000 or 12001) && !decoded.HadBom)
            warnings.Add($"Read as {decoded.EncodingName} and saved as UTF-8.");
        return new OperationResult { Outputs = [output.Commit()], Warnings = warnings };
    }

    static SubtitleFormat SourceFormat(InputFile input) => SubtitleParser.FormatFromExtension(input.Extension) ?? SubtitleFormat.Srt;

    sealed class ConvertOperation(SubtitleFormat format, string id, string group, int order, string description, string[] skip) : FileOperation(
        Infos.Convert(id, group, $"Convert to {group}", description, format == SubtitleFormat.Txt ? "text" : "subtitle", SubtitleWriter.Extension(format), order),
        file => AnySubtitle(file) && !skip.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
    {
        protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var (document, decoded) = Load(request.Input);
            return Save(request, document, decoded, format, suffix: null);
        }
    }

    sealed class ShiftOperation() : FileOperation(
        Infos.Tool("subtitle.shift", "Shift timing", "Shift timing…", "Move every subtitle earlier or later to fix out-of-sync subtitles.", "speed", null, 10),
        AnySubtitle,
        new OptionSchema("Shift timing",
        [
            new NumberField("ms", "Shift by", 0, -3_600_000, 3_600_000) { Unit = "ms", Step = 100, Help = "Positive = later, negative = earlier. 1000 ms = 1 second." },
            new TimeField("from", "Only subtitles from", TimeSpan.Zero) { Help = "Leave at 0 to shift everything." },
        ])
        {
            ConfirmLabel = "Shift",
        })
    {
        protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var milliseconds = request.Options.GetDouble("ms", 0);
            if (Math.Abs(milliseconds) < 1)
                throw new OperationException("Enter how many milliseconds to shift by (for example 1500 or -250).");
            var from = request.Options.GetTime("from", TimeSpan.Zero);
            var (document, decoded) = Load(request.Input);
            var removed = SubtitleTools.Shift(document, TimeSpan.FromMilliseconds(milliseconds), from);
            if (document.Dialogue.FirstOrDefault() is null)
                throw new OperationException("Shifting that far would move every subtitle before the start.");
            string[] warnings = removed > 0 ? [$"{removed} subtitle(s) moved before 0:00 and were removed."] : [];
            var seconds = (milliseconds / 1000).ToString("+0.###;-0.###", CultureInfo.InvariantCulture);
            return Save(request, document, decoded, SourceFormat(request.Input), $"{seconds} s", warnings);
        }
    }

    sealed class FrameRateOperation() : FileOperation(
        Infos.Tool("subtitle.framerate", "Frame rate", "Change frame rate…", "Re-time subtitles made for another frame rate, e.g. 23.976 → 25 fps.", "speed", null, 11),
        AnySubtitle,
        new OptionSchema("Change frame rate",
        [
            new ChoiceField("from", "Subtitles were timed for", Choices(), "23.976"),
            new ChoiceField("to", "Video plays at", Choices(), "25"),
        ])
        {
            Description = "Use this when subtitles drift further out of sync as the video plays.",
            ConfirmLabel = "Convert",
        })
    {
        static IReadOnlyList<OptionChoice> Choices() => SubtitleTools.FrameRates.Select(r => new OptionChoice(r.Value, r.Label)).ToList();

        protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var fromText = request.Options.GetString("from", "23.976");
            var toText = request.Options.GetString("to", "25");
            var from = SubtitleTools.ParseFrameRate(fromText);
            var to = SubtitleTools.ParseFrameRate(toText);
            if (Math.Abs(from - to) < 0.0005)
                throw new OperationException("Pick two different frame rates.");
            var (document, decoded) = Load(request.Input);
            SubtitleTools.ChangeFrameRate(document, from, to);
            return Save(request, document, decoded, SourceFormat(request.Input), $"{toText} fps");
        }
    }

    sealed class CleanOperation(string id, string variant, bool formatting, bool hearingImpaired, int order, string description) : FileOperation(
        Infos.Tool(id, "Clean", $"Clean subtitles ({variant.ToLowerInvariant()})", description, "erase", null, order, variant),
        AnySubtitle)
    {
        protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var (document, decoded) = Load(request.Input);
            var removed = SubtitleTools.Clean(document, formatting, hearingImpaired);
            if (document.Dialogue.FirstOrDefault() is null)
                throw new OperationException("Nothing would be left after cleaning — every subtitle was a sound effect or description.");
            string[] warnings = removed > 0 ? [$"{removed} subtitle(s) became empty and were removed."] : [];
            return Save(request, document, decoded, SourceFormat(request.Input), "clean", warnings);
        }
    }
}
