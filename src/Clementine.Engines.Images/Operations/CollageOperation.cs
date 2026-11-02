using Clementine.Engines.Images.Imaging;

namespace Clementine.Engines.Images.Operations;

/// <summary>Collage (combined): a tidy grid of the images with even spacing and a background colour.</summary>
public sealed class CollageOperation : ImageOperation
{
    const long MaxCanvasPixels = 120_000_000;

    static readonly OptionSchema Schema = new("Collage",
    [
        new NumberField("columns", "Columns", 0, 0, 20) { Help = "0 picks a balanced grid automatically." },
        new NumberField("cell", "Cell width", 800, 100, 4000) { Unit = "px", Step = 50 },
        new NumberField("spacing", "Spacing", 16, 0, 200) { Unit = "px" },
        new ChoiceField("layout", "Pictures", [new("fill", "Fill cells (crop)"), new("fit", "Fit inside cells")], "fill"),
        new ChoiceField("background", "Background", [new("white", "White"), new("black", "Black"), new("transparent", "Transparent")], "white"),
    ])
    {
        ConfirmLabel = "Make collage",
    };

    public CollageOperation()
        : base(new OperationInfo
        {
            Id = "image.collage",
            Category = OperationCategory.Tool,
            Group = "Collage",
            Title = "Collage",
            Description = "Arranges the images in a grid with even spacing (file-name order).",
            IconKey = "grid",
            Order = 110,
        })
    {
    }

    public override InputMode InputMode => InputMode.Combined;

    public override int MaxInputs => 400;

    public override OptionSchema? Options => Schema;

    protected override OperationResult Execute(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var inputs = NaturalSortComparer.Sort(request.Inputs);
        var options = request.Options;
        var spacing = (int)Math.Clamp(options.GetDouble("spacing", 16), 0, 200);
        var fill = options.GetString("layout", "fill") != "fit";
        var background = options.GetString("background", "white");
        var transparent = background == "transparent";

        Report(progress, 0.02, "Measuring…");
        var sizes = inputs.Select(i => ImageProbe.Size(i) ?? throw new OperationException($"Couldn't read “{i.Name}”.")).ToList();
        var count = inputs.Count;
        var requestedColumns = options.GetInt("columns", 0);
        var columns = requestedColumns > 0 ? Math.Min(requestedColumns, count) : count <= 3 ? count : (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);

        var aspects = sizes.Select(s => s.Width / (double)Math.Max(1, s.Height)).Order().ToList();
        var aspect = aspects[aspects.Count / 2];
        var cellWidth = Math.Clamp(options.GetDouble("cell", 800), 100, 4000);
        var cellHeight = cellWidth / aspect;
        var canvasWidth = columns * cellWidth + (columns + 1) * spacing;
        var canvasHeight = rows * cellHeight + (rows + 1) * spacing;
        if (canvasWidth * canvasHeight > MaxCanvasPixels)
        {
            var shrink = Math.Sqrt(MaxCanvasPixels / (canvasWidth * canvasHeight));
            cellWidth *= shrink;
            cellHeight *= shrink;
        }
        var cw = (uint)Math.Max(1, Math.Round(cellWidth));
        var ch = (uint)Math.Max(1, Math.Round(cellHeight));
        var width = (uint)(columns * cw + (columns + 1) * spacing);
        var height = (uint)(rows * ch + (rows + 1) * spacing);

        var color = background switch
        {
            "black" => MagickColors.Black,
            "transparent" => MagickColors.Transparent,
            _ => MagickColors.White,
        };
        using var canvas = new MagickImage(color, width, height);
        if (transparent)
            canvas.Alpha(AlphaOption.Set);
        var warnings = new List<string>();

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, 0.05 + 0.85 * i / count, $"Placing {i + 1} of {count}…");
            using var image = ImageLoader.Load(inputs[i], LoadOptions.Still, cancellationToken);
            var picture = image.First;
            ColorPipeline.ToSrgbProfile(picture);
            if (fill)
            {
                var cover = Cover(picture.Width, picture.Height, cw, ch);
                ResizeOperation.ResizeFrame(picture, cover.Width, cover.Height);
                picture.Crop(cw, ch, Gravity.Center);
                picture.ResetPage();
            }
            else
            {
                var scale = Math.Min(cw / (double)picture.Width, ch / (double)picture.Height);
                ResizeOperation.ResizeFrame(picture, (uint)Math.Max(1, Math.Round(picture.Width * scale)), (uint)Math.Max(1, Math.Round(picture.Height * scale)));
            }

            var row = i / columns;
            var column = i % columns;
            var inRow = row == rows - 1 ? count - row * columns : columns;
            var rowOffset = (columns - inRow) * (int)(cw + spacing) / 2; // centre an incomplete last row
            var x = spacing + column * (int)(cw + spacing) + rowOffset + ((int)cw - (int)picture.Width) / 2;
            var y = spacing + row * (int)(ch + spacing) + ((int)ch - (int)picture.Height) / 2;
            canvas.Composite(picture, x, y, CompositeOperator.Over);
            warnings.AddRange(image.Warnings.Select(w => $"{inputs[i].Name}: {w}"));
        }

        Report(progress, 0.92, "Saving…");
        var (format, extension) = transparent ? (MagickFormat.Png, ".png") : (MagickFormat.Jpeg, ".jpg");
        using var output = request.Output.BeginFile(inputs[0], extension, "collage");
        if (!transparent)
            canvas.Alpha(AlphaOption.Off);
        canvas.Quality = 92;
        canvas.Depth = 8;
        using (var stream = new FileStream(output.WorkingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            canvas.Write(stream, format);
        return new OperationResult { Outputs = [output.Commit()], Warnings = warnings };
    }

    /// <summary>Smallest size of the same aspect that covers a cell.</summary>
    static (uint Width, uint Height) Cover(uint width, uint height, uint cellWidth, uint cellHeight)
    {
        var scale = Math.Max(cellWidth / (double)width, cellHeight / (double)height);
        return ((uint)Math.Max(cellWidth, Math.Ceiling(width * scale)), (uint)Math.Max(cellHeight, Math.Ceiling(height * scale)));
    }
}
