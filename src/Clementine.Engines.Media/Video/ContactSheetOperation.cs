using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>A 4×4 grid of evenly spaced frames as one PNG — a quick visual overview of a clip.</summary>
public sealed class ContactSheetOperation : MediaOperation
{
    const int Columns = 4;
    const int Rows = 4;
    const int ThumbnailWidth = 320;

    public ContactSheetOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file);

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(request.Input.Path, cancellationToken).ConfigureAwait(false);
        var video = source.Video!;
        var duration = source.DurationOrZero;
        var display = VideoGeometry.DisplaySize(video);
        var thumbWidth = Math.Min(ThumbnailWidth, display.Width) & ~1;
        var thumb = new FrameSize(thumbWidth, VideoGeometry.Even(display.Height * (double)thumbWidth / display.Width));
        var count = Columns * Rows;
        var lastFrame = TimeSpan.FromSeconds(Math.Max(0, duration.TotalSeconds - 1.5 / video.Fps));
        var labelled = CopyLabelFont(request.TempDirectory);
        var fontSize = Math.Max(10, thumb.Height / 10);

        // One fast seek per tile (much quicker than decoding a long file end to end).
        var completed = 0;
        using var gate = new SemaphoreSlim(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var at = duration > TimeSpan.Zero ? duration * ((i + 0.5) / count) : TimeSpan.Zero;
                if (at > lastFrame)
                    at = lastFrame;
                var tile = Path.Combine(request.TempDirectory, $"tile{i + 1:00}.png");
                var filters = VideoEncodePlanner.PreFilters(video).Append(VideoEncodePlanner.ToRgb(video, thumb, keepAlpha: false));
                if (labelled)
                {
                    var label = OptionValues.FormatTime(at).Replace(":", @"\:", StringComparison.Ordinal);
                    filters = filters.Append($"drawtext=fontfile={LabelFont}:text='{label}':fontcolor=white:fontsize={fontSize}" +
                        ":x=6:y=h-th-8:box=1:boxcolor=black@0.55:boxborderw=4");
                }
                await Kit.Ffmpeg.RunAsync(
                [
                    .. VideoEncodePlanner.DecoderArguments(video),
                    "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", source.Path,
                    "-map", $"0:{video.Index}", "-frames:v", "1", "-vf", string.Join(',', filters), "-update", "1", tile,
                ], new FfmpegRunOptions { WorkingDirectory = request.TempDirectory }, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(tile))
                    await CopyPreviousOrBlankAsync(tile, i, thumb, request.TempDirectory, cancellationToken).ConfigureAwait(false);
                progress.Report(new JobProgress(0.9 * Interlocked.Increment(ref completed) / count, "Collecting frames…"));
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        using var output = request.Output.BeginFile(request.Input, ".png", "contact sheet");
        var final = await RunToOutputAsync(output,
        [
            "-framerate", "1", "-start_number", "1", "-i", "tile%02d.png",
            "-vf", $"tile={Columns}x{Rows}:padding=6:margin=6:color=0x151515", "-frames:v", "1", "-update", "1", output.WorkingPath,
        ], new FfmpegRunOptions { Progress = progress, Status = "Building the sheet…", ProgressStart = 0.9, ProgressSpan = 0.1, WorkingDirectory = request.TempDirectory },
            cancellationToken).ConfigureAwait(false);
        return OperationResult.FromOutputs(final);
    }

    const string LabelFont = "label.ttf";

    /// <summary>
    /// Copies Segoe UI next to the tiles so drawtext can use a plain relative path (FFmpeg's fontconfig isn't set up
    /// on Windows, and absolute paths need awkward escaping). No font, no labels.
    /// </summary>
    static bool CopyLabelFont(string directory)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "segoeui.ttf", "arial.ttf" })
        {
            try
            {
                var font = Path.Combine(fonts, name);
                if (File.Exists(font))
                {
                    File.Copy(font, Path.Combine(directory, LabelFont), overwrite: true);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return false;
    }

    // A seek past the last decodable frame writes nothing; reuse a neighbour so the grid stays complete.
    async Task CopyPreviousOrBlankAsync(string tile, int index, FrameSize size, string directory, CancellationToken cancellationToken)
    {
        for (var i = index; i >= 1; i--)
        {
            var previous = Path.Combine(directory, $"tile{i:00}.png");
            if (File.Exists(previous))
            {
                File.Copy(previous, tile);
                return;
            }
        }
        await Kit.Ffmpeg.RunAsync(["-f", "lavfi", "-i", $"color=c=0x151515:s={size.Width}x{size.Height}:d=0.04", "-frames:v", "1", "-update", "1", tile],
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
