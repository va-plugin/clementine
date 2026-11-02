using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

/// <summary>
/// A tool that changes the pixels (resize, crop, rotate, adjust…) and saves the result in the input's own format
/// at the input's quality — or as PNG/JPEG when that format can't be written (PSD, RAW, SVG, HEIC…).
/// Animated GIF/WebP stay animated: every frame is transformed.
/// </summary>
public abstract class ImageToolOperation : ImageOperation
{
    protected ImageToolOperation(OperationInfo info)
        : base(info)
    {
    }

    /// <summary>Whether animated GIF/WebP inputs keep all frames.</summary>
    protected virtual bool KeepsAnimation => true;

    /// <summary>Status line while working ("Resizing…").</summary>
    protected abstract string WorkingStatus { get; }

    /// <summary>Output name suffix for these options ("50%", "1x1").</summary>
    protected abstract string Suffix(LoadedImage image, OptionValues options);

    /// <summary>Applies the change to <paramref name="image"/> (all frames).</summary>
    protected abstract void Transform(LoadedImage image, OptionValues options, CancellationToken cancellationToken);

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        Report(progress, 0.05, "Reading…");
        using var image = ImageLoader.Load(request.Input, KeepsAnimation ? LoadOptions.Animated : LoadOptions.Still, cancellationToken);
        Report(progress, 0.35, WorkingStatus);
        var suffix = Suffix(image, request.Options);
        Transform(image, request.Options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Report(progress, 0.75, "Saving…");
        var output = ImageFormats.ToolOutputFor(request.Input, image.HasTransparency);
        if (output.IsFallback)
            image.AddWarning($"Saved as {output.Name} because Clementine can't write {request.Input.Extension} files.");
        var path = SaveNextToInput(request, image, output.Format, output.Extension, suffix, PreservingQuality(image, output), cancellationToken);
        Report(progress, 1, "Done");
        return Result(path, image);
    }
}
