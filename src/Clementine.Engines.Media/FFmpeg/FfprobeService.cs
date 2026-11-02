using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Clementine.Core;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>
/// Runs ffprobe and turns its JSON into <see cref="MediaInfo"/>. Results are cached per (path, size, last write
/// time), so the several probes one job makes (defaults, planning, verification) cost a single ffprobe run.
/// </summary>
public sealed class FfprobeService
{
    const int CacheLimit = 256;
    static readonly ConcurrentDictionary<(string Path, long Size, DateTime Modified), MediaInfo> Cache = new();

    readonly ToolLocator _tools;

    public FfprobeService(ToolLocator tools) => _tools = tools;

    /// <summary>Probes <paramref name="path"/>; throws a friendly <see cref="OperationException"/> when the file can't be read.</summary>
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new OperationException($"\"{file.Name}\" doesn't exist any more.");

        var key = (file.FullName.ToLowerInvariant(), file.Length, file.LastWriteTimeUtc);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var ffprobe = _tools.RequireFfprobe();
        var result = await ProcessRunner.RunAsync(
            ffprobe,
            ["-v", "error", "-hide_banner", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", file.FullName],
            new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(2), StdErrTailChars = 4000 },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new OperationException(
                $"Couldn't read \"{file.Name}\" — it may be damaged or not a media file FFmpeg understands.",
                result.StdErrTail);
        }

        MediaInfo info;
        try
        {
            info = Parse(file.FullName, result.StdOut, file.Length);
        }
        catch (JsonException ex)
        {
            throw new OperationException($"Couldn't read \"{file.Name}\" — ffprobe returned something unexpected.", result.StdOut, ex);
        }

        if (Cache.Count >= CacheLimit)
            Cache.Clear();
        Cache[key] = info;
        return info;
    }

    /// <summary>Probes and insists on a video stream, with the friendly message video operations need for audio-only files.</summary>
    public async Task<MediaInfo> ProbeVideoAsync(string path, CancellationToken cancellationToken)
    {
        var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasVideo)
        {
            throw new OperationException(info.HasAudio
                ? $"\"{System.IO.Path.GetFileName(path)}\" has no picture — it's an audio-only file. Use the audio tools instead."
                : $"\"{System.IO.Path.GetFileName(path)}\" has no video stream.");
        }
        return info;
    }

    /// <summary>Probes and insists on an audio stream.</summary>
    public async Task<MediaInfo> ProbeAudioAsync(string path, CancellationToken cancellationToken)
    {
        var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasAudio)
            throw new OperationException(info.HasVideo ? "This video has no audio track." : $"\"{System.IO.Path.GetFileName(path)}\" has no audio.");
        return info;
    }

    /// <summary>Parses ffprobe's "-print_format json -show_format -show_streams" output.</summary>
    public static MediaInfo Parse(string path, string json, long size = 0)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var videos = new List<VideoStreamInfo>();
        var pictures = new List<VideoStreamInfo>();
        var audios = new List<AudioStreamInfo>();
        var subtitles = new List<SubtitleStreamInfo>();
        var others = new List<OtherStreamInfo>();

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                switch (Str(s, "codec_type"))
                {
                    case "video":
                        var video = ParseVideo(s);
                        (video.IsAttachedPicture ? pictures : videos).Add(video);
                        break;
                    case "audio":
                        audios.Add(Common(s, new AudioStreamInfo
                        {
                            SampleRate = Int(s, "sample_rate"),
                            Channels = Int(s, "channels"),
                            ChannelLayout = Str(s, "channel_layout"),
                            SampleFormat = Str(s, "sample_fmt"),
                            BitsPerSample = Math.Max(Int(s, "bits_per_raw_sample"), Int(s, "bits_per_sample")),
                        }));
                        break;
                    case "subtitle":
                        subtitles.Add(Common(s, new SubtitleStreamInfo()));
                        break;
                    default:
                        others.Add(Common(s, new OtherStreamInfo { Kind = Str(s, "codec_type") ?? "data" }));
                        break;
                }
            }
        }

        var format = root.TryGetProperty("format", out var f) ? f : default;
        var hasFormat = format.ValueKind == JsonValueKind.Object;
        var chapters = root.TryGetProperty("chapters", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;
        var formatSize = hasFormat ? Long(format, "size") : null;

        return new MediaInfo
        {
            Path = path,
            FormatName = hasFormat ? Str(format, "format_name") ?? "" : "",
            FormatLongName = hasFormat ? Str(format, "format_long_name") : null,
            Duration = hasFormat ? Seconds(format, "duration") : null,
            Size = formatSize ?? size,
            BitRate = hasFormat ? Long(format, "bit_rate") : null,
            VideoStreams = videos,
            AttachedPictures = pictures,
            AudioStreams = audios,
            SubtitleStreams = subtitles,
            OtherStreams = others,
            Tags = hasFormat ? Tags(format) : new Dictionary<string, string>(),
            ChapterCount = chapters,
        };
    }

    static VideoStreamInfo ParseVideo(JsonElement s)
    {
        var tags = Tags(s);
        var pixelFormat = Str(s, "pix_fmt");
        var codec = Str(s, "codec_name") ?? "";
        var alphaMode = tags.TryGetValue("alpha_mode", out var mode) && mode == "1";
        var bitDepth = Int(s, "bits_per_raw_sample");
        return Common(s, new VideoStreamInfo
        {
            Width = Int(s, "width"),
            Height = Int(s, "height"),
            SampleAspectRatio = Rational.Parse(Str(s, "sample_aspect_ratio")),
            FrameRate = Rational.Parse(Str(s, "avg_frame_rate")),
            RealFrameRate = Rational.Parse(Str(s, "r_frame_rate")),
            TimeBase = Rational.Parse(Str(s, "time_base")),
            PixelFormat = pixelFormat,
            HasAlpha = PixelFormats.HasAlpha(pixelFormat) && !IsOpaquePalette(codec, pixelFormat) || alphaMode || IsAlphaCodecTag(Str(s, "codec_tag_string")),
            BitDepth = bitDepth > 0 ? bitDepth : PixelFormats.BitDepth(pixelFormat),
            Rotation = Rotation(s, tags),
            FieldOrder = Str(s, "field_order"),
            ColorPrimaries = Known(Str(s, "color_primaries")),
            ColorTransfer = Known(Str(s, "color_transfer")),
            ColorSpace = Known(Str(s, "color_space")),
            ColorRange = Known(Str(s, "color_range")),
            FrameCount = Long(s, "nb_frames"),
            IsAttachedPicture = s.TryGetProperty("disposition", out var d) && Int(d, "attached_pic") == 1,
        });
    }

    // Palette formats only matter for formats that can carry transparency (GIF, PNG, APNG, WebP).
    static bool IsOpaquePalette(string codec, string? pixelFormat) =>
        pixelFormat == "pal8" && codec is not ("gif" or "png" or "apng" or "webp");

    // HAP Alpha / HAP Q Alpha / HAP alpha-only report "rgba" anyway, but be explicit for decoders that don't.
    static bool IsAlphaCodecTag(string? tag) => tag is "Hap5" or "HapM" or "HapA";

    static T Common<T>(JsonElement s, T stream) where T : StreamInfo
    {
        var tags = Tags(s);
        StreamInfo common = stream;
        return (T)(common with
        {
            Index = Int(s, "index"),
            Codec = Str(s, "codec_name") ?? "",
            CodecLongName = Str(s, "codec_long_name"),
            CodecTag = Str(s, "codec_tag_string") is { } tag && !tag.StartsWith("[", StringComparison.Ordinal) ? tag : null,
            Profile = Str(s, "profile"),
            Duration = Seconds(s, "duration"),
            BitRate = Long(s, "bit_rate"),
            Language = tags.GetValueOrDefault("language") is { } lang && lang != "und" ? lang : null,
            Title = tags.GetValueOrDefault("title"),
            IsDefault = s.TryGetProperty("disposition", out var d) && Int(d, "default") == 1,
            Tags = tags,
        });
    }

    static int Rotation(JsonElement s, IReadOnlyDictionary<string, string> tags)
    {
        double degrees = 0;
        if (s.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                    degrees = r.GetDouble();
            }
        }
        else if (tags.TryGetValue("rotate", out var legacy) && double.TryParse(legacy, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            degrees = -parsed;
        }
        var normalized = (int)Math.Round(degrees / 90.0) * 90 % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    static IReadOnlyDictionary<string, string> Tags(JsonElement e)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in t.EnumerateObject())
                tags[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText();
        }
        return tags;
    }

    static string? Known(string? value) => value is null or "unknown" or "unspecified" or "reserved" ? null : value;

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Null ? null : v.GetRawText() : null;

    static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out var i) : int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) ? i : 0;

    static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt64(out var l) : long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) ? l : null;

    static TimeSpan? Seconds(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
