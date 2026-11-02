using System.Text;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;

namespace Clementine.Engines.Media.Video;

/// <summary>
/// Renders a subtitle file (.srt/.ass/.ssa/.vtt) into the picture. Drop exactly one video and one subtitle file.
/// The subtitle is copied (as UTF-8) into the private temp folder under a plain name and FFmpeg runs there, which
/// sidesteps the filter-graph escaping of Windows paths, spaces and Unicode.
/// </summary>
public sealed class BurnSubtitlesOperation : MediaOperation
{
    public BurnSubtitlesOperation(OperationInfo info, MediaToolkit kit)
        : base(info, kit)
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override int MaxInputs => 2;

    public override bool Accepts(InputFile file) => MediaInputs.IsVideo(file) || MediaInputs.IsBurnableSubtitle(file);

    public override bool AcceptsAll(IReadOnlyList<InputFile> files) =>
        files.Count == 2 && files.Count(MediaInputs.IsVideo) == 1 && files.Count(MediaInputs.IsBurnableSubtitle) == 1;

    public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var videoFile = request.Inputs.FirstOrDefault(MediaInputs.IsVideo);
        var subtitleFile = request.Inputs.FirstOrDefault(MediaInputs.IsBurnableSubtitle);
        if (videoFile is null || subtitleFile is null)
            throw new OperationException("Drop one video together with one subtitle file (.srt, .ass or .vtt).");

        progress.Report(JobProgress.Indeterminate("Reading the video…"));
        var source = await Kit.Probe.ProbeVideoAsync(videoFile.Path, cancellationToken).ConfigureAwait(false);

        var localName = "subtitles" + subtitleFile.Extension;
        var text = DecodeText(await File.ReadAllBytesAsync(subtitleFile.Path, cancellationToken).ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Combine(request.TempDirectory, localName), text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var target = MatchingTargets.For(source, videoFile.Extension);
        var edit = new VideoEdit { VideoFilters = [$"subtitles={localName}"] };
        var single = new OperationRequest
        {
            Inputs = [videoFile],
            Options = request.Options,
            Output = request.Output,
            Context = request.Context,
            TempDirectory = request.TempDirectory,
        };
        return await EncodeToFileAsync(single, source, target, edit, "subtitled", progress, "Burning in subtitles…", cancellationToken,
            workingDirectory: request.TempDirectory).ConfigureAwait(false);
    }

    /// <summary>UTF-8 (with or without BOM), UTF-16 with BOM, otherwise Windows-1252 — what subtitle files are in practice.</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && (bytes[0] == 0xFF && bytes[1] == 0xFE || bytes[0] == 0xFE && bytes[1] == 0xFF))
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return Windows1252.Value.GetString(bytes);
        }
    }

    static readonly Lazy<Encoding> Windows1252 = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    });
}
