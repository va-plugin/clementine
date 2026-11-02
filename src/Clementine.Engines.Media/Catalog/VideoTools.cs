using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;
using Clementine.Engines.Media.Video;

namespace Clementine.Engines.Media.Catalog;

/// <summary>The Tools-wheel entries for videos. Re-encodes keep the source's codec (ProRes stays ProRes, HAP stays HAP…).</summary>
internal static class VideoTools
{
    public static IEnumerable<IOperation> Create(MediaToolkit kit)
    {
        // Compress
        yield return new CompressVideoOperation(Info("video.compress.good", "Compress", "Good quality", 10, "Compress · good quality",
            "Re-encodes to an efficient H.264 MP4 (H.265 stays H.265) at the same size — usually much smaller.", "compress", ".mp4"), kit, VideoCompression.GoodQuality);
        yield return new CompressVideoOperation(Info("video.compress.smaller", "Compress", "Smaller (≤ 1080p)", 11, "Compress · smaller",
            "Stronger compression and at most 1080p — for sharing and email.", "compress", ".mp4"), kit, VideoCompression.Smaller);
        yield return new CompressVideoOperation(Info("video.compress.target", "Compress", "Target size…", 12, "Compress to a target size",
            "Two-pass encode that lands close to a size or percentage you choose.", "compress", ".mp4"), kit, VideoCompression.TargetSize);

        // Trim / crop / resize
        yield return new TrimVideoOperation(Info("video.trim", "Trim", null, 20, "Trim",
            "Keeps the part between a start and an end time — instant and lossless unless you ask for frame-exact cuts.", "trim"), kit);
        yield return Edit(kit, Info("video.crop", "Crop", null, 30, "Crop",
            "Crops to 16:9, 9:16, 1:1, 4:5, 4:3, 21:9 or a custom size.", "crop"), "Cropping…", CropPlan, CropOptions());
        yield return Edit(kit, Info("video.resize.1080p", "Resize", "1080p", 40, "Resize to 1080p",
            "Scales so the short side is 1080 px, keeping the aspect ratio.", "resize"), "Resizing…",
            (source, _) => Resize(source, s => VideoGeometry.ShortSide(s, 1080), "1080p"));
        yield return Edit(kit, Info("video.resize.720p", "Resize", "720p", 41, "Resize to 720p",
            "Scales so the short side is 720 px, keeping the aspect ratio.", "resize"), "Resizing…",
            (source, _) => Resize(source, s => VideoGeometry.ShortSide(s, 720), "720p"));
        yield return Edit(kit, Info("video.resize.50", "Resize", "50%", 42, "Resize to 50%",
            "Halves the width and height.", "resize"), "Resizing…",
            (source, _) => Resize(source, s => VideoGeometry.Percent(s, 50), "50%"));
        yield return Edit(kit, Info("video.resize.custom", "Resize", "Custom…", 43, "Resize to a custom size",
            "Fits the video into a width and/or height you choose, keeping the aspect ratio.", "resize"), "Resizing…",
            (source, options) =>
            {
                var width = options.GetInt("width", 1280);
                var height = options.GetInt("height", 0);
                return Resize(source, s => VideoGeometry.Fit(s, width, height), null);
            },
            new OptionSchema("Resize",
            [
                new NumberField("width", "Width", 1280, 0, 8192) { Unit = "px", Help = "0 = follow the height" },
                new NumberField("height", "Height", 0, 0, 8192) { Unit = "px", Help = "0 = follow the width" },
            ])
            { Description = "The aspect ratio is always kept; sizes are rounded to even numbers.", ConfirmLabel = "Resize" });

        // Speed / frame rate
        yield return Edit(kit, Info("video.speed.half", "Speed", "0.5×", 50, "Half speed (0.5×)",
            "Slow motion at half speed; audio is stretched without changing pitch.", "speed"), "Changing speed…", (source, _) => Speed(source, 0.5, false));
        yield return Edit(kit, Info("video.speed.double", "Speed", "2×", 51, "Double speed (2×)",
            "Plays twice as fast; audio keeps its pitch.", "speed"), "Changing speed…", (source, _) => Speed(source, 2, false));
        yield return Edit(kit, Info("video.speed.custom", "Speed", "Custom…", 52, "Change speed",
            "Any speed from 0.1× to 10×, with or without the sound.", "speed"), "Changing speed…",
            (source, options) => Speed(source, Math.Clamp(options.GetDouble("factor", 1.5), 0.1, 10), options.GetBool("dropAudio", false)),
            new OptionSchema("Speed",
            [
                new NumberField("factor", "Speed", 1.5, 0.1, 10) { Step = 0.05, Decimals = 2, Unit = "×" },
                new BoolField("dropAudio", "Remove the sound", false),
            ])
            { ConfirmLabel = "Apply" });
        var order = 60;
        foreach (var fps in new[] { 24, 25, 30, 50, 60 })
        {
            yield return Edit(kit, Info($"video.fps.{fps}", "Frame rate", $"{fps} fps", order++, $"Change frame rate to {fps} fps",
                $"Converts to a constant {fps} frames per second (frames are dropped or repeated, never sped up).", "film"), "Changing frame rate…",
                (_, _) => new VideoEditPlan(new VideoEdit { FrameRate = new Rational(fps, 1) }, $"{fps} fps"));
        }

        // Rotate / flip
        yield return Edit(kit, Info("video.rotate.right", "Rotate", "90° right", 70, "Rotate 90° clockwise", "Turns the picture a quarter turn clockwise.", "rotate"),
            "Rotating…", (source, _) => Transform(source, "transpose=clock", swap: true, "rotated"));
        yield return Edit(kit, Info("video.rotate.left", "Rotate", "90° left", 71, "Rotate 90° counter-clockwise", "Turns the picture a quarter turn counter-clockwise.", "rotate"),
            "Rotating…", (source, _) => Transform(source, "transpose=cclock", swap: true, "rotated"));
        yield return Edit(kit, Info("video.rotate.180", "Rotate", "180°", 72, "Rotate 180°", "Turns the picture upside down.", "rotate"),
            "Rotating…", (source, _) => Transform(source, "hflip,vflip", swap: false, "rotated"));
        yield return Edit(kit, Info("video.flip.horizontal", "Rotate", "Flip horizontal", 73, "Flip horizontally", "Mirrors the picture left to right.", "flip"),
            "Flipping…", (source, _) => Transform(source, "hflip", swap: false, "flipped"));
        yield return Edit(kit, Info("video.flip.vertical", "Rotate", "Flip vertical", 74, "Flip vertically", "Mirrors the picture top to bottom.", "flip"),
            "Flipping…", (source, _) => Transform(source, "vflip", swap: false, "flipped"));

        // Loops
        yield return new ReverseVideoOperation(Info("video.pingpong", "Ping-pong", null, 80, "Ping-pong loop",
            "Plays forwards then backwards for a seamless VJ loop (turnaround frames aren't doubled).", "loop"), kit, pingPong: true);
        yield return new ReverseVideoOperation(Info("video.reverse", "Reverse", null, 81, "Reverse",
            "Plays the clip backwards, sound included.", "reverse"), kit, pingPong: false);

        // Split / join
        yield return new SplitVideoOperation(Info("video.split", "Split", null, 90, "Split",
            "Cuts the video into equal parts or fixed-length pieces, saved in a folder.", "split", null, OperationWeight.Heavy), kit);
        yield return new JoinVideoOperation(Info("video.join", "Join", null, 91, "Join videos",
            "Joins the videos end to end in name order — losslessly when they match.", "join"), kit);

        // Snapshots
        yield return new SnapshotOperation(Info("video.snapshot.first", "Snapshot", "First frame", 100, "Snapshot · first frame",
            "Saves the first frame as a full-size PNG.", "snapshot", ".png", OperationWeight.Light), kit, SnapshotPosition.First);
        yield return new SnapshotOperation(Info("video.snapshot.middle", "Snapshot", "Middle frame", 101, "Snapshot · middle frame",
            "Saves the middle frame as a full-size PNG.", "snapshot", ".png", OperationWeight.Light), kit, SnapshotPosition.Middle);
        yield return new SnapshotOperation(Info("video.snapshot.at", "Snapshot", "At time…", 102, "Snapshot at a time",
            "Saves the frame at the time you choose as a PNG.", "snapshot", ".png", OperationWeight.Light), kit, SnapshotPosition.AtTime);
        yield return new ContactSheetOperation(Info("video.snapshot.sheet", "Snapshot", "Contact sheet", 103, "Contact sheet",
            "A 4 × 4 grid of evenly spaced frames in one PNG.", "grid", ".png"), kit);
        yield return new ExtractFramesOperation(Info("video.snapshot.frames", "Snapshot", "All frames", 104, "All frames as PNG",
            "Saves every frame as a numbered PNG (transparency kept) in a folder.", "frames", null), kit);

        // Streams & metadata
        yield return new RemoveAudioOperation(Info("video.remove-audio", "Remove audio", null, 110, "Remove audio",
            "Copies the video without its sound — instant, no quality loss.", "mute", null, OperationWeight.Light), kit);
        yield return new ExtractSubtitlesOperation(Info("video.subtitles.extract", "Subtitles", "Extract (.srt)", 120, "Extract subtitles",
            "Saves the first subtitle track as an .srt file.", "subtitle", ".srt", OperationWeight.Light), kit);
        yield return new BurnSubtitlesOperation(Info("video.subtitles.burn", "Subtitles", "Burn in", 121, "Burn in subtitles",
            "Drop a video with an .srt/.ass/.vtt file to render the subtitles into the picture.", "subtitle"), kit);
        yield return new RemoveMetadataOperation(Info("video.metadata.remove", "Metadata", null, 130, "Remove metadata",
            "Strips tags, chapters, GPS location and cover art without re-encoding.", "metadata", null, OperationWeight.Light), kit, audio: false);
        yield return new MediaInfoOperation(Info("video.info", "Info", null, 140, "Video info",
            "Shows codec, resolution, frame rate, colour, alpha and audio details.", "info", null, OperationWeight.Light), kit, audio: false);

        // Stills → video
        yield return new ImageSequenceOperation(Info("sequence.video", "To video", null, 150, "Image sequence → video",
            "Turns the selected images into a video (H.264, ProRes 4444, HAP Alpha or DXV) in name order.", "film"), kit);
    }

    static VideoEditOperation Edit(MediaToolkit kit, OperationInfo info, string status, Func<MediaInfo, OptionValues, VideoEditPlan> plan, OptionSchema? options = null) =>
        new(info, kit, status, plan, options);

    static VideoEditPlan Resize(MediaInfo source, Func<FrameSize, FrameSize> size, string? suffix)
    {
        var target = size(VideoGeometry.DisplaySize(source.Video!));
        return new VideoEditPlan(new VideoEdit { ScaleTo = target }, suffix ?? $"{target.Width}x{target.Height}");
    }

    static VideoEditPlan Speed(MediaInfo source, double factor, bool dropAudio)
    {
        var video = source.Video!;
        var factorText = factor.ToString("0.######", CultureInfo.InvariantCulture);
        var edit = new VideoEdit
        {
            VideoFilters = [$"setpts=PTS/{factorText}", $"fps={video.BestFrameRate}:eof_action=pass"],
            AudioFilters = dropAudio ? [] : VideoGeometry.AtempoChain(factor),
            DropAudio = dropAudio,
            Speed = factor,
        };
        return new VideoEditPlan(edit, VideoGeometry.SpeedLabel(factor));
    }

    static VideoEditPlan Transform(MediaInfo source, string filter, bool swap, string suffix)
    {
        var size = VideoGeometry.DisplaySize(source.Video!);
        return new VideoEditPlan(new VideoEdit
        {
            VideoFilters = [filter],
            FrameSize = swap ? new FrameSize(size.Height, size.Width) : size,
        }, suffix);
    }

    static VideoEditPlan CropPlan(MediaInfo source, OptionValues options)
    {
        var frame = VideoGeometry.DisplaySize(source.Video!);
        var anchor = Enum.TryParse<CropAnchor>(options.GetString("anchor", "center"), ignoreCase: true, out var a) ? a : CropAnchor.Center;
        var aspect = options.GetString("aspect", "16:9");
        var (size, x, y) = aspect == "custom"
            ? VideoGeometry.CropToSize(frame, options.GetInt("width", 1080), options.GetInt("height", 1080), anchor)
            : VideoGeometry.CropToAspect(frame, Rational.Parse(aspect).Value is > 0 and var value ? value : 16 / 9.0, anchor);
        var edit = new VideoEdit { VideoFilters = [$"crop={size.Width}:{size.Height}:{x}:{y}"], FrameSize = size };
        return new VideoEditPlan(edit, aspect == "custom" ? $"{size.Width}x{size.Height}" : aspect.Replace(':', 'x'));
    }

    static OptionSchema CropOptions() => new("Crop",
    [
        new ChoiceField("aspect", "Shape",
        [
            new OptionChoice("16:9", "16:9"), new OptionChoice("9:16", "9:16"), new OptionChoice("1:1", "1:1"), new OptionChoice("4:5", "4:5"),
            new OptionChoice("4:3", "4:3"), new OptionChoice("21:9", "21:9"), new OptionChoice("custom", "Custom"),
        ], "9:16"),
        new NumberField("width", "Width", 1080, 2, 8192) { Unit = "px", VisibleWhen = new OptionCondition("aspect", "custom") },
        new NumberField("height", "Height", 1080, 2, 8192) { Unit = "px", VisibleWhen = new OptionCondition("aspect", "custom") },
        new ChoiceField("anchor", "Keep",
        [
            new OptionChoice("center", "Centre"), new OptionChoice("top", "Top"), new OptionChoice("bottom", "Bottom"),
            new OptionChoice("left", "Left"), new OptionChoice("right", "Right"),
        ], "center"),
    ])
    {
        ConfirmLabel = "Crop",
    };

    static OperationInfo Info(
        string id, string group, string? variant, int order, string title, string description, string icon,
        string? extension = null, OperationWeight weight = OperationWeight.Heavy) => new()
    {
        Id = id,
        Category = OperationCategory.Tool,
        Group = group,
        // Groups shared with the audio tools (Trim, Info, Join…) need no "Video" label: video and audio files never
        // see each other's tools on one wheel.
        Variant = variant,
        Title = title,
        Description = description,
        IconKey = icon,
        OutputExtension = extension,
        Order = order,
        Weight = weight,
    };
}
