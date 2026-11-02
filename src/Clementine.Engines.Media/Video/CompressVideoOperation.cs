using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>The three compression presets.</summary>
public enum VideoCompression
{
    /// <summary>Visually lossless-ish CRF encode at the original size.</summary>
    GoodQuality,

    /// <summary>Stronger CRF and at most 1080p.</summary>
    Smaller,

    /// <summary>Two-pass encode aiming at a percentage of the original or a size in MB.</summary>
    TargetSize,
}

/// <summary>Shrinks videos to H.264/H.265 MP4 (two-pass for a target size).</summary>
public sealed class CompressVideoOperation : MediaOperation
{
    const double ContainerOverhead = 0.97;
    readonly VideoCompression _mode;

    public CompressVideoOperation(OperationInfo info, MediaToolkit kit, VideoCompression mode)
        : base(info, kit) => _mode = mode;

    public override OptionSchema? Options => _mode != VideoCompression.TargetSize ? null : new OptionSchema("Compress to a target size",
    [
        new ChoiceField("mode", "Aim for", [new OptionChoice("percent", "% of the original"), new OptionChoice("size", "File size")], "percent"),
        new NumberField("percent", "Size", 50, 5, 95) { Unit = "%", Step = 5, VisibleWhen = new OptionCondition("mode", "percent") },
        new NumberField("sizeMb", "Size", 10, 0.1, 100_000) { Unit = "MB", Step = 1, Decimals = 1, VisibleWhen = new OptionCondition("mode", "size") },
        new ChoiceField("maxResolution", "Max resolution",
        [
            new OptionChoice("original", "Original"), new OptionChoice("2160", "4K"), new OptionChoice("1440", "1440p"),
            new OptionChoice("1080", "1080p"), new OptionChoice("720", "720p"), new OptionChoice("480", "480p"),
        ], "original"),
        new ChoiceField("codec", "Codec", [new OptionChoice("h264", "H.264 (plays everywhere)"), new OptionChoice("h265", "H.265 (smaller)")], "h264"),
    ])
    {
        Description = "Two-pass encode that lands close to the size you ask for.",
        ConfirmLabel = "Compress",
    };

    /// <summary>H.264 is the default for every preset (H.265 only for H.265 sources or when chosen).</summary>
    public override IReadOnlyList<string> RequiredEncoders => ["libx264", "aac"];

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var hevcSource = source.Video!.Codec == "hevc";
        var display = VideoGeometry.DisplaySize(source.Video);

        if (_mode == VideoCompression.GoodQuality)
        {
            var target = (hevcSource ? VideoTarget.Hevc() with { Crf = 26 } : VideoTarget.H264() with { Crf = 23 }) with
            {
                AllowHardware = false,
                AudioBitrateKbps = 128,
                CopyCompatibleAudio = false,
            };
            return await EncodeToFileAsync(request, source, target, VideoEdit.None, "compressed", progress, "Compressing…", cancellationToken).ConfigureAwait(false);
        }

        if (_mode == VideoCompression.Smaller)
        {
            var target = (hevcSource ? VideoTarget.Hevc() with { Crf = 30 } : VideoTarget.H264() with { Crf = 28 }) with
            {
                AllowHardware = false,
                AudioBitrateKbps = 96,
                CopyCompatibleAudio = false,
            };
            var edit = new VideoEdit { ScaleTo = VideoGeometry.CapShortSide(display, 1080) };
            return await EncodeToFileAsync(request, source, target, edit, "smaller", progress, "Compressing…", cancellationToken).ConfigureAwait(false);
        }

        return await TargetSizeAsync(request, source, display, progress, cancellationToken).ConfigureAwait(false);
    }

    async Task<OperationResult> TargetSizeAsync(OperationRequest request, MediaInfo source, FrameSize display, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var options = request.Options;
        var duration = source.DurationOrZero;
        if (duration <= TimeSpan.Zero)
            throw new OperationException("Couldn't work out how long this video is, so a target size can't be calculated.");
        if (duration.TotalSeconds < 0.1)
            throw new OperationException("This clip is too short to aim for a file size — use Compress › Good quality instead.");

        var bySize = options.GetString("mode", "percent") == "size";
        var sizeMb = options.GetDouble("sizeMb", 10);
        var percent = Math.Clamp(options.GetDouble("percent", 50), 1, 99);
        var targetBytes = bySize ? sizeMb * 1024 * 1024 : request.Input.Length * percent / 100;
        var plan = BitratePlan(targetBytes, duration, source.HasAudio);

        var hevc = options.GetString("codec", "h264") == "h265";
        var target = (hevc ? VideoTarget.Hevc() : VideoTarget.H264()) with
        {
            Crf = null,
            BitrateKbps = plan.VideoKbps,
            AllowHardware = false,
            AudioBitrateKbps = plan.AudioKbps,
            CopyCompatibleAudio = false,
            Audio = plan.AudioKbps > 0 ? VideoAudioCodec.Aac : VideoAudioCodec.None,
        };
        var edit = new VideoEdit();
        if (int.TryParse(options.GetString("maxResolution", "original"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxShortSide))
            edit = edit with { ScaleTo = VideoGeometry.CapShortSide(display, maxShortSide) };

        // Pass 1 writes the rate-control stats into the private temp folder (relative names avoid path-escaping trouble).
        await Kit.EncodeVideoAsync(source, target with { Pass = 1, Audio = VideoAudioCodec.None }, edit with { OutputKind = EncodeOutputKind.Null, DropAudio = true },
            "NUL", progress, "Analysing (pass 1 of 2)…", cancellationToken, 0, 0.5, request.TempDirectory).ConfigureAwait(false);

        using var output = request.Output.BeginFile(request.Input, ".mp4", TargetLabel(bySize, sizeMb, percent));
        var encoded = await Kit.EncodeVideoAsync(source, target with { Pass = 2 }, edit, output.WorkingPath, progress, "Compressing (pass 2 of 2)…",
            cancellationToken, 0.5, 0.5, request.TempDirectory).ConfigureAwait(false);
        var final = output.Commit();

        var warnings = new List<string>(encoded.Warnings);
        if (plan.AudioKbps is > 0 and < 96 && source.HasAudio)
            warnings.Add($"Audio was squeezed to {plan.AudioKbps} kbps to make room for the picture.");
        return OperationResult.FromOutputs(final) with { Warnings = warnings };
    }

    /// <summary>
    /// The name label of a target-size result: "clip (25 MB).mp4" or "clip (50% size).mp4" — distinct from
    /// "(compressed)" and "(smaller)", and from Resize 50%'s "(50%)".
    /// </summary>
    public static string TargetLabel(bool bySize, double sizeMb, double percent) => bySize
        ? $"{sizeMb.ToString("0.##", CultureInfo.InvariantCulture)} MB"
        : $"{percent.ToString("0.#", CultureInfo.InvariantCulture)}% size";

    /// <summary>Splits a byte budget between video and audio (audio shrinks first for tight targets).</summary>
    public static (int VideoKbps, int AudioKbps) BitratePlan(double targetBytes, TimeSpan duration, bool hasAudio)
    {
        var totalKbps = targetBytes * 8 * ContainerOverhead / duration.TotalSeconds / 1000;
        var audioKbps = !hasAudio ? 0 : totalKbps >= 1000 ? 128 : totalKbps >= 400 ? 96 : totalKbps >= 160 ? 64 : 32;
        var videoKbps = (int)Math.Floor(totalKbps - audioKbps);
        if (videoKbps < 40)
        {
            var minimumMb = (40 + audioKbps) * 1000 / 8.0 * duration.TotalSeconds / ContainerOverhead / 1024 / 1024;
            throw new OperationException($"That target is too small for a video this long — aim for at least {Math.Ceiling(minimumMb * 10) / 10:0.#} MB.");
        }
        return (videoKbps, audioKbps);
    }
}
