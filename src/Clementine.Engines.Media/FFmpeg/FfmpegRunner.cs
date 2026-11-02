using Clementine.Core;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>How one FFmpeg run reports progress and where it runs.</summary>
public sealed record FfmpegRunOptions
{
    /// <summary>Length of the output being written; drives the progress fraction (null = indeterminate).</summary>
    public TimeSpan? ExpectedDuration { get; init; }

    public IProgress<JobProgress>? Progress { get; init; }

    /// <summary>Status text shown with the progress bar ("Encoding ProRes…").</summary>
    public string Status { get; init; } = "Encoding…";

    /// <summary>Maps this run's 0..1 onto a slice of the job (e.g. pass 2 of 2 = 0.5..1.0).</summary>
    public double ProgressStart { get; init; }

    public double ProgressSpan { get; init; } = 1;

    public string? WorkingDirectory { get; init; }

    /// <summary>FFmpeg log level. "error" keeps stderr short; analysis passes (loudnorm, silencedetect) need "info".</summary>
    public string LogLevel { get; init; } = "error";

    /// <summary>Message used when FFmpeg fails and no specific pattern matches.</summary>
    public string? FailureMessage { get; init; }

    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// Runs ffmpeg with an argument list: adds the standard flags (-nostdin -hide_banner -progress pipe:1 …), reports
/// progress, kills the process on cancellation, and turns failures into friendly <see cref="OperationException"/>s.
/// </summary>
public sealed class FfmpegRunner
{
    readonly ToolLocator _tools;
    readonly ILog _log;

    public FfmpegRunner(ToolLocator tools, ILog log)
    {
        _tools = tools;
        _log = log;
    }

    /// <summary>The flags every invocation starts with.</summary>
    public static IReadOnlyList<string> StandardArguments(string logLevel = "error") =>
        ["-nostdin", "-hide_banner", "-nostats", "-loglevel", logLevel, "-progress", "pipe:1", "-stats_period", "0.25"];

    /// <summary>Runs ffmpeg and throws a friendly <see cref="OperationException"/> if it fails. Returns stderr (for analysis passes).</summary>
    public async Task<string> RunAsync(IReadOnlyList<string> arguments, FfmpegRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new FfmpegRunOptions();
        var ffmpeg = _tools.RequireFfmpeg();
        var parser = new FfmpegProgressParser(options.ExpectedDuration);
        var progress = options.Progress;

        void Report(double? fraction) =>
            progress?.Report(new JobProgress(
                fraction is { } f ? options.ProgressStart + f * options.ProgressSpan : null,
                options.Status));

        Report(options.ExpectedDuration is null ? null : 0);
        var args = new List<string>(StandardArguments(options.LogLevel));
        args.AddRange(arguments);

        _log.Info($"ffmpeg {string.Join(' ', args.Select(Quote))}");
        var result = await ProcessRunner.RunAsync(
            ffmpeg,
            args,
            new ProcessRunOptions
            {
                WorkingDirectory = options.WorkingDirectory,
                CaptureStdOut = false,
                // FFmpeg prints "progress=end" even when it fails (e.g. a GPU encoder that won't open), so the end is
                // only reported once the exit code says it worked — a failed run never flashes 100 %.
                OnStdOut = line =>
                {
                    if (parser.Feed(line) && !parser.IsFinished)
                        Report(parser.Fraction);
                },
                StdErrTailChars = options.LogLevel == "error" ? 16_000 : 256_000,
                Timeout = options.Timeout,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var message = FfmpegErrors.Describe(result.StdErrTail, options.FailureMessage ?? "FFmpeg couldn't process this file.");
            throw new OperationException(message, Tail(result.StdErrTail, 6000));
        }
        if (options.ExpectedDuration is not null)
            Report(1);
        return result.StdErrTail;
    }

    /// <summary>Shell-style quoting for log lines only (arguments are passed as a list, never joined).</summary>
    static string Quote(string arg) => arg.Length == 0 || arg.Any(char.IsWhiteSpace) || arg.Contains('"') ? $"\"{arg.Replace("\"", "\\\"")}\"" : arg;

    static string Tail(string text, int max) => text.Length <= max ? text.Trim() : "…" + text[^max..].Trim();
}
