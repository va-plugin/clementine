using System.Collections.Concurrent;
using Clementine.Core;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>GPU encoder families, in the order Clementine prefers them.</summary>
public enum HardwareVendor
{
    Nvidia,
    IntelQuickSync,
    Amd,
}

/// <summary>A GPU encoder that passed a probe encode on this machine.</summary>
public sealed record HardwareEncoder(string Name, HardwareVendor Vendor, bool IsHevc);

/// <summary>
/// Finds a working GPU encoder for H.264 / H.265 (NVENC → Quick Sync → AMF). "Listed by ffmpeg -encoders" isn't
/// enough — the driver and GPU must accept a real encode — so each candidate gets a 0.2 s test encode. Results are
/// cached for the life of the process.
/// </summary>
public static class HardwareEncoders
{
    static readonly ConcurrentDictionary<(string Ffmpeg, bool Hevc), Lazy<Task<HardwareEncoder?>>> Cache = new();

    static readonly (string H264, string Hevc, HardwareVendor Vendor)[] Candidates =
    [
        ("h264_nvenc", "hevc_nvenc", HardwareVendor.Nvidia),
        ("h264_qsv", "hevc_qsv", HardwareVendor.IntelQuickSync),
        ("h264_amf", "hevc_amf", HardwareVendor.Amd),
    ];

    /// <summary>The first GPU encoder that works for the codec, or null (use software).</summary>
    public static Task<HardwareEncoder?> FindAsync(string ffmpeg, bool hevc, CancellationToken cancellationToken)
    {
        var lazy = Cache.GetOrAdd((ffmpeg, hevc), key => new Lazy<Task<HardwareEncoder?>>(() => ProbeAllAsync(key.Ffmpeg, key.Hevc)));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Test hook: makes <see cref="FindAsync"/> return <paramref name="encoder"/> until the result is disposed.</summary>
    internal static IDisposable Pretend(string ffmpeg, bool hevc, HardwareEncoder? encoder)
    {
        var key = (ffmpeg, hevc);
        Cache.TryGetValue(key, out var previous);
        Cache[key] = new Lazy<Task<HardwareEncoder?>>(Task.FromResult(encoder));
        return new Restore(() =>
        {
            if (previous is null)
                Cache.TryRemove(key, out _);
            else
                Cache[key] = previous;
        });
    }

    sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    static async Task<HardwareEncoder?> ProbeAllAsync(string ffmpeg, bool hevc)
    {
        foreach (var (h264, hevcName, vendor) in Candidates)
        {
            var name = hevc ? hevcName : h264;
            if (await ProbeAsync(ffmpeg, name).ConfigureAwait(false))
                return new HardwareEncoder(name, vendor, hevc);
        }
        return null;
    }

    /// <summary>Encodes 0.2 s of black at 256×256 to the null muxer.</summary>
    static async Task<bool> ProbeAsync(string ffmpeg, string encoder)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                ffmpeg,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=256x256:r=30:d=0.2",
                 "-pix_fmt", encoder.EndsWith("_qsv", StringComparison.Ordinal) ? "nv12" : "yuv420p", "-c:v", encoder, "-f", "null", "-"],
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(15), CaptureStdOut = false, StdErrTailChars = 2000 }).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch (OperationException)
        {
            return false;
        }
    }

    /// <summary>Quality-oriented rate control arguments for a GPU encoder (roughly x264 CRF 20 / x265 CRF 23).</summary>
    public static IReadOnlyList<string> QualityArguments(HardwareEncoder encoder) => encoder.Vendor switch
    {
        HardwareVendor.Nvidia => ["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", encoder.IsHevc ? "24" : "21", "-b:v", "0", "-spatial-aq", "1"],
        HardwareVendor.IntelQuickSync => ["-preset", "medium", "-global_quality", encoder.IsHevc ? "24" : "21"],
        _ => ["-quality", "quality", "-rc", "cqp", "-qp_i", encoder.IsHevc ? "22" : "20", "-qp_p", encoder.IsHevc ? "24" : "22"],
    };

    /// <summary>Pixel format the encoder wants for 8-bit 4:2:0 input.</summary>
    public static string PixelFormat(HardwareEncoder encoder) => encoder.Vendor == HardwareVendor.Nvidia ? "yuv420p" : "nv12";
}
