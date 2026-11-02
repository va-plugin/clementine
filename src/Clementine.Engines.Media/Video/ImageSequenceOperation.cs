using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>
/// Turns a selection of stills (PNG, JPEG, TIFF, BMP, EXR, TGA, WebP) into a video, in natural name order.
/// Frames are linked (or copied) into the private temp folder under sequential names, so any file names work.
/// </summary>
public sealed partial class ImageSequenceOperation : MediaOperation
{
    public ImageSequenceOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override OptionSchema Options { get; } = new("Image sequence → video",
    [
        new NumberField("fps", "Frame rate", 30, 1, 120) { Unit = "fps" },
        new ChoiceField("codec", "Format",
        [
            new OptionChoice("h264", "H.264 MP4"),
            new OptionChoice("prores4444", "ProRes 4444 (alpha)"),
            new OptionChoice("hapalpha", "HAP Alpha"),
            new OptionChoice("dxv", "DXV"),
        ], "h264"),
    ])
    {
        ConfirmLabel = "Make video",
    };

    public override bool Accepts(InputFile file) => MediaInputs.IsSequenceImage(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var images = request.Inputs.OrderBy(i => i.Name, NaturalComparer.Instance).ToList();
        var fps = Math.Clamp(request.Options.GetDouble("fps", 30), 1, 120);
        var rate = Rational.FromDouble(fps);
        var target = request.Options.GetString("codec", "h264") switch
        {
            "prores4444" => VideoTarget.ProResOf(ProResProfile.P4444, audio: false),
            "hapalpha" => VideoTarget.HapOf(HapFormat.HapAlpha, audio: false),
            "dxv" => VideoTarget.DxvOf(audio: false),
            _ => VideoTarget.H264(".mp4", audio: false),
        };

        progress.Report(JobProgress.Indeterminate("Gathering frames…"));
        var first = await Kit.Probe.ProbeVideoAsync(images[0].Path, cancellationToken).ConfigureAwait(false);
        var firstVideo = first.Video!;
        var extensions = images.Select(i => i.Extension).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string extension;
        if (extensions.Count == 1)
        {
            extension = extensions[0];
            for (var i = 0; i < images.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LinkOrCopy(images[i].Path, Path.Combine(request.TempDirectory, FrameName(i, extension)));
            }
        }
        else
        {
            // Mixed formats: normalise every frame to PNG so one decoder reads them all.
            extension = ".png";
            for (var i = 0; i < images.Count; i++)
            {
                await Kit.Ffmpeg.RunAsync(["-i", images[i].Path, "-frames:v", "1", "-update", "1", Path.Combine(request.TempDirectory, FrameName(i, extension))],
                    new FfmpegRunOptions { Status = "Gathering frames…", Progress = progress }, cancellationToken).ConfigureAwait(false);
            }
        }

        var size = new FrameSize(firstVideo.Width, firstVideo.Height);
        var duration = TimeSpan.FromSeconds(images.Count / fps);
        var sequence = first with
        {
            Path = $"frame_%06d{extension}",
            Duration = duration,
            VideoStreams = [firstVideo with { Index = 0, FrameRate = rate, RealFrameRate = rate, TimeBase = new Rational(rate.Denominator, rate.Numerator), Rotation = 0 }],
            AudioStreams = [],
        };
        var inputOptions = new List<string> { "-framerate", rate.ToString(), "-start_number", "0" };
        if (extension == ".exr")
            inputOptions.AddRange(["-apply_trc", "iec61966_2_1"]);
        var edit = new VideoEdit
        {
            InputOptions = inputOptions,
            VideoFilters = [$"scale={size.Width}:{size.Height}:force_original_aspect_ratio=decrease,pad={size.Width}:{size.Height}:(ow-iw)/2:(oh-ih)/2:color=black@0"],
            OutputDuration = duration,
        };

        using var output = request.Output.BeginFile(request.Output.ResolveDirectory(images[0]), OutputName(images[0].BaseName, target), target.Extension);
        var plan = await Kit.EncodeVideoAsync(sequence, target, edit, output.WorkingPath, progress, $"Encoding {target.DisplayName}…", cancellationToken,
            workingDirectory: request.TempDirectory).ConfigureAwait(false);
        var final = output.Commit();
        return OperationResult.FromOutputs(final) with { Warnings = plan.Warnings };
    }

    /// <summary>"shot_0001" → "shot", "render.0001" → "render", "0001" → "sequence".</summary>
    public static string SequenceName(string firstBaseName)
    {
        var trimmed = TrailingNumber().Replace(firstBaseName, "");
        return trimmed.Length == 0 ? "sequence" : trimmed;
    }

    /// <summary>"shot" for H.264 MP4; the .mov VJ codecs carry their name ("shot (HAP Alpha).mov") so they never clash.</summary>
    public static string OutputName(string firstBaseName, VideoTarget target) =>
        target.IsVjCodec ? $"{SequenceName(firstBaseName)} ({target.DisplayName})" : SequenceName(firstBaseName);

    static string FrameName(int index, string extension) => $"frame_{index.ToString("000000", CultureInfo.InvariantCulture)}{extension}";

    static void LinkOrCopy(string source, string destination)
    {
        if (!CreateHardLink(destination, source, IntPtr.Zero))
            File.Copy(source, destination);
    }

    [GeneratedRegex(@"[\s._-]*\d+$")]
    private static partial Regex TrailingNumber();

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
