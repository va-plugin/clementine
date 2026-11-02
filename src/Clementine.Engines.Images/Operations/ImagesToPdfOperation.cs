using System.Globalization;
using Clementine.Engines.Images.Imaging;
using Clementine.Engines.Images.Pdf;

namespace Clementine.Engines.Images.Operations;

/// <summary>Images → PDF (combined): one image per page in natural file-name order; A4, Letter or page = image.</summary>
public sealed class ImagesToPdfOperation : ImageOperation
{
    readonly OptionSchema _schema;

    public ImagesToPdfOperation()
        : base(new OperationInfo
        {
            Id = "image.pdf.combine",
            Category = OperationCategory.Tool,
            Group = "Images → PDF",
            Title = "Images → PDF",
            Description = "One PDF with a page per image, in file-name order. JPEGs are embedded without recompression.",
            IconKey = "pdf",
            OutputExtension = ".pdf",
            Order = 100,
        })
    {
        var defaultPaper = RegionUsesLetter() ? "letter" : "a4";
        var onPaper = new OptionCondition("paper", "a4", "letter");
        _schema = new OptionSchema("Images to PDF",
        [
            new ChoiceField("paper", "Page size", [new("a4", "A4"), new("letter", "Letter"), new("fit", "Fit each image")], defaultPaper),
            new ChoiceField("orientation", "Orientation", [new("auto", "Automatic"), new("portrait", "Portrait"), new("landscape", "Landscape")], "auto")
            {
                VisibleWhen = onPaper,
            },
            new NumberField("margin", "Margin", 10, 0, 50) { Unit = "mm", VisibleWhen = onPaper },
        ])
        {
            ConfirmLabel = "Create PDF",
        };
    }

    public override InputMode InputMode => InputMode.Combined;

    public override int MinInputs => 1;

    public override OptionSchema? Options => _schema;

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var inputs = NaturalSortComparer.Sort(request.Inputs);
        var paper = request.Options.GetString("paper", "a4") switch
        {
            "letter" => PdfPaper.Letter,
            "fit" => PdfPaper.Fit,
            _ => PdfPaper.A4,
        };
        var orientation = request.Options.GetString("orientation", "auto") switch
        {
            "portrait" => PdfOrientation.Portrait,
            "landscape" => PdfOrientation.Landscape,
            _ => PdfOrientation.Auto,
        };
        var margin = request.Options.GetDouble("margin", 10);

        var warnings = new List<string>();
        using var output = request.Output.BeginFile(inputs[0], ".pdf");
        using (var stream = new FileStream(output.WorkingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var pdf = new PdfImageWriter(stream);
            for (var i = 0; i < inputs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Report(progress, (double)i / inputs.Count, $"Adding page {i + 1} of {inputs.Count}…");
                using var image = ImageLoader.Load(inputs[i], LoadOptions.Still, cancellationToken);
                pdf.AddPage(PdfPages.OnPaper(image, paper, orientation, margin));
                warnings.AddRange(image.Warnings.Select(w => inputs.Count > 1 ? $"{inputs[i].Name}: {w}" : w));
            }
            pdf.Finish(inputs[0].BaseName);
        }
        Report(progress, 1, "Done");
        return new OperationResult { Outputs = [output.Commit()], Warnings = warnings };
    }

    static bool RegionUsesLetter()
    {
        try
        {
            return new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE";
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
