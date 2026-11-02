using System.Globalization;
using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Psd;

namespace Clementine.Engines.Images.Operations;

/// <summary>
/// Export layers (PSD/PSB/layered TIFF) → a folder of PNGs named "01 Layer name.png", bottom layer first.
/// "Full canvas" keeps every layer in place on a document-sized transparent canvas (ready to stack in Resolume);
/// "Trimmed" saves each layer at its own bounds. Multi-page TIFFs without layers export their pages.
/// </summary>
public sealed class ExportLayersOperation : ImageOperation
{
    readonly bool _trimmed;

    public ExportLayersOperation(bool trimmed)
        : base(new OperationInfo
        {
            Id = trimmed ? "image.layers.export.trimmed" : "image.layers.export",
            Category = OperationCategory.Tool,
            Group = "Export layers",
            Variant = trimmed ? "Trimmed" : "Full canvas",
            Title = trimmed ? "Export layers · trimmed" : "Export layers",
            Description = trimmed
                ? "Every layer as its own PNG, cropped to the layer's contents."
                : "Every layer as a PNG the size of the document, in place — ready to stack again.",
            IconKey = "layers",
            Order = 15 + (trimmed ? 1 : 0),
            Weight = OperationWeight.Heavy,
        }) => _trimmed = trimmed;

    public static IEnumerable<IOperation> All() => [new ExportLayersOperation(false), new ExportLayersOperation(true)];

    protected override IReadOnlySet<string> InputExtensions => ImageFormats.Layered;

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var input = request.Input;
        Report(progress, 0.05, "Reading layers…");
        ImageLoader.EnsureNotTooLarge(input, ImageLoader.ReadSettings(input, LoadOptions.Still, firstFrameOnly: true));
        using var frames = new MagickImageCollection();
        try
        {
            frames.Read(input.Path, ImageLoader.ReadSettings(input, LoadOptions.Still, firstFrameOnly: false));
        }
        catch (MagickException ex)
        {
            throw MagickErrors.Reading(ex, input);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var layerFrames = PsdLayers.LayerFrames(frames);
        if (layerFrames.Count == 0)
        {
            if (input.HasExtension(".tif", ".tiff") && frames.Count > 1)
                return ExportPages(request, frames, progress, cancellationToken);
            throw new OperationException($"“{input.Name}” has no layers to export.");
        }

        var document = input.HasExtension(ImageFormats.Psd) ? PsdDocument.TryRead(input.Path) : null;
        var plan = PsdLayers.Plan(document, layerFrames);
        var reference = frames[0];
        var width = document is { Width: > 0 } d ? (uint)d.Width : reference.Width;
        var height = document is { Height: > 0 } ? (uint)document.Height : reference.Height;
        var digits = plan.Count >= 100 ? 3 : 2;

        using var folder = request.Output.BeginDirectory(input, "layers");
        var exported = 0;
        var empty = 0;
        var hidden = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, 0.1 + 0.85 * i / plan.Count, $"Saving layer {i + 1} of {plan.Count}…");
            var layer = plan[i];
            using var rendered = PsdLayers.Render(layer with { ExtraOpacity = 1 }, width, height, reference, fullCanvas: !_trimmed);
            if (IsFullyTransparent(rendered))
            {
                empty++;
                continue;
            }
            if (!layer.Visible)
                hidden++;
            if (rendered.GetColorProfile() is null && reference.GetColorProfile() is { } icc)
                rendered.SetProfile(icc);
            ColorPipeline.ToDisplayColorSpace(rendered);
            exported++;
            var name = $"{exported.ToString($"D{digits}", CultureInfo.InvariantCulture)} {OutputPlanner.SanitizeName(layer.Name)}.png";
            WritePng(rendered, Path.Combine(folder.WorkingPath, name));
        }
        if (exported == 0)
            throw new OperationException($"All layers in “{input.Name}” are empty.");

        var warnings = new List<string>();
        if (hidden > 0)
            warnings.Add($"{hidden} hidden layer{(hidden == 1 ? " was" : "s were")} exported too.");
        if (empty > 0)
            warnings.Add($"Skipped {empty} empty layer{(empty == 1 ? "" : "s")} (groups or adjustment layers).");
        return new OperationResult { Outputs = [folder.Commit()], Warnings = warnings };
    }

    static OperationResult ExportPages(OperationRequest request, MagickImageCollection frames, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        using var folder = request.Output.BeginDirectory(request.Input, "pages");
        var digits = frames.Count >= 100 ? 3 : 2;
        for (var i = 0; i < frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, 0.1 + 0.85 * i / frames.Count, $"Saving page {i + 1} of {frames.Count}…");
            var page = frames[i];
            ColorPipeline.Orient(page);
            ColorPipeline.ToDisplayColorSpace(page);
            page.ResetPage();
            WritePng(page, Path.Combine(folder.WorkingPath, $"{(i + 1).ToString($"D{digits}", CultureInfo.InvariantCulture)} Page {i + 1}.png"));
        }
        return OperationResult.FromOutputs(folder.Commit());
    }

    static void WritePng(IMagickImage<QuantumType> image, string path)
    {
        if (image.Depth > 16)
            image.Depth = 16;
        ImageEncoder.ConfigurePng(image);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        image.Write(stream, MagickFormat.Png);
    }

    static bool IsFullyTransparent(IMagickImage<QuantumType> image)
    {
        if (!image.HasAlpha)
            return false;
        var alpha = image.Statistics().GetChannel(PixelChannel.Alpha);
        return alpha is not null && alpha.Maximum <= 0;
    }
}
