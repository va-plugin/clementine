using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Clementine.Core;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>
/// Which encoders an FFmpeg build has ("ffmpeg -hide_banner -encoders"), probed once per ffmpeg path on a background
/// thread. Until the answer is in (or when the probe fails) every operation reports itself available, so the wheel
/// never waits for it; once it is known, operations whose encoder is missing say so instead of failing mid-job.
/// </summary>
public static partial class FfmpegEncoders
{
    static readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlySet<string>?>>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Starts the probe for <paramref name="ffmpeg"/> in the background (no-op when it already ran or is running).</summary>
    public static void Prefetch(string? ffmpeg)
    {
        if (!string.IsNullOrEmpty(ffmpeg))
            _ = Get(ffmpeg);
    }

    /// <summary>The build's encoder names if the probe has finished, otherwise null (and the probe is started). Never blocks.</summary>
    public static IReadOnlySet<string>? TryGetKnown(string ffmpeg)
    {
        var task = Get(ffmpeg);
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    /// <summary>Waits for the probe; null when it failed.</summary>
    public static Task<IReadOnlySet<string>?> GetAsync(string ffmpeg, CancellationToken cancellationToken) =>
        Get(ffmpeg).WaitAsync(cancellationToken);

    /// <summary>"Available" unless <paramref name="known"/> is known and lacks one of <paramref name="required"/>.</summary>
    public static Availability Check(IReadOnlySet<string>? known, IEnumerable<string> required)
    {
        if (known is null)
            return Availability.Available;
        foreach (var encoder in required)
        {
            if (!known.Contains(encoder))
                return Availability.Unavailable(MissingMessage(encoder));
        }
        return Availability.Available;
    }

    public static string MissingMessage(string encoder) =>
        $"This FFmpeg build has no {encoder} encoder — install a full build (winget install Gyan.FFmpeg)";

    /// <summary>
    /// Encoder names from "ffmpeg -encoders" output. Each entry is a line of six capability flags (the first is
    /// V, A or S), the name and a description: <c> V....D libx264   libx264 H.264 / AVC …</c>. The legend above
    /// the "------" line (<c> V..... = Video</c>) is skipped.
    /// </summary>
    public static IReadOnlySet<string> Parse(string output)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var match = EncoderLine().Match(line);
            if (match.Success && match.Groups["name"].Value != "=")
                names.Add(match.Groups["name"].Value);
        }
        return names;
    }

    /// <summary>Test hook: makes the probe for <paramref name="ffmpeg"/> answer <paramref name="encoders"/> (null = unknown) until disposed.</summary>
    internal static IDisposable Pretend(string ffmpeg, IReadOnlySet<string>? encoders)
    {
        Cache.TryGetValue(ffmpeg, out var previous);
        Cache[ffmpeg] = new Lazy<Task<IReadOnlySet<string>?>>(Task.FromResult(encoders));
        return new Restore(() =>
        {
            if (previous is null)
                Cache.TryRemove(ffmpeg, out _);
            else
                Cache[ffmpeg] = previous;
        });
    }

    sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    static Task<IReadOnlySet<string>?> Get(string ffmpeg) =>
        Cache.GetOrAdd(ffmpeg, path => new Lazy<Task<IReadOnlySet<string>?>>(() => Task.Run(() => ProbeAsync(path)))).Value;

    static async Task<IReadOnlySet<string>?> ProbeAsync(string ffmpeg)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                ffmpeg,
                ["-nostdin", "-hide_banner", "-encoders"],
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(20), StdErrTailChars = 2000 }).ConfigureAwait(false);
            if (!result.Succeeded)
                return null;
            var names = Parse(result.StdOut);
            return names.Count > 0 ? names : null;
        }
        catch (Exception)
        {
            // A broken or missing ffmpeg: say nothing here — "Requires FFmpeg" or the run itself reports it.
            return null;
        }
    }

    [GeneratedRegex(@"^\s*[VAS][A-Z.]{5}\s+(?<name>\S+)")]
    private static partial Regex EncoderLine();
}
