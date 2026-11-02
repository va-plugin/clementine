using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media;

/// <summary>
/// The FFmpeg services every media operation shares: probing, running ffmpeg, GPU encoder discovery and the
/// "encode this source to that target" workflow with automatic software fallback.
/// </summary>
public sealed class MediaToolkit
{
    public MediaToolkit(EngineContext context)
    {
        Context = context;
        Probe = new FfprobeService(context.Tools);
        Ffmpeg = new FfmpegRunner(context.Tools, context.Log);
        // Learn which encoders this build has while the user is still dragging (locating ffmpeg may touch the disk,
        // so even that stays off the calling thread).
        Task.Run(() => FfmpegEncoders.Prefetch(context.Tools.Ffmpeg));
    }

    public EngineContext Context { get; }

    public FfprobeService Probe { get; }

    public FfmpegRunner Ffmpeg { get; }

    public ClementineSettings Settings => Context.Settings;

    public ILog Log => Context.Log;

    /// <summary>
    /// "Requires FFmpeg" when it can't be found; "This FFmpeg build has no … encoder" when the build's encoder list is
    /// known and lacks one of <paramref name="encoders"/>. Never waits for the encoder probe (available until known).
    /// </summary>
    public Availability CheckFfmpeg(IReadOnlyList<string>? encoders = null)
    {
        var ffmpeg = Context.Tools.Ffmpeg;
        if (ffmpeg is null || Context.Tools.Ffprobe is null)
            return Availability.Unavailable("Requires FFmpeg (free) — install it or set its location in Settings → Tools");
        return encoders is { Count: > 0 } ? FfmpegEncoders.Check(FfmpegEncoders.TryGetKnown(ffmpeg), encoders) : Availability.Available;
    }

    /// <summary>
    /// Encodes <paramref name="source"/> to <paramref name="outputPath"/>. Uses a GPU encoder when allowed and working,
    /// and silently retries in software if the GPU encode fails. On failure or cancellation the partial file is removed.
    /// </summary>
    public async Task<VideoEncodePlan> EncodeVideoAsync(
        MediaInfo source,
        VideoTarget target,
        VideoEdit edit,
        string outputPath,
        IProgress<JobProgress>? progress,
        string status,
        CancellationToken cancellationToken,
        double progressStart = 0,
        double progressSpan = 1,
        string? workingDirectory = null)
    {
        var settings = Settings;
        // One monotonic view for both attempts: a GPU attempt that fails part-way must not make the bar jump back.
        progress = progress is null ? null : MonotonicProgress.Wrap(progress);
        HardwareEncoder? hardware = null;
        if (target.AllowHardware && settings.UseHardwareEncoding && target.Codec is VideoCodec.H264 or VideoCodec.Hevc)
        {
            // Later stages of a multi-part job keep their bar instead of flipping back to "indeterminate".
            progress?.Report(new JobProgress(progressStart > 0 ? progressStart : null, "Checking the graphics card…"));
            hardware = await HardwareEncoders.FindAsync(Context.Tools.RequireFfmpeg(), target.Codec == VideoCodec.Hevc, cancellationToken).ConfigureAwait(false);
        }

        var plan = VideoEncodePlanner.Build(source, target, edit, outputPath, settings, hardware);
        var options = new FfmpegRunOptions
        {
            ExpectedDuration = plan.ExpectedDuration,
            Progress = progress,
            Status = status,
            ProgressStart = progressStart,
            ProgressSpan = progressSpan,
            WorkingDirectory = workingDirectory,
        };
        try
        {
            await Ffmpeg.RunAsync(plan.Arguments, options, cancellationToken).ConfigureAwait(false);
            return plan;
        }
        catch (OperationException ex) when (hardware is not null && !cancellationToken.IsCancellationRequested)
        {
            Log.Warn($"{plan.VideoEncoder} failed ({ex.Message}); retrying with the software encoder.");
            await OutputFiles.DeleteQuietlyAsync(outputPath).ConfigureAwait(false);
        }
        catch
        {
            await OutputFiles.DeleteQuietlyAsync(outputPath).ConfigureAwait(false);
            throw;
        }

        plan = VideoEncodePlanner.Build(source, target, edit, outputPath, settings, hardware: null);
        try
        {
            await Ffmpeg.RunAsync(plan.Arguments, options, cancellationToken).ConfigureAwait(false);
            return plan;
        }
        catch
        {
            await OutputFiles.DeleteQuietlyAsync(outputPath).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs ffmpeg writing <paramref name="outputPath"/>, removing the partial output if it fails or is cancelled.</summary>
    public async Task<string> RunToFileAsync(IReadOnlyList<string> arguments, string outputPath, FfmpegRunOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await Ffmpeg.RunAsync(arguments, options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await OutputFiles.DeleteQuietlyAsync(outputPath).ConfigureAwait(false);
            throw;
        }
    }
}
