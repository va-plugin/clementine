using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Metadata;

namespace Clementine.Engines.Images.Operations;

/// <summary>
/// Base for every image operation: extension-based acceptance, synchronous Magick work run inside the job's
/// worker, ImageMagick errors mapped to friendly messages, and output helpers that always go through
/// <see cref="OutputPlanner"/>.
/// </summary>
public abstract class ImageOperation : OperationBase
{
    protected ImageOperation(OperationInfo info)
        : base(info)
    {
    }

    /// <summary>Extensions this operation takes (default: every readable image).</summary>
    protected virtual IReadOnlySet<string> InputExtensions => ImageFormats.Readable;

    public override bool Accepts(InputFile file) => !file.IsDirectory && file.HasExtension(InputExtensions);

    public sealed override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            MagickRuntime.EnsureInitialized();
            return Task.FromResult(Execute(request, progress, cancellationToken));
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or MagickException or OperationException)
        {
            return Task.FromCanceled<OperationResult>(cancellationToken);
        }
        catch (MagickException ex)
        {
            var message = MagickErrors.IsOverLimit(ex) ? "This image is too large to process on this computer." : "Couldn't process this image.";
            return Task.FromException<OperationResult>(new OperationException(message, ex.Message, ex));
        }
        catch (Exception ex)
        {
            return Task.FromException<OperationResult>(ex);
        }
    }

    /// <summary>The work, on the job's worker thread. Throw <see cref="OperationException"/> for expected failures.</summary>
    protected abstract OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken);

    /// <summary>Encodes <paramref name="image"/> next to the input (never overwriting) and returns the final path.</summary>
    protected static string SaveNextToInput(
        OperationRequest request, LoadedImage image, MagickFormat format, string extension, string? suffix,
        EncodeOptions options, CancellationToken cancellationToken)
    {
        using var output = request.Output.BeginFile(request.Input, extension, suffix);
        ImageEncoder.Save(image, format, options, output.WorkingPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return output.Commit();
    }

    /// <summary>Writes already encoded bytes next to the input.</summary>
    protected static string SaveBytesNextToInput(OperationRequest request, byte[] bytes, string extension, string? suffix, CancellationToken cancellationToken)
    {
        using var output = request.Output.BeginFile(request.Input, extension, suffix);
        using (var stream = new FileStream(output.WorkingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        return output.Commit();
    }

    protected static OperationResult Result(string output, LoadedImage? image, params string[] extraWarnings) => new()
    {
        Outputs = [output],
        Warnings = [.. image?.Warnings ?? [], .. extraWarnings],
    };

    /// <summary>Quality for re-encoding a tool result in the input's own lossy format, so tools don't degrade it.</summary>
    protected static EncodeOptions PreservingQuality(LoadedImage image, ToolOutputFormat output)
    {
        if (output.Format == MagickFormat.Jpeg)
            return new EncodeOptions { Quality = (int)Math.Clamp(image.SourceQuality ?? ImageEncoder.DefaultJpegQuality, 60, 100) };
        if (output.Format == MagickFormat.WebP && WebPInfo.IsLossless(image.Source.Path))
            return new EncodeOptions { Lossless = true };
        return EncodeOptions.Default;
    }

    protected static void Report(IProgress<JobProgress> progress, double? fraction, string status) =>
        progress.Report(new JobProgress(fraction, status));
}
