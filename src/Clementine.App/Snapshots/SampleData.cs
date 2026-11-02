using System.IO;
using Clementine.Core;

namespace Clementine.App.Snapshots;

/// <summary>
/// Sample operations, schemas and files for <c>--snapshot-ui</c>, so every dialog and window can be previewed even
/// while the engines are still empty. Ids use the shell's own prefix and never reach the real catalog.
/// </summary>
internal static class SampleData
{
    const string Prefix = "action.snapshot-sample.";

    /// <summary>Empty placeholder files with realistic names (the catalog only looks at extensions).</summary>
    public sealed record Files(
        string Directory,
        IReadOnlyList<string> Videos,
        string Psd,
        string Pdf,
        string Audio,
        string Folder,
        IReadOnlyList<string> Images,
        IReadOnlyList<string> Others);

    public static Files CreateFiles(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        string Touch(string name)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                File.WriteAllBytes(path, []);
            return path;
        }

        var folder = Path.Combine(directory, "Render Output");
        System.IO.Directory.CreateDirectory(folder);
        return new Files(
            directory,
            [Touch("Festival Opener 4K.mov"), Touch("Tunnel Loop – Alpha.mov"), Touch("Crowd Cam 03.mp4")],
            Touch("Poster Layers.psd"),
            Touch("Setlist 2026.pdf"),
            Touch("Interview.wav"),
            folder,
            [Touch("Moodboard 01.png"), Touch("Moodboard 02.png"), Touch("Moodboard 03.jpg")],
            [Touch("Stage Visuals.zip"), Touch("Interview.srt"), Touch("Rider.docx"), Touch("Budget.xlsx"), Touch("Notes.md")]);
    }

    // ===== Schemas (mirroring docs/SPEC.md) =====

    public static OptionSchema TrimSchema => new("Trim",
    [
        new TimeField("start", "Start", TimeSpan.Zero),
        new TimeField("end", "End", TimeSpan.FromSeconds(12.48)) { Help = "Defaults to the end of the clip." },
        new BoolField("precise", "Precise (re-encode)", false) { Help = "Cuts exactly on the frame you chose. Slower; the default copies streams instantly." },
    ])
    {
        ConfirmLabel = "Trim",
    };

    public static OptionSchema CompressSchema => new("Compress",
    [
        new ChoiceField("mode", "Target", [new OptionChoice("percent", "Quality"), new OptionChoice("size", "File size")], "percent"),
        new NumberField("quality", "Quality", 70, 10, 100) { Unit = "%", Step = 5, VisibleWhen = new OptionCondition("mode", "percent") },
        new NumberField("size", "Target size", 50, 1, 4000) { Unit = "MB", Decimals = 1, Step = 0.5, VisibleWhen = new OptionCondition("mode", "size"), Help = "Two-pass encode; the result lands within a few percent." },
        new ChoiceField("maxres", "Max resolution",
            [new OptionChoice("source", "Same as source"), new OptionChoice("2160", "4K (2160p)"), new OptionChoice("1080", "1080p"), new OptionChoice("720", "720p"), new OptionChoice("480", "480p")],
            "source"),
        new ChoiceField("codec", "Codec", [new OptionChoice("h264", "H.264"), new OptionChoice("h265", "H.265")], "h264"),
    ])
    {
        Description = "Re-encodes with the codec that matches the source where possible.",
        ConfirmLabel = "Compress",
    };

    public static OptionSchema PdfRangesSchema => new("Split PDF",
    [
        new TextField("ranges", "Page ranges", "1-3, 5, 8-") { Placeholder = "e.g. 1-3, 5, 8-", Help = "Each range becomes its own PDF. \"8-\" means page 8 to the end." },
        new BoolField("single", "One PDF per range", true),
    ])
    {
        ConfirmLabel = "Split",
    };

    public static OptionSchema PasswordSchema => new("Protect with password",
    [
        new TextField("password", "Password", "") { IsPassword = true },
        new TextField("confirm", "Confirm password", "") { IsPassword = true },
        new BoolField("printing", "Allow printing", true),
    ])
    {
        ConfirmLabel = "Protect",
    };

    /// <summary>One of every field type, with a VisibleWhen chain, for design review.</summary>
    public static OptionSchema EveryFieldSchema => new("Image sequence → video",
    [
        new NumberField("fps", "Frame rate", 30, 1, 120) { Unit = "fps", Help = "Frames per second of the finished clip." },
        new ChoiceField("codec", "Codec",
            [new OptionChoice("h264", "H.264 MP4"), new OptionChoice("prores", "ProRes 4444 α"), new OptionChoice("hap", "HAP Alpha"), new OptionChoice("dxv", "DXV")],
            "prores"),
        new ChoiceField("audio", "Audio", [new OptionChoice("none", "None"), new OptionChoice("pcm", "PCM")], "none"),
        new BoolField("loop", "Ping-pong loop", false) { Help = "Plays forward then backward for a seamless VJ loop." },
        new NumberField("repeat", "Repeat", 2, 1, 16) { VisibleWhen = new OptionCondition("loop", "true"), Unit = "×" },
        new TimeField("duration", "Hold last frame", TimeSpan.FromSeconds(1.5)),
        new TextField("notes", "Notes", "Shot for the main stage.\nKeep the alpha.") { Multiline = true, Placeholder = "Anything to remember…" },
        new FolderField("folder", "Save into"),
    ])
    {
        Description = "Natural sort: frame 2 comes before frame 10.",
        ConfirmLabel = "Create video",
    };

    // ===== Operations =====

    /// <summary>The media engine's reason when FFmpeg can't be found (MediaToolkit.CheckFfmpeg).</summary>
    public const string RequiresFfmpeg = "Requires FFmpeg (free) — install it or set its location in Settings → Tools";

    /// <param name="missingFfmpeg">Mark video and audio operations unavailable the way the media engine does without FFmpeg.</param>
    public static IReadOnlyList<IOperation> Operations(bool missingFfmpeg = false)
    {
        var list = new List<IOperation>();
        void Convert(string id, string group, string? variant, string title, string description, string icon, int order, string ext, FileKind kind, OptionSchema? options = null, bool heavy = true) =>
            list.Add(Op(id, OperationCategory.Convert, group, variant, title, description, icon, order, ext, kind, options, heavy, missingFfmpeg));
        void Tool(string id, string group, string? variant, string title, string description, string icon, int order, FileKind kind, OptionSchema? options = null) =>
            list.Add(Op(id, OperationCategory.Tool, group, variant, title, description, icon, order, null, kind, options, heavy: true, missingFfmpeg));

        // Video conversions
        Convert("mp4.h264", "MP4", "H.264", "MP4 · H.264", "Plays everywhere. Good quality at a sensible size.", "video", 10, ".mp4", FileKind.Video);
        Convert("mp4.h264.noaudio", "MP4", "H.264 · no audio", "MP4 · H.264 · no audio", "Video only.", "video", 11, ".mp4", FileKind.Video);
        Convert("mp4.h265", "MP4", "H.265", "MP4 · H.265", "Smaller files, newer devices.", "video", 12, ".mp4", FileKind.Video);
        Convert("mov.h264", "MOV", "H.264", "MOV · H.264", "QuickTime container.", "video", 20, ".mov", FileKind.Video);
        Convert("mov.remux", "MOV", "Remux", "MOV · remux", "Changes the container without re-encoding.", "video", 21, ".mov", FileKind.Video);
        Convert("webm.vp9", "WebM", "VP9", "WebM · VP9", "For the web.", "video", 30, ".webm", FileKind.Video);
        Convert("webm.vp9.alpha", "WebM", "VP9 · alpha", "WebM · VP9 · alpha", "Keeps transparency.", "video", 31, ".webm", FileKind.Video);
        Convert("gif", "GIF", "High quality", "GIF · high quality", "Two-pass palette, loops forever.", "gif", 40, ".gif", FileKind.Video);
        Convert("gif.small", "GIF", "Small (480 px)", "GIF · small", "480 px wide.", "gif", 41, ".gif", FileKind.Video);
        Convert("dxv", "DXV", "With audio", "DXV3 · with audio", "Resolume's codec. No alpha — use HAP Alpha or ProRes 4444 for transparency.", "vj", 100, ".mov", FileKind.Video);
        Convert("dxv.noaudio", "DXV", "No audio", "DXV3 · no audio", "Resolume's codec, video only.", "vj", 101, ".mov", FileKind.Video);
        Convert("hap", "HAP", "HAP", "HAP · with audio", "GPU-decoded VJ codec.", "vj", 110, ".mov", FileKind.Video);
        Convert("hap.noaudio", "HAP", "HAP · no audio", "HAP · no audio", "GPU-decoded VJ codec, video only.", "vj", 111, ".mov", FileKind.Video);
        Convert("hap.alpha", "HAP", "HAP Alpha", "HAP Alpha · with audio", "Keeps transparency.", "vj", 112, ".mov", FileKind.Video);
        Convert("hap.alpha.noaudio", "HAP", "HAP Alpha · no audio", "HAP Alpha · no audio", "Keeps transparency, video only.", "vj", 113, ".mov", FileKind.Video);
        Convert("hap.q", "HAP", "HAP Q", "HAP Q · with audio", "Higher quality HAP.", "vj", 114, ".mov", FileKind.Video);
        Convert("hap.q.noaudio", "HAP", "HAP Q · no audio", "HAP Q · no audio", "Higher quality HAP, video only.", "vj", 115, ".mov", FileKind.Video);
        Convert("prores.422hq", "ProRes", "422 HQ", "ProRes 422 HQ · with audio", "Edit-friendly master quality.", "film", 120, ".mov", FileKind.Video);
        Convert("prores.422hq.noaudio", "ProRes", "422 HQ · no audio", "ProRes 422 HQ · no audio", "Video only.", "film", 121, ".mov", FileKind.Video);
        Convert("prores.4444", "ProRes", "4444 α · audio", "ProRes 4444 · alpha · with audio", "Keeps transparency, with PCM audio.", "film", 122, ".mov", FileKind.Video);
        Convert("prores.4444.noaudio", "ProRes", "4444 α · no audio", "ProRes 4444 · alpha · no audio", "Keeps transparency.", "film", 123, ".mov", FileKind.Video);
        Convert("prores.422", "ProRes", "422", "ProRes 422", "Standard ProRes.", "film", 124, ".mov", FileKind.Video);
        Convert("prores.lt", "ProRes", "422 LT", "ProRes 422 LT", "Lighter ProRes.", "film", 125, ".mov", FileKind.Video);
        Convert("prores.proxy", "ProRes", "422 Proxy", "ProRes 422 Proxy", "Offline editing.", "film", 126, ".mov", FileKind.Video);
        Convert("prores.xq", "ProRes", "4444 XQ α", "ProRes 4444 XQ · alpha", "Highest quality ProRes.", "film", 127, ".mov", FileKind.Video);
        Convert("audio.mp3", "Audio", "MP3", "Audio · MP3", "Extracts the soundtrack.", "audio", 200, ".mp3", FileKind.Video);
        Convert("audio.wav", "Audio", "WAV", "Audio · WAV", "Extracts the soundtrack, lossless.", "audio", 201, ".wav", FileKind.Video);
        Convert("mkv.remux", "MKV", null, "MKV · remux", "Copies every stream into Matroska.", "video", 210, ".mkv", FileKind.Video);
        Convert("avi", "AVI", null, "AVI · MPEG-4", "For older players.", "video", 400, ".avi", FileKind.Video);

        // Video tools
        Tool("compress", "Compress", "Good quality", "Compress · good quality", "Smaller file, same look.", "compress", 10, FileKind.Video, CompressSchema);
        Tool("trim", "Trim", null, "Trim…", "Cut the start and end.", "trim", 20, FileKind.Video, TrimSchema);
        Tool("crop", "Crop", null, "Crop…", "16:9, 9:16, 1:1 and more.", "crop", 30, FileKind.Video);
        Tool("resize.1080", "Resize", "1080p", "Resize · 1080p", "Fits inside 1920×1080.", "resize", 40, FileKind.Video);
        Tool("resize.720", "Resize", "720p", "Resize · 720p", "Fits inside 1280×720.", "resize", 41, FileKind.Video);
        Tool("resize.half", "Resize", "50%", "Resize · 50%", "Half the size.", "resize", 42, FileKind.Video);
        Tool("speed.half", "Speed", "0.5×", "Speed · 0.5×", "Half speed.", "speed", 50, FileKind.Video);
        Tool("speed.double", "Speed", "2×", "Speed · 2×", "Double speed.", "speed", 51, FileKind.Video);
        Tool("pingpong", "Loop", "Ping-pong", "Ping-pong loop", "Forward then backward: a seamless VJ loop.", "loop", 60, FileKind.Video);
        Tool("reverse", "Loop", "Reverse", "Reverse", "Plays backwards.", "reverse", 61, FileKind.Video);
        Tool("rotate.right", "Rotate", "90° right", "Rotate 90° right", "Clockwise.", "rotate", 70, FileKind.Video);
        Tool("snapshot", "Snapshot", "Middle frame", "Snapshot · middle frame", "Saves a PNG.", "snapshot", 80, FileKind.Video);
        Tool("mute", "Remove audio", null, "Remove audio", "Copies the video without sound.", "mute", 90, FileKind.Video);
        Tool("info", "Info", null, "Media info", "Codec, size, frame rate, colour, audio.", "info", 400, FileKind.Video);

        // PSD / images
        foreach (var (format, order, ext) in new[] { ("PNG", 10, ".png"), ("JPEG", 20, ".jpg"), ("WebP", 30, ".webp"), ("TIFF", 40, ".tif"), ("AVIF", 50, ".avif"), ("PDF", 60, ".pdf") })
            Convert($"image.{format.ToLowerInvariant()}", format, null, $"{format}", $"Converts to {format}.", "image", order, ext, FileKind.Image, heavy: false);
        Tool("image.layers", "Export layers", null, "Export layers", "A folder of PNGs, one per layer.", "layers", 100, FileKind.Image);

        // PDF
        Tool("pdf.split", "Split", "Ranges…", "Split PDF by ranges", "Each range becomes its own PDF.", "split", 10, FileKind.Pdf, PdfRangesSchema);
        Tool("pdf.protect", "Protect", null, "Protect with password…", "Adds a password.", "lock", 20, FileKind.Pdf, PasswordSchema);
        Tool("image.sequence", "Sequence", null, "Image sequence → video", "Combines images into a clip.", "frames", 30, FileKind.Image, EveryFieldSchema);
        return list;
    }

    static IOperation Op(string id, OperationCategory category, string group, string? variant, string title, string description,
        string icon, int order, string? extension, FileKind kind, OptionSchema? options, bool heavy, bool missingFfmpeg) =>
        new DelegateOperation(
            new OperationInfo
            {
                Id = Prefix + id.Replace('.', '-'),
                Category = category,
                Group = group,
                Variant = variant,
                Title = title,
                Description = description,
                IconKey = icon,
                OutputExtension = extension,
                Order = order,
                Weight = heavy ? OperationWeight.Heavy : OperationWeight.Light,
            },
            file => file.Kind == kind,
            (_, _, _) => Task.FromResult(new OperationResult()),
            options,
            availability: missingFfmpeg && kind is FileKind.Video or FileKind.Audio ? () => Availability.Unavailable(RequiresFfmpeg)
                : id == "image.avif" ? () => Availability.Unavailable("AVIF encoding isn't available in this build.")
                : null);

    /// <summary>Provider wrapper for <see cref="Operations"/>.</summary>
    public sealed class Provider(bool missingFfmpeg = false) : IOperationProvider
    {
        public string Name => "Snapshot samples";

        public IEnumerable<IOperation> CreateOperations(EngineContext context) => Operations(missingFfmpeg);
    }

    /// <summary>An installed copy's update source that offers <paramref name="version"/> and "downloads" it at once (never online).</summary>
    public sealed class UpdateSource(string version) : Updates.IUpdateBackend
    {
        public bool IsInstalled => true;

        public Task<Updates.UpdateOffer?> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Updates.UpdateOffer?>(new Updates.UpdateOffer(version));

        public Task DownloadAsync(Updates.UpdateOffer offer, Action<int> progress, CancellationToken cancellationToken)
        {
            progress(100);
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(Updates.UpdateOffer offer, IReadOnlyList<string> restartArgs)
        {
        }
    }

    /// <summary>The Tools page of a release build: FFmpeg and 7-Zip in the app's tools folder, LibreOffice not installed.</summary>
    public static IReadOnlyList<Preferences.ToolInfo> BundledTools()
    {
        var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClementineApp", "current");
        return
        [
            new(Preferences.ToolStatus.FfmpegKey, "FFmpeg", "Video and audio: every conversion and tool",
                Path.Combine(app, "tools", "ffmpeg", "bin", "ffmpeg.exe"), Preferences.Licences.BundledFfmpegVersion,
                Preferences.ToolStatus.FfmpegDownloadUrl, CanOverride: true, IsBundled: true),
            new(Preferences.ToolStatus.LibreOfficeKey, "LibreOffice", "PowerPoint → PDF and images, .doc/.xls/.ppt and OpenDocument files", null, null,
                "https://www.libreoffice.org/download/download-libreoffice/", CanOverride: true, IsOptional: true,
                MissingNote: Preferences.ToolStatus.LibreOfficeMissingNote),
            new(Preferences.ToolStatus.SevenZipKey, "7-Zip", "Creating .7z archives",
                Path.Combine(app, "tools", "7zip", "7za.exe"), Preferences.Licences.BundledSevenZipVersion,
                "https://www.7-zip.org/download.html", CanOverride: true, IsBundled: true),
            new(Preferences.ToolStatus.EdgeKey, "Microsoft Edge", "HTML, Markdown, e-books and Word/Excel → PDF (part of Windows)",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "142.0.3595.53", "https://www.microsoft.com/edge", CanOverride: false),
        ];
    }
}
