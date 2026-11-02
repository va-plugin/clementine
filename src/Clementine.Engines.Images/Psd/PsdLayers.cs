namespace Clementine.Engines.Images.Psd;

/// <summary>A decoded layer with the properties that decide how it composites.</summary>
/// <param name="Frame">Layer pixels as decoded by ImageMagick (its own opacity and mask already applied), positioned by <c>Page</c>.</param>
/// <param name="Visible">Visible itself and inside visible groups.</param>
/// <param name="ExtraOpacity">Fill opacity × enclosing group opacity (0..1), not yet applied to the pixels.</param>
public sealed record PsdLayer(
    int Number,
    string Name,
    IMagickImage<QuantumType> Frame,
    bool Visible,
    double ExtraOpacity,
    CompositeOperator Operator,
    bool Clipped)
{
    public int X => Frame.Page.X;

    public int Y => Frame.Page.Y;
}

/// <summary>
/// Pairs ImageMagick's decoded layer frames with the layer records parsed by <see cref="PsdDocument"/> and
/// composites them the way Photoshop would (visibility incl. groups, opacity, clipping masks, blend modes best-effort).
/// </summary>
public static class PsdLayers
{
    /// <summary>The decoded layer frames of a PSD/PSB/layered TIFF read with every frame (ImageMagick tags them with psd:layer.* artifacts).</summary>
    public static IReadOnlyList<IMagickImage<QuantumType>> LayerFrames(IMagickImageCollection<QuantumType> frames) =>
        frames.Where(f => f.GetArtifact("psd:layer.visible") is not null).ToList();

    /// <summary>Builds the stacking plan (bottom-most first).</summary>
    public static IReadOnlyList<PsdLayer> Plan(PsdDocument? document, IReadOnlyList<IMagickImage<QuantumType>> frames)
    {
        var pairs = document is null ? null : Pair(document.Layers, frames);
        if (document is null || pairs is null)
            return frames.Select((f, i) => FromFrameOnly(i + 1, f)).ToList();

        var records = document.Layers;
        var effective = EffectiveVisibility(records);
        var plan = new List<PsdLayer>();
        foreach (var record in records)
        {
            if (!pairs.TryGetValue(record.Index, out var frame))
                continue;
            var (visible, opacity) = effective[record.Index];
            plan.Add(new PsdLayer(plan.Count + 1, Clean(record.Name, plan.Count + 1), frame, visible, opacity,
                BlendModeFor(record.BlendKey), record.Clipping));
        }
        // Frames ImageMagick decoded that we couldn't pair stay visible in their stored order (best effort).
        foreach (var frame in frames.Where(f => !pairs.ContainsValue(f)))
            plan.Add(FromFrameOnly(plan.Count + 1, frame));
        return plan;
    }

    /// <summary>Composites <paramref name="plan"/> onto a transparent canvas the size of the document.</summary>
    public static MagickImage Composite(IReadOnlyList<PsdLayer> plan, uint width, uint height, IMagickImage<QuantumType> reference, CancellationToken cancellationToken)
    {
        var canvas = NewCanvas(width, height, reference);
        MagickImage? pending = null; // current clipping base, document-sized
        var pendingOperator = CompositeOperator.Over;
        var baseHidden = false;

        void Flush()
        {
            if (pending is null)
                return;
            canvas.Composite(pending, pendingOperator);
            pending.Dispose();
            pending = null;
        }

        for (var i = 0; i < plan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layer = plan[i];
            if (layer.Clipped && (pending is not null || baseHidden))
            {
                if (!layer.Visible || pending is null)
                    continue;
                using var placed = Place(layer, canvas);
                if (layer.Operator == CompositeOperator.Over)
                {
                    pending.Composite(placed, CompositeOperator.Atop);
                }
                else
                {
                    var blended = (MagickImage)pending.Clone();
                    blended.Composite(placed, layer.Operator);
                    blended.Composite(pending, CompositeOperator.CopyAlpha);
                    pending.Dispose();
                    pending = blended;
                }
                continue;
            }

            Flush();
            baseHidden = !layer.Visible;
            if (!layer.Visible)
                continue;
            var nextIsClipped = i + 1 < plan.Count && plan[i + 1].Clipped;
            if (nextIsClipped)
            {
                pending = Place(layer, canvas);
                pendingOperator = layer.Operator;
            }
            else
            {
                using var prepared = WithOpacity(layer);
                canvas.Composite(prepared, layer.X, layer.Y, layer.Operator);
            }
        }
        Flush();

        foreach (var name in reference.ProfileNames)
        {
            if (reference.GetProfile(name) is { } profile)
                canvas.SetProfile(profile);
        }
        canvas.Density = reference.Density;
        return canvas;
    }

    /// <summary>A layer as a stand-alone image: either its own bounds, or placed on a document-sized canvas.</summary>
    public static MagickImage Render(PsdLayer layer, uint width, uint height, IMagickImage<QuantumType> reference, bool fullCanvas)
    {
        if (!fullCanvas)
        {
            var own = WithOpacity(layer);
            own.ResetPage();
            return own;
        }
        var canvas = NewCanvas(width, height, reference);
        using var prepared = WithOpacity(layer);
        canvas.Composite(prepared, layer.X, layer.Y, CompositeOperator.Over);
        return canvas;
    }

    /// <summary>Photoshop blend key → ImageMagick operator (closest match).</summary>
    public static CompositeOperator BlendModeFor(string key) => key switch
    {
        "norm" or "pass" or "diss" => CompositeOperator.Over,
        "dark" => CompositeOperator.Darken,
        "mul " => CompositeOperator.Multiply,
        "idiv" => CompositeOperator.ColorBurn,
        "lbrn" => CompositeOperator.LinearBurn,
        "dkCl" => CompositeOperator.DarkenIntensity,
        "lite" => CompositeOperator.Lighten,
        "scrn" => CompositeOperator.Screen,
        "div " => CompositeOperator.ColorDodge,
        "lddg" => CompositeOperator.LinearDodge,
        "lgCl" => CompositeOperator.LightenIntensity,
        "over" => CompositeOperator.Overlay,
        "sLit" => CompositeOperator.SoftLight,
        "hLit" => CompositeOperator.HardLight,
        "vLit" => CompositeOperator.VividLight,
        "lLit" => CompositeOperator.LinearLight,
        "pLit" => CompositeOperator.PinLight,
        "hMix" => CompositeOperator.HardMix,
        "diff" => CompositeOperator.Difference,
        "smud" => CompositeOperator.Exclusion,
        "fsub" => CompositeOperator.MinusSrc,
        "fdiv" => CompositeOperator.DivideSrc,
        "hue " => CompositeOperator.Hue,
        "sat " => CompositeOperator.Saturate,
        "colr" => CompositeOperator.Colorize,
        "lum " => CompositeOperator.Luminize,
        _ => CompositeOperator.Over,
    };

    static PsdLayer FromFrameOnly(int number, IMagickImage<QuantumType> frame)
    {
        var visible = frame.GetArtifact("psd:layer.visible") != "0" && frame.Compose != CompositeOperator.No;
        var op = frame.Compose is CompositeOperator.No or CompositeOperator.Undefined ? CompositeOperator.Over : frame.Compose;
        return new PsdLayer(number, Clean(frame.Label, number), frame, visible, 1, op, Clipped: false);
    }

    /// <summary>Matches records (in stored order) to decoded frames by position and size; null if they don't line up.</summary>
    static Dictionary<int, IMagickImage<QuantumType>>? Pair(IReadOnlyList<PsdLayerRecord> records, IReadOnlyList<IMagickImage<QuantumType>> frames)
    {
        var map = new Dictionary<int, IMagickImage<QuantumType>>();
        var next = 0;
        foreach (var record in records)
        {
            for (var look = next; look < Math.Min(frames.Count, next + 4); look++)
            {
                if (Matches(record, frames[look]))
                {
                    map[record.Index] = frames[look];
                    next = look + 1;
                    break;
                }
            }
        }
        return frames.Count > 0 && map.Count >= Math.Max(1, frames.Count * 3 / 4) ? map : null;
    }

    static bool Matches(PsdLayerRecord record, IMagickImage<QuantumType> frame) =>
        record.HasArea
        && frame.Width == (uint)record.Width && frame.Height == (uint)record.Height
        && frame.Page.X == record.Left && frame.Page.Y == record.Top;

    /// <summary>Visibility and extra opacity per record, honouring enclosing groups (records are stored bottom-up).</summary>
    static Dictionary<int, (bool Visible, double Opacity)> EffectiveVisibility(IReadOnlyList<PsdLayerRecord> records)
    {
        var result = new Dictionary<int, (bool, double)>();
        var groups = new Stack<(bool Visible, double Opacity)>();
        groups.Push((true, 1.0));
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];
            var parent = groups.Peek();
            if (record.IsGroupStart)
            {
                groups.Push((parent.Visible && !record.Hidden, parent.Opacity * record.Opacity / 255.0));
                result[record.Index] = (false, 0);
            }
            else if (record.IsGroupEnd)
            {
                if (groups.Count > 1)
                    groups.Pop();
                result[record.Index] = (false, 0);
            }
            else
            {
                result[record.Index] = (parent.Visible && !record.Hidden, parent.Opacity * record.FillOpacity / 255.0);
            }
        }
        return result;
    }

    static MagickImage NewCanvas(uint width, uint height, IMagickImage<QuantumType> reference)
    {
        var canvas = new MagickImage(MagickColors.Transparent, width, height);
        if (reference.ColorSpace is ColorSpace.CMYK or ColorSpace.Gray or ColorSpace.Lab)
            canvas.ColorSpace = reference.ColorSpace;
        canvas.Alpha(AlphaOption.Set);
        canvas.Depth = reference.Depth;
        return canvas;
    }

    static MagickImage Place(PsdLayer layer, IMagickImage<QuantumType> canvasLike)
    {
        var placed = new MagickImage(MagickColors.Transparent, canvasLike.Width, canvasLike.Height);
        if (canvasLike.ColorSpace != placed.ColorSpace)
            placed.ColorSpace = canvasLike.ColorSpace;
        placed.Alpha(AlphaOption.Set);
        using var prepared = WithOpacity(layer);
        placed.Composite(prepared, layer.X, layer.Y, CompositeOperator.Over);
        return placed;
    }

    static MagickImage WithOpacity(PsdLayer layer)
    {
        var copy = (MagickImage)layer.Frame.Clone();
        if (layer.ExtraOpacity < 0.999)
        {
            if (!copy.HasAlpha)
                copy.Alpha(AlphaOption.Set);
            copy.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, Math.Clamp(layer.ExtraOpacity, 0, 1));
        }
        return copy;
    }

    static string Clean(string? name, int number) => string.IsNullOrWhiteSpace(name) ? $"Layer {number}" : name.Trim();
}
