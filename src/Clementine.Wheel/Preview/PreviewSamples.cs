using System.Text;
using Clementine.Core;

namespace Clementine.Wheel;

/// <summary>
/// Realistic sample operations (same groups, variants and wording the engines use) for rendering wheel previews
/// before the engines are populated. Ids start with "preview." and the samples never join the app's catalog;
/// running one fails with a friendly message.
/// </summary>
internal sealed class PreviewSampleProvider : IOperationProvider
{
    public string Name => "Wheel preview samples";

    public IEnumerable<IOperation> CreateOperations(EngineContext context) => PreviewSamples.Operations();
}

internal static class PreviewSamples
{
    const string NeedsLibreOffice = "Requires LibreOffice (free) — libreoffice.org";
    const string Needs7Zip = "Requires 7-Zip (free) — 7-zip.org";

    sealed record S(string? Variant, string Title, string Description, string? Missing = null);

    /// <summary>Which inputs a sample accepts; the name keeps ids unique across file kinds.</summary>
    sealed record D(string Name, Func<InputFile, bool> Accepts);

    public static IOperationProvider CreateProvider() => new PreviewSampleProvider();

    public static IEnumerable<IOperation> Operations()
    {
        var c = OperationCategory.Convert;
        var t = OperationCategory.Tool;
        var a = OperationCategory.Action;
        var video = new D("video", f => f.Kind == FileKind.Video);
        var audio = new D("audio", f => f.Kind == FileKind.Audio);
        var anything = new D("any", _ => true);
        var document = new D("document", f => f.Kind == FileKind.Document);
        var pdf = new D("pdf", f => f.Kind == FileKind.Pdf);
        D ImageNot(params string[] own) => new("image", f => f.Kind == FileKind.Image && !f.HasExtension(own));
        D AudioNot(params string[] own) => new("audio", f => f.Kind == FileKind.Audio && !f.HasExtension(own));

        IEnumerable<IOperation>[] groups =
        [
            // ---- video → convert
            Group(c, "MP4", "video", 10, video,
                new S("H.264", "MP4 · H.264", "Plays everywhere — phones, browsers and editors."),
                new S("H.264 · no audio", "MP4 · H.264 · no audio", "Same picture with the audio track removed."),
                new S("H.265", "MP4 · H.265 (HEVC)", "About half the size of H.264 at the same quality."),
                new S("H.265 · no audio", "MP4 · H.265 · no audio", "HEVC video without the audio track.")),
            Group(c, "MOV", "video", 20, video,
                new S("H.264", "MOV · H.264", "QuickTime movie with H.264 video and AAC audio."),
                new S("Remux", "MOV · remux (no re-encode)", "Moves the streams into a MOV container — instant and lossless.")),
            Group(c, "MKV", "video", 30, video,
                new S("Remux", "MKV · remux (no re-encode)", "Copies every stream into Matroska — instant and lossless."),
                new S("H.264", "MKV · H.264", "Re-encodes to H.264 inside Matroska.")),
            Group(c, "WebM", "video", 40, video,
                new S("VP9", "WebM · VP9", "Web-friendly VP9 with Opus audio."),
                new S("VP9 · alpha", "WebM · VP9 with alpha", "Keeps transparency for overlays on the web."),
                new S("AV1", "WebM · AV1", "Smallest files; slower to encode.")),
            Group(c, "GIF", "gif", 50, video,
                new S("High quality", "GIF · high quality", "Two-pass palette for clean colours. Loops forever."),
                new S("Small (480 px)", "GIF · small (480 px)", "Scaled to 480 px wide for chats and docs.")),
            Group(c, "WebP", "image", 55, video,
                new S(null, "Animated WebP", "Loops forever — much smaller than a GIF.")),
            Group(c, "Audio", "music", 60, video,
                new S("MP3", "Extract audio · MP3", "The first audio track as a 320 kbps MP3."),
                new S("WAV", "Extract audio · WAV", "The first audio track, uncompressed."),
                new S("M4A", "Extract audio · M4A", "The first audio track as AAC."),
                new S("FLAC", "Extract audio · FLAC", "The first audio track, lossless and compressed.")),
            Group(c, "DXV", "vj", 100, video,
                new S("With audio", "DXV3 · with audio", "Resolume DXV3, Normal Quality. No alpha — use HAP Alpha or ProRes 4444 for transparency."),
                new S("No audio", "DXV3 · no audio", "Resolume DXV3, Normal Quality, video only.")),
            Group(c, "HAP", "vj", 110, video,
                new S("HAP", "HAP · with audio", "GPU-decoded VJ codec — smooth playback at high resolutions."),
                new S("HAP · no audio", "HAP · no audio", "GPU-decoded VJ codec, video only."),
                new S("HAP Alpha", "HAP Alpha · with audio", "Keeps transparency; decodes on the GPU."),
                new S("HAP Alpha · no audio", "HAP Alpha · no audio", "Transparent HAP, video only."),
                new S("HAP Q", "HAP Q · with audio", "Higher quality HAP for gradients and detail."),
                new S("HAP Q · no audio", "HAP Q · no audio", "Higher quality HAP, video only.")),
            Group(c, "ProRes", "vj", 120, video,
                new S("422 HQ", "ProRes 422 HQ · with audio", "Edit-ready 10-bit master with 24-bit PCM audio."),
                new S("422 HQ · no audio", "ProRes 422 HQ · no audio", "Edit-ready 10-bit master, video only."),
                new S("4444 α · audio", "ProRes 4444 · alpha · with audio", "Keeps the alpha channel — ideal for layered VJ clips."),
                new S("4444 α · no audio", "ProRes 4444 · alpha · no audio", "Alpha channel, video only."),
                new S("422", "ProRes 422 · with audio", "Standard ProRes, a little smaller than HQ."),
                new S("422 LT", "ProRes 422 LT · with audio", "Lighter ProRes for long recordings."),
                new S("422 Proxy", "ProRes 422 Proxy · with audio", "Small offline-edit proxies."),
                new S("4444 XQ α", "ProRes 4444 XQ · alpha · with audio", "Highest quality ProRes with alpha.")),
            Group(c, "AVI", "video", 200, video, new S(null, "AVI · MPEG-4", "For older players and hardware.")),
            Group(c, "WMV", "video", 210, video, new S(null, "WMV", "For old Windows apps and PowerPoint.")),

            // ---- video → tools
            Group(t, "Compress", "compress", 10, video,
                new S("Good quality", "Compress · good quality", "Smaller file with the same codec, barely visible loss."),
                new S("Smaller (≤ 1080p)", "Compress · smaller (≤ 1080p)", "Scales down to 1080p and compresses harder."),
                new S("Target size…", "Compress to a target size…", "Two-pass encode that hits the size you choose.")),
            Group(t, "Trim", "trim", 20, video, new S(null, "Trim…", "Keep only the part between a start and end time.")),
            Group(t, "Crop", "crop", 30, video, new S(null, "Crop…", "Crop to 16:9, 9:16, 1:1, 4:5 or a custom size.")),
            Group(t, "Resize", "resize", 40, video,
                new S("1080p", "Resize to 1080p", "Scales so the height is 1080 px."),
                new S("720p", "Resize to 720p", "Scales so the height is 720 px."),
                new S("50%", "Resize to 50%", "Half the width and height."),
                new S("Custom…", "Resize…", "Any size; keeps the aspect ratio.")),
            Group(t, "Speed", "speed", 50, video,
                new S("0.5×", "Half speed", "Slow motion; audio is stretched to match."),
                new S("2×", "Double speed", "Twice as fast; audio pitch is preserved."),
                new S("Custom…", "Change speed…", "Anything from 0.1× to 10×.")),
            Group(t, "Frame rate", "film", 60, video, new S(null, "Change frame rate…", "Convert to 24, 25, 30, 50 or 60 fps.")),
            Group(t, "Rotate", "rotate", 70, video,
                new S("90° right", "Rotate 90° right", "Turns the picture clockwise."),
                new S("90° left", "Rotate 90° left", "Turns the picture anticlockwise."),
                new S("180°", "Rotate 180°", "Turns the picture upside down."),
                new S("Flip horizontal", "Flip horizontally", "Mirror image, left to right."),
                new S("Flip vertical", "Flip vertically", "Mirror image, top to bottom.")),
            Group(t, "Reverse", "reverse", 80, video, new S(null, "Reverse", "Plays the clip backwards.")),
            Group(t, "Ping-pong", "loop", 85, video, new S(null, "Ping-pong loop", "Forward then backward — a seamless VJ loop.")),
            Group(t, "Split", "split", 90, video, new S(null, "Split…", "Into equal parts or every N seconds, without re-encoding.")),
            Group(t, "Snapshot", "snapshot", 100, video,
                new S("First frame", "Snapshot · first frame", "Saves the first frame as a PNG."),
                new S("Middle frame", "Snapshot · middle frame", "Saves the frame halfway through as a PNG."),
                new S("At time…", "Snapshot at a time…", "Saves the frame at the time you choose."),
                new S("Contact sheet", "Contact sheet", "A 4×4 grid of frames from across the clip."),
                new S("All frames", "Export all frames", "Every frame as a PNG sequence in a folder.")),
            Group(t, "Remove audio", "mute", 110, video, new S(null, "Remove audio", "Copies the video without its audio track.")),
            Group(t, "Subtitles", "subtitle", 120, video, new S(null, "Extract subtitles", "Saves the first subtitle track as an .srt file.")),
            Group(t, "Metadata", "metadata", 130, video, new S(null, "Remove metadata", "Strips titles, dates, GPS and chapters.")),
            Group(t, "Info", "info", 140, video, new S(null, "Video info", "Codec, size, frame rate, colour and audio details.")),

            // ---- image → convert (PSD included)
            Group(c, "PNG", "image", 10, ImageNot(".png"),
                new S(null, "PNG", "Lossless with transparency — from a PSD, the merged composite of every layer.")),
            Group(c, "JPEG", "image", 20, ImageNot(".jpg", ".jpeg"),
                new S("High (92)", "JPEG · high quality", "Great for photos; transparency becomes white."),
                new S("Medium (80)", "JPEG · medium quality", "Smaller, still good for the web."),
                new S("Small (65)", "JPEG · small", "Smallest JPEG for quick sharing.")),
            Group(c, "WebP", "image", 30, ImageNot(".webp"),
                new S("Lossy", "WebP · lossy", "Much smaller than JPEG at the same quality."),
                new S("Lossless", "WebP · lossless", "Smaller than PNG, keeps transparency.")),
            Group(c, "AVIF", "image", 40, ImageNot(".avif"), new S(null, "AVIF", "Modern format with excellent compression.")),
            Group(c, "TIFF", "image", 50, ImageNot(".tif", ".tiff"), new S(null, "TIFF · LZW", "Lossless, for print and archiving.")),
            Group(c, "PDF", "pdf", 60, ImageNot(".pdf"), new S(null, "PDF", "The image on a single PDF page.")),
            Group(c, "GIF", "gif", 70, ImageNot(".gif"), new S(null, "GIF", "256 colours; animation is kept.")),
            Group(c, "BMP", "image", 200, ImageNot(".bmp"), new S(null, "BMP", "Uncompressed bitmap for old software.")),
            Group(c, "ICO", "image", 210, ImageNot(".ico"), new S(null, "ICO (16–256 px)", "A Windows icon with every standard size.")),

            // ---- audio → convert
            Group(c, "MP3", "music", 10, AudioNot(".mp3"),
                new S("320 kbps", "MP3 · 320 kbps", "Best MP3 quality."),
                new S("192 kbps", "MP3 · 192 kbps", "Good quality, smaller."),
                new S("128 kbps", "MP3 · 128 kbps", "Small files for speech and previews.")),
            Group(c, "WAV", "music", 20, AudioNot(".wav", ".wave"),
                new S("16-bit", "WAV · 16-bit", "CD quality, uncompressed."),
                new S("24-bit", "WAV · 24-bit", "Studio quality, uncompressed.")),
            Group(c, "FLAC", "music", 30, AudioNot(".flac"), new S(null, "FLAC", "Lossless and about half the size of WAV.")),

            // ---- audio → tools
            Group(t, "Trim", "trim", 10, audio, new S(null, "Trim…", "Keep only the part between a start and end time.")),
            Group(t, "Normalize", "normalize", 20, audio,
                new S("Streaming −14 LUFS", "Normalize · streaming (−14 LUFS)", "Matches Spotify, YouTube and Apple Music loudness."),
                new S("Podcast −16 LUFS", "Normalize · podcast (−16 LUFS)", "The usual loudness for spoken word."),
                new S("Broadcast −23 LUFS", "Normalize · broadcast (−23 LUFS)", "EBU R128 loudness for TV and radio."),
                new S("Peak 0 dB", "Normalize · peak 0 dB", "Raises the volume until the loudest peak hits 0 dB.")),
            Group(t, "Volume", "volume", 30, audio, new S(null, "Change volume…", "Louder or quieter by a number of decibels.")),
            Group(t, "Channels", "channels", 40, audio,
                new S("Mono", "Mix down to mono", "Both channels mixed into one."),
                new S("Stereo", "Make stereo", "Duplicates a mono channel to left and right."),
                new S("Swap L/R", "Swap left and right", "Mirrors the stereo image."),
                new S("Left only", "Keep the left channel", "Drops the right channel."),
                new S("Right only", "Keep the right channel", "Drops the left channel.")),
            Group(t, "Speed", "speed", 50, audio, new S(null, "Change speed…", "Faster or slower, keeping the pitch.")),
            Group(t, "Fade", "fade", 60, audio,
                new S("In & out 2 s", "Fade in and out (2 s)", "Two-second fades at both ends."),
                new S("Custom…", "Fade…", "Choose the fade-in and fade-out lengths.")),
            Group(t, "Bleep", "bleep", 70, audio, new S(null, "Bleep…", "Covers time ranges with a 1 kHz tone or silence.")),
            Group(t, "Compress", "compress", 80, audio,
                new S("Smaller", "Compress · smaller", "Smaller file with the same format."),
                new S("Target size…", "Compress to a target size…", "Picks the bitrate that hits your size.")),
            Group(t, "Split", "split", 90, audio,
                new S("N parts…", "Split into parts…", "Equal parts, without re-encoding."),
                new S("Every N seconds…", "Split every N seconds…", "Fixed-length chunks."),
                new S("On silence", "Split on silence", "Cuts wherever the audio goes quiet.")),
            Group(t, "Reverse", "reverse", 100, audio, new S(null, "Reverse", "Plays the audio backwards.")),
            Group(t, "Waveform", "waveform", 110, audio, new S(null, "Waveform video", "An MP4 of the waveform — handy for social posts.")),
            Group(t, "Metadata", "metadata", 120, audio, new S(null, "Remove metadata", "Strips tags and cover art.")),
            Group(t, "Info", "info", 130, audio, new S(null, "Audio info", "Codec, sample rate, channels, bitrate and tags.")),

            // ---- documents → convert (LibreOffice missing on purpose: shows disabled segments)
            Group(c, "PDF", "pdf", 10, document, new S(null, "PDF", "A PDF that looks exactly like the document.", NeedsLibreOffice)),
            Group(c, "TXT", "text", 20, document, new S(null, "Plain text", "Just the words, as UTF-8 text.")),
            Group(c, "HTML", "code", 30, document, new S(null, "HTML", "A web page version of the document.", NeedsLibreOffice)),
            Group(c, "ODT", "document", 40, document, new S(null, "OpenDocument text", "For LibreOffice and other open-source suites.", NeedsLibreOffice)),
            Group(c, "DOC", "document", 200, document, new S(null, "Word 97–2003", "For very old versions of Word.", NeedsLibreOffice)),

            // ---- PDF → convert
            Group(c, "PNG", "image", 10, pdf,
                new S("150 DPI", "PDF pages to PNG (150 DPI)", "One image per page, screen resolution."),
                new S("300 DPI", "PDF pages to PNG (300 DPI)", "One image per page, print resolution.")),
            Group(c, "TXT", "text", 30, pdf, new S(null, "PDF to text", "The selectable text of every page.")),

            // ---- actions on anything
            Group(a, "Copy path", "link", 10, anything, new S(null, "Copy path", "Puts the full path on the clipboard.")),
            Group(a, "Copy to", "copy", 20, anything, new S(null, "Copy to…", "Copies the files to a folder you choose.")),
            Group(a, "Move to", "move", 30, anything, new S(null, "Move to…", "Moves the files to a folder you choose.")),
            Group(a, "Show in folder", "folder", 40, anything, new S(null, "Show in Explorer", "Opens the folder with the files selected.")),
            Group(a, "Terminal", "terminal", 50, anything, new S(null, "Open terminal here", "Windows Terminal in the files' folder.")),
            Group(a, "Open with", "open", 60, anything, new S(null, "Open with…", "Pick the app to open the file with.")),
            Group(a, "Shelf", "pin", 70, anything, new S(null, "Pin to shelf", "Keeps the files on a floating shelf to drag out later.")),
            Group(a, "Rename", "rename", 80, anything, new S(null, "Rename…", "Rename with a pattern like {name} {n} {date}.")),
            Group(a, "Duplicate", "copy", 90, anything, new S(null, "Duplicate", "A copy next to the original.")),
            Group(a, "Archive", "archive", 100, anything,
                new S("ZIP", "Compress to ZIP", "One ZIP with everything in it."),
                new S("ZIP each", "Compress each to ZIP", "A separate ZIP per file or folder."),
                new S("TAR.GZ", "Compress to TAR.GZ", "A gzip-compressed tarball."),
                new S("7Z", "Compress to 7Z", "Best compression ratio.", Needs7Zip)),
            Group(a, "Checksum", "hash", 110, anything,
                new S("SHA-256", "Checksum · SHA-256", "The standard fingerprint for verifying downloads."),
                new S("SHA-1", "Checksum · SHA-1", "Older fingerprint, still common."),
                new S("MD5", "Checksum · MD5", "Fast fingerprint for spotting changed files."),
                new S("CRC32", "Checksum · CRC32", "Quick 8-character check value.")),
        ];
        return groups.SelectMany(g => g);
    }

    static IEnumerable<IOperation> Group(OperationCategory category, string group, string icon, int order, D inputs, params S[] variants)
    {
        for (var i = 0; i < variants.Length; i++)
        {
            var sample = variants[i];
            var variantSlug = variants.Length > 1 ? $".{i + 1}-{Slug(sample.Variant ?? sample.Title)}" : "";
            var info = new OperationInfo
            {
                Id = $"preview.{inputs.Name}.{Slug(category.ToString())}.{Slug(group)}{variantSlug}",
                Category = category,
                Group = group,
                Variant = variants.Length > 1 ? sample.Variant : null,
                Title = sample.Title,
                Description = sample.Description,
                IconKey = icon,
                Order = order + i,
            };
            Func<Availability>? availability = sample.Missing is { } reason ? () => Availability.Unavailable(reason) : null;
            yield return new DelegateOperation(info, inputs.Accepts, Run, availability: availability);
        }
    }

    static Task<OperationResult> Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        throw new OperationException("This is a wheel preview sample and can't run.");

    static string Slug(string text)
    {
        var builder = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "x" : slug;
    }
}
