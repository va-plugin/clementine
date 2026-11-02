using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>How the wheel is being used; changes hints and whether the invisible catch area is drawn.</summary>
public enum WheelInteraction
{
    /// <summary>Opened by a Shift-drag: hover follows the OLE drag, a drop chooses.</summary>
    Drag,

    /// <summary>Opened for files that aren't being dragged: mouse and keyboard choose.</summary>
    Click,
}

/// <summary>One layer of the wheel; draws whatever its owner tells it to.</summary>
internal sealed class WheelLayer : FrameworkElement
{
    readonly Action<DrawingContext> _draw;
    readonly Action<Exception> _failed;

    public WheelLayer(Action<DrawingContext> draw, Action<Exception> failed)
    {
        _draw = draw;
        _failed = failed;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        try
        {
            _draw(drawingContext);
        }
        catch (Exception ex)
        {
            // A drawing bug must never take down the drag that is in progress.
            _failed(ex);
        }
    }
}

/// <summary>
/// Draws a <see cref="WheelSession"/>: the glass disc, the inner ring of groups, the fanned-out variants of the
/// open group and the centre description. Four layers (base, fan, ring, centre) so the fan can animate on the render
/// thread without redrawing anything else. Sizes are in DIPs; the wheel centre is at (<see cref="Extent"/>, <see cref="Extent"/>).
/// </summary>
internal sealed class WheelView : FrameworkElement
{
    static readonly double[] RingFontSizes = [13, 12, 11, 10.5];
    static readonly double[] FanFontSizes = [11.5, 11, 10.5, 10];
    const double LabelMargin = 4;

    /// <summary>Room kept free at the outer edge of a segment with variants for its three "more inside" dots.</summary>
    const double VariantDotsReserve = 6.5;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Text, double Size, double PixelsPerDip), double> FitWidths = new();
    static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(140));
    static readonly Duration FanDuration = new(TimeSpan.FromMilliseconds(120));

    readonly WheelPalette _palette;
    readonly WheelLayer _base;
    readonly WheelLayer _fan;
    readonly WheelLayer _ring;
    readonly WheelLayer _centre;
    readonly VisualCollection _children;
    readonly ScaleTransform _openScale = new(1, 1);
    readonly ScaleTransform _fanScale = new(1, 1);
    readonly Dictionary<(string Text, double Size, int Weight), double> _widths = [];
    readonly Pen _discPen;
    readonly Pen _centrePen;
    readonly Pen _variantPen;
    readonly Pen _pendingPen;

    WheelSession? _session;
    WheelInteraction _interaction;
    double? _pixelsPerDip;

    WheelLayout? _fittedLayout;
    double _fittedPixelsPerDip;
    IReadOnlyList<LabelFit> _ringFits = [];
    (string Text, FontFamily Family)?[] _ringGlyphs = [];
    Geometry[] _ringGeometries = [];
    int _fittedFan = -1;
    IReadOnlyList<LabelFit> _fanFits = [];
    Geometry[] _fanGeometries = [];
    int _shownFan = -1;

    public WheelView(WheelPalette? palette = null)
    {
        _palette = palette ?? WheelPalette.Current;
        _base = new WheelLayer(DrawBase, OnRenderFailed);
        _fan = new WheelLayer(DrawFan, OnRenderFailed);
        _ring = new WheelLayer(DrawRing, OnRenderFailed);
        _centre = new WheelLayer(DrawCentre, OnRenderFailed);
        _children = new VisualCollection(this) { _base, _fan, _ring, _centre };

        _fan.RenderTransform = _fanScale;
        var shadow = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 18,
            ShadowDepth = 3,
            Direction = 270,
            Opacity = 0.5,
            RenderingBias = RenderingBias.Performance,
        };
        shadow.Freeze();
        _fan.Effect = shadow;
        RenderTransform = _openScale;

        _discPen = FrozenPen(_palette.DiscEdge, 1);
        _centrePen = FrozenPen(_palette.CentreEdge, 1);
        _variantPen = FrozenPen(_palette.VariantEdge, 1);
        _pendingPen = FrozenPen(_palette.AccentLight, 1.5);

        UseLayoutRounding = true;
        Focusable = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
    }

    /// <summary>Called when drawing throws (the exception is swallowed so a drag in progress survives).</summary>
    public Action<Exception>? RenderFailed { get; set; }

    public WheelSession? Session => _session;

    public WheelInteraction Interaction => _interaction;

    public WheelMetrics Metrics => _session?.Layout.Metrics ?? WheelMetrics.Default;

    public double Extent => Metrics.Extent;

    /// <summary>The wheel centre in this element's coordinates.</summary>
    public Point Centre => new(Extent, Extent);

    /// <summary>Overrides the device scale used for text hinting (previews render at 2×).</summary>
    public double? PixelsPerDipOverride
    {
        get => _pixelsPerDip;
        set
        {
            _pixelsPerDip = value;
            Invalidate(layout: true);
        }
    }

    double PixelsPerDip => _pixelsPerDip ?? VisualTreeHelper.GetDpi(this).PixelsPerDip;

    protected override int VisualChildrenCount => _children.Count;

    protected override Visual GetVisualChild(int index) => _children[index];

    /// <summary>Shows <paramref name="session"/> from scratch (new files, mode or page).</summary>
    public void Show(WheelSession session, WheelInteraction interaction)
    {
        _session = session;
        _interaction = interaction;
        _shownFan = session.ExpandedSegment;
        StopFanAnimation();
        Invalidate(layout: true);
    }

    /// <summary>Redraws after the session's highlight or page changed; animates a newly opened fan.</summary>
    public void Refresh(bool animate = true)
    {
        if (_session is null)
            return;
        if (!ReferenceEquals(_fittedLayout, _session.Layout))
        {
            _shownFan = _session.ExpandedSegment;
            Invalidate(layout: true);
            return;
        }
        var fan = _session.ExpandedSegment;
        if (fan != _shownFan)
        {
            _shownFan = fan;
            if (fan >= 0 && animate)
                AnimateFan();
        }
        _fan.InvalidateVisual();
        _ring.InvalidateVisual();
        _centre.InvalidateVisual();
    }

    /// <summary>Open animation: scale 0.85 → 1 and fade in, ease-out.</summary>
    public void PlayOpenAnimation()
    {
        if (!AnimationsEnabled)
        {
            StopOpenAnimation();
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _openScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, OpenDuration) { EasingFunction = ease });
        _openScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, OpenDuration) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, OpenDuration) { EasingFunction = ease });
    }

    /// <summary>Jumps every animation to its end state.</summary>
    public void StopOpenAnimation()
    {
        _openScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _openScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BeginAnimation(OpacityProperty, null);
        _openScale.ScaleX = _openScale.ScaleY = 1;
        Opacity = 1;
    }

    /// <summary>
    /// Renders a representative wheel once, off screen, so fonts, glyph coverage and every drawing path are loaded
    /// and compiled before the user's first Shift-drag.
    /// </summary>
    public static void WarmUp()
    {
        var info = new OperationInfo
        {
            Id = "wheel.warm-up",
            Category = OperationCategory.Convert,
            Group = "ProRes",
            Variant = "422 HQ",
            Title = "Warm-up",
            IconKey = "vj",
        };
        var operation = new DelegateOperation(info, _ => true, (_, _, _) => Task.FromResult(new OperationResult()));
        var options = new[] { new WheelOption(operation, Availability.Available), new WheelOption(operation, Availability.Available) };
        var model = WheelModel.FromGroups([], WheelMode.Convert, [new WheelGroup("ProRes", "vj", options), new WheelGroup("MP4", "video", options[..1])]);
        var session = new WheelSession(model);
        session.Highlight(WheelHit.OnVariant(0, 1));
        var view = new WheelView();
        view.Show(session, WheelInteraction.Drag);
        var size = view.Metrics.Size;
        view.Measure(new Size(size, size));
        view.Arrange(new Rect(0, 0, size, size));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
    }

    /// <summary>
    /// Shows <paramref name="session"/> without drawing it and measures every label of its first page (ring and all
    /// fans), so this view's text cache already holds them when the wheel really opens.
    /// </summary>
    public void Prime(WheelSession session)
    {
        Show(session, WheelInteraction.Drag);
        TruncatedLabels();
    }

    /// <summary>Labels that had to be shortened with "…" anywhere on the current page (should be none).</summary>
    internal IReadOnlyList<string> TruncatedLabels()
    {
        if (_session is null)
            return [];
        EnsureRingFits();
        var truncated = _ringFits.Where(f => f.Truncated).Select(f => string.Join(" ", f.Lines)).ToList();
        for (var s = 0; s < _session.Segments.Count; s++)
        {
            if (!_session.Segments[s].HasVariants)
                continue;
            EnsureFanFits(s);
            truncated.AddRange(_fanFits.Where(f => f.Truncated).Select(f => string.Join(" ", f.Lines)));
        }
        EnsureFanFits(_session.ExpandedSegment);
        return truncated;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(Metrics.Size, Metrics.Size);
        foreach (UIElement child in _children)
            child.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = Metrics.Size;
        foreach (UIElement child in _children)
            child.Arrange(new Rect(0, 0, size, size));
        _openScale.CenterX = _openScale.CenterY = size / 2;
        _fanScale.CenterX = _fanScale.CenterY = size / 2;
        return new Size(size, size);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _widths.Clear();
        Invalidate(layout: true);
    }

    static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    void Invalidate(bool layout)
    {
        if (layout)
        {
            _fittedLayout = null;
            _fittedFan = -1;
            InvalidateMeasure();
            InvalidateArrange();
            _base.InvalidateVisual();
        }
        _fan.InvalidateVisual();
        _ring.InvalidateVisual();
        _centre.InvalidateVisual();
    }

    void AnimateFan()
    {
        if (!AnimationsEnabled)
        {
            StopFanAnimation();
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _fanScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, FanDuration) { EasingFunction = ease });
        _fanScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, FanDuration) { EasingFunction = ease });
        _fan.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FanDuration) { EasingFunction = ease });
    }

    void StopFanAnimation()
    {
        _fanScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _fanScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _fan.BeginAnimation(OpacityProperty, null);
        _fanScale.ScaleX = _fanScale.ScaleY = 1;
        _fan.Opacity = 1;
    }

    void OnRenderFailed(Exception ex) => RenderFailed?.Invoke(ex);

    // ---------------------------------------------------------------- layout caches

    void EnsureRingFits()
    {
        var session = _session!;
        var ppd = PixelsPerDip;
        if (ReferenceEquals(_fittedLayout, session.Layout) && _fittedPixelsPerDip == ppd)
            return;

        var layout = session.Layout;
        var m = layout.Metrics;
        var kindKey = IconGlyphs.KeyForKind(session.Model.Summary.Kind);
        _ringGlyphs = session.Segments
            .Select(s => s.IsMore || !string.Equals(s.IconKey, kindKey, StringComparison.Ordinal) ? IconGlyphs.Resolve(s.IconKey, _palette.IconFont) : null)
            .ToArray();

        var glyphs = _ringGlyphs;
        var items = RingItems(layout, session.Segments, i => glyphs[i] is not null);
        _ringFits = LabelFitter.FitAll(items, RingFontSizes, _palette.GlyphSize, (text, size) => Measure(text, size, _palette.Semibold), LabelMargin);
        _ringGeometries = layout.Segments
            .Select(r => SegmentGeometry.Create(Centre, r, m.InnerRingInnerRadius, m.InnerRingOuterRadius, m.SegmentGap, m.CornerRadius))
            .ToArray();
        _fittedLayout = layout;
        _fittedPixelsPerDip = ppd;
        _fittedFan = -1;
    }

    void EnsureFanFits(int segment)
    {
        var session = _session!;
        if (segment < 0 || segment >= session.Segments.Count || _fittedFan == segment)
            return;
        var layout = session.Layout;
        var m = layout.Metrics;
        var fan = layout.VariantsOf(segment);
        var options = session.Segments[segment].Options;
        var items = fan
            .Select((range, v) => (options[v].Label, false, new SectorFrame(range, m.OuterRingInnerRadius, m.OuterRingOuterRadius, m.SegmentGap)))
            .ToList();
        _fanFits = LabelFitter.FitAll(items, FanFontSizes, 0, (text, size) => Measure(text, size, _palette.Semibold), LabelMargin);
        _fanGeometries = fan
            .Select(r => SegmentGeometry.Create(Centre, r, m.OuterRingInnerRadius, m.OuterRingOuterRadius, m.SegmentGap, m.CornerRadius - 1))
            .ToArray();
        _fittedFan = segment;
    }

    static string LabelOf(WheelSegment segment) =>
        segment.IsMore ? $"{segment.HiddenCount.ToString(CultureInfo.CurrentCulture)} more" : segment.Label;

    /// <summary>What each inner-ring label must fit into: its segment, minus the strip the variant dots use.</summary>
    static List<(string Label, bool HasGlyph, SectorFrame Frame)> RingItems(WheelLayout layout, IReadOnlyList<WheelSegment> segments, Func<int, bool> hasGlyph)
    {
        var m = layout.Metrics;
        return segments
            .Select((s, i) => (LabelOf(s), hasGlyph(i), new SectorFrame(
                layout.Segments[i], m.InnerRingInnerRadius, m.InnerRingOuterRadius - (s.HasVariants ? VariantDotsReserve : 0), m.SegmentGap)))
            .ToList();
    }

    /// <summary>
    /// Whether every inner-ring label of <paramref name="segments"/> fits <paramref name="layout"/> without an
    /// ellipsis when drawn at <paramref name="pixelsPerDip"/>. <see cref="WheelSession.ForDisplay"/> grows a page until
    /// this holds.
    /// </summary>
    internal static bool RingLabelsFit(WheelLayout layout, IReadOnlyList<WheelSegment> segments, double pixelsPerDip = 1)
    {
        var typeface = WheelPalette.Current.Semibold;
        var ppd = pixelsPerDip > 0 && double.IsFinite(pixelsPerDip) ? pixelsPerDip : 1;
        double MeasureAt(string text, double size) => FitWidths.GetOrAdd((text, size, ppd), key =>
            new FormattedText(key.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, key.Size, Brushes.Black, null, TextFormattingMode.Display, key.PixelsPerDip).Width);
        if (FitWidths.Count > 4000)
            FitWidths.Clear();
        var fits = LabelFitter.FitAll(RingItems(layout, segments, _ => false), RingFontSizes, 0, MeasureAt, LabelMargin);
        return fits.All(f => !f.Truncated);
    }

    // ---------------------------------------------------------------- drawing

    void DrawBase(DrawingContext dc)
    {
        if (_session is null)
            return;
        var m = Metrics;
        var c = Centre;

        // Near-invisible catch area: lets the whole wheel (and a little around it) receive drags and clicks.
        dc.DrawEllipse(_palette.CatchArea, null, c, m.OuterRingOuterRadius + m.PointerSlack, m.OuterRingOuterRadius + m.PointerSlack);

        // With nothing to offer only the centre disc shows, so the message isn't lost in an empty ring.
        var discRadius = _session.Model.IsEmpty ? m.CentreRadius : m.DiscRadius;
        var shadowRadius = discRadius + 24;
        // Only around the disc: seen through the 93 % glass its falloff would quantise into faint rings that no
        // dithering can reach (the glass divides the dither by 14). 2 DIPs of overlap keep the edges seamless.
        var shadow = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(c + new Vector(0, 4), shadowRadius, shadowRadius), new EllipseGeometry(c, discRadius - 2, discRadius - 2));
        shadow.Freeze();
        Fill(dc, ShadowBrush(discRadius, shadowRadius), shadow);
        if (_session.Model.IsEmpty)
            return;
        dc.DrawEllipse(_palette.Disc, _discPen, c, discRadius - 0.5, discRadius - 0.5);
        var sheen = new EllipseGeometry(c, discRadius - 1, discRadius - 1);
        sheen.Freeze();
        Fill(dc, _palette.DiscSheen, sheen);
    }

    void DrawRing(DrawingContext dc)
    {
        var session = _session;
        if (session is null || session.Segments.Count == 0)
            return;
        EnsureRingFits();
        var m = Metrics;

        for (var i = 0; i < session.Segments.Count; i++)
        {
            var segment = session.Segments[i];
            var hovered = session.Hover.Kind == WheelHitKind.Segment && session.Hover.Segment == i;
            var expanded = session.ExpandedSegment == i && !hovered;
            var available = segment.IsAvailable;
            var lit = hovered && available;
            // While the open fan is held, the group actually under the pointer (what a drop right now would run) is outlined.
            var underPointer = !hovered && session.PendingHit is { Kind: WheelHitKind.Segment } pending && pending.Segment == i;

            var fill = hovered
                ? available ? _palette.AccentGradient : _palette.SegmentHoverDisabled
                : expanded ? _palette.SegmentExpanded
                : available ? _palette.Segment : _palette.SegmentDisabled;
            Fill(dc, fill, _ringGeometries[i]);
            if (underPointer)
                dc.DrawGeometry(null, _pendingPen, _ringGeometries[i]);

            if (segment.HasVariants)
            {
                // Three small dots along the outer edge: "more inside".
                var hintBrush = lit ? _palette.TextOnAccent : expanded ? _palette.Accent : _palette.VariantHint;
                var radius = m.InnerRingOuterRadius - 6.5;
                var step = Angles.ToDegrees(5 / radius);
                dc.PushOpacity(lit ? 0.6 : 1);
                for (var d = -1; d <= 1; d++)
                    dc.DrawEllipse(hintBrush, null, Angles.PointAt(Centre, session.Layout.Segments[i].Mid + d * step, radius), 1.35, 1.35);
                dc.Pop();
            }

            var text = lit ? _palette.TextOnAccent : !available ? _palette.TextDisabled : expanded ? _palette.AccentLight : _palette.Text;
            var glyph = lit ? _palette.TextOnAccent : !available ? _palette.TextDisabled : expanded ? _palette.AccentLight : _palette.TextSecondary;
            DrawLabel(dc, _ringFits[i], _ringGlyphs[i], text, glyph);
        }
    }

    void DrawFan(DrawingContext dc)
    {
        var session = _session;
        if (session is null)
            return;
        var open = session.ExpandedSegment;
        if (open < 0 || !session.Segments[open].HasVariants)
            return;
        EnsureFanFits(open);
        var m = Metrics;
        var options = session.Segments[open].Options;

        for (var v = 0; v < _fanGeometries.Length; v++)
        {
            var option = options[v];
            var hovered = session.Hover.Kind == WheelHitKind.Variant && session.Hover.Variant == v;
            var lit = hovered && option.IsAvailable;

            if (lit)
                Fill(dc, _palette.AccentGradient, _fanGeometries[v]);
            else
                dc.DrawGeometry(_palette.Variant, _variantPen, _fanGeometries[v]);
            if (hovered && !option.IsAvailable)
                dc.DrawGeometry(_palette.SegmentHoverDisabled, null, _fanGeometries[v]);

            var brush = lit ? _palette.TextOnAccent : option.IsAvailable ? _palette.Text : _palette.TextDisabled;
            DrawLabel(dc, _fanFits[v], null, brush, brush);
        }
    }

    void DrawLabel(DrawingContext dc, LabelFit fit, (string Text, FontFamily Family)? glyph, Brush textBrush, Brush glyphBrush)
    {
        var anchor = Centre + fit.Centre;
        var glyphSize = _palette.GlyphSize;
        var showGlyph = fit.ShowGlyph && glyph is not null;
        var height = fit.Lines.Count * fit.LineHeight + (showGlyph ? glyphSize + LabelFitter.GlyphGap : 0);
        var y = anchor.Y - height / 2;

        if (showGlyph)
        {
            var (glyphText, family) = glyph!.Value;
            var icon = Format(glyphText, glyphSize, new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), glyphBrush);
            dc.DrawText(icon, Snap(new Point(anchor.X - icon.Width / 2, y + (glyphSize - icon.Height) / 2)));
            y += glyphSize + LabelFitter.GlyphGap;
        }
        foreach (var line in fit.Lines)
        {
            var text = Format(line, fit.FontSize, _palette.Semibold, textBrush);
            dc.DrawText(text, Snap(new Point(anchor.X - text.Width / 2, y + (fit.LineHeight - text.Height) / 2)));
            y += fit.LineHeight;
        }
    }

    void DrawCentre(DrawingContext dc)
    {
        var session = _session;
        if (session is null)
            return;
        var m = Metrics;
        var disc = new EllipseGeometry(Centre, m.CentreRadius - 0.5, m.CentreRadius - 0.5);
        disc.Freeze();
        Fill(dc, _palette.Centre, disc);
        dc.DrawGeometry(null, _centrePen, disc);
        DrawCentreBlock(dc, ChooseCentre(session, m.CentreRadius - 11));
    }

    /// <summary>Fills a shape; gradients are dithered (see <see cref="DitheredGradient"/>) so dark ramps don't band.</summary>
    void Fill(DrawingContext dc, Brush brush, Geometry geometry)
    {
        if (DitheredGradient.Supports(brush))
            DitheredGradient.Fill(dc, brush, geometry, PixelsPerDip);
        else
            dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>
    /// The first centre arrangement whose text fits completely (nothing cut, no word split across lines), trying
    /// ever more compact versions; the most compact one if none does.
    /// </summary>
    CentreBlock ChooseCentre(WheelSession session, double radius)
    {
        CentreBlock? block = null;
        foreach (var items in CentreCandidates(session))
        {
            block = LayoutCentreBlock(items, radius);
            if (!block.Degraded)
                break;
        }
        return block!;
    }

    /// <summary>The centre text of the current state (tests): one entry per line.</summary>
    internal IReadOnlyList<string> CentreLines()
    {
        if (_session is null)
            return [];
        var block = ChooseCentre(_session, Metrics.CentreRadius - 11);
        return block.Items.Select((item, k) => item is CentreText ? block.Lines[k] : []).SelectMany(l => l).ToList();
    }

    // ---------------------------------------------------------------- centre content

    abstract record CentreItem(double Size, double SpaceBefore);

    sealed record CentreGlyph(string Text, FontFamily Family, double GlyphSize, Brush Brush, double Space) : CentreItem(GlyphSize, Space);

    sealed record CentreText(string Text, double FontSize, Typeface Typeface, Brush Brush, int MaxLines, double Space, double Tracking = 0)
        : CentreItem(FontSize, Space);

    /// <summary>The centre's content for the current state, best first, then ever more compact fallbacks.</summary>
    IEnumerable<List<CentreItem>> CentreCandidates(WheelSession session)
    {
        var model = session.Model;
        var summary = model.Summary;
        var noun = Noun(model.Mode);

        if (model.IsEmpty)
        {
            List<CentreItem> Empty(bool glyph, double headline)
            {
                var items = new List<CentreItem>();
                if (glyph)
                    AddGlyph(items, IconGlyphs.KeyForKind(summary.Kind), 24, _palette.TextSecondary, 0);
                items.Add(new CentreText(EmptyHeadline(model), headline, _palette.Semibold, _palette.Text, 2, glyph ? 7 : 0));
                items.Add(new CentreText(EmptyDetail(model), 11, _palette.Regular, _palette.TextSecondary, 2, 3));
                if (summary.Kind is null && model.Files.Count > 1)
                    items.Add(new CentreText("Try one kind at a time", 10.5, _palette.Regular, _palette.Hint, 1, 7));
                return items;
            }
            yield return Empty(true, 13.5);
            yield return Empty(false, 13);
            yield break;
        }

        if (session.IsOverMore)
        {
            var more = session.Segments[session.Hover.Segment];
            var items = new List<CentreItem>();
            AddGlyph(items, IconGlyphs.MoreKey, 22, _palette.AccentLight, 0);
            items.Add(new CentreText($"{more.HiddenCount} more {noun}s", 14, _palette.Semibold, _palette.Text, 2, 6));
            items.Add(new CentreText(
                _interaction == WheelInteraction.Click ? "Click to show them" : "Hold here to show them",
                11, _palette.Regular, _palette.TextSecondary, 2, 3));
            items.Add(new CentreText($"Page {session.PageIndex + 1} of {session.Pages.Count}", 10, _palette.Regular, _palette.Hint, 1, 6));
            yield return items;
            yield break;
        }

        if (session.HoveredOption is { } option)
        {
            // The title and the explanation matter most (e.g. why DXV has no alpha and what to use instead): first
            // drop the file-name line, then step the type down, rather than cut a sentence.
            var titleBrush = option.IsAvailable ? _palette.Text : _palette.TextSecondary;
            List<CentreItem> Hovered(bool headline, double title, double body, int bodyLines)
            {
                var items = new List<CentreItem>();
                if (headline)
                    items.Add(new CentreText(summary.Headline, 10.5, _palette.Regular, _palette.Hint, 1, 0));
                items.Add(new CentreText(option.Info.Title, title, _palette.Semibold, titleBrush, 3, headline ? 5 : 0));
                if (!option.IsAvailable)
                    items.Add(new CentreText(option.Availability.Reason ?? "Not available on this PC", body, _palette.Semibold, _palette.Reason, bodyLines, 6));
                else if (!string.IsNullOrWhiteSpace(option.Info.Description))
                    items.Add(new CentreText(option.Info.Description!, body, _palette.Regular, _palette.TextSecondary, bodyLines, 5));
                return items;
            }
            yield return Hovered(true, 14, 11, 4);
            yield return Hovered(false, 14, 11, 5);
            yield return Hovered(false, 13, 10.5, 5);
            yield return Hovered(false, 12, 10, 6);
            yield break;
        }

        var hint = _interaction == WheelInteraction.Click
            ? (session.KeyboardActive ? $"Enter to choose · Esc to close" : $"Click a {noun} · Esc to close")
            : $"Drop on a {noun}";
        if (session.Pages.Count > 1)
            hint += $" · page {session.PageIndex + 1}/{session.Pages.Count}";
        List<CentreItem> Idle(bool glyph, double headline)
        {
            var items = new List<CentreItem>
            {
                new CentreText(ModeTitle(model.Mode).ToUpperInvariant(), 9.5, _palette.Semibold, _palette.Accent, 1, 0, Tracking: 1.6),
            };
            if (glyph)
                AddGlyph(items, IconGlyphs.KeyForKind(summary.Kind), 26, _palette.AccentLight, 8);
            items.Add(new CentreText(summary.Headline, headline, _palette.Semibold, _palette.Text, 2, glyph ? 7 : 6));
            if (summary.Detail is { } detail)
                items.Add(new CentreText(detail, 11, _palette.Regular, _palette.TextSecondary, 2, 2));
            items.Add(new CentreText(hint, 10.5, _palette.Regular, _palette.Hint, 2, 8));
            return items;
        }
        yield return Idle(true, 14);
        yield return Idle(true, 13);
        yield return Idle(false, 13);
    }

    void AddGlyph(List<CentreItem> items, string key, double size, Brush brush, double space)
    {
        if (IconGlyphs.Resolve(key, _palette.IconFont) is { } glyph)
            items.Add(new CentreGlyph(glyph.Text, glyph.Family, size, brush, space));
    }

    static string Noun(WheelMode mode) => mode switch
    {
        WheelMode.Tools => "tool",
        WheelMode.Actions => "action",
        _ => "format",
    };

    static string ModeTitle(WheelMode mode) => mode switch
    {
        WheelMode.Tools => "Tools",
        WheelMode.Actions => "Actions",
        _ => "Convert",
    };

    static string EmptyHeadline(WheelModel model)
    {
        var things = model.Mode switch
        {
            WheelMode.Tools => "tools",
            WheelMode.Actions => "actions",
            _ => "conversions",
        };
        if (model.Summary.Kind is null && model.Files.Count > 1)
            return $"No {things} fit this mix";
        return model.Files.Count == 1 ? $"No {things} for this file" : $"No {things} for these files";
    }

    static string EmptyDetail(WheelModel model)
    {
        var summary = model.Summary;
        if (summary.Kind is null && model.Files.Count > 1)
            return summary.Detail ?? summary.Headline;
        return model.Files.Count == 1 ? summary.Headline : $"{summary.Headline} · {summary.Detail}";
    }

    /// <summary>Centre items with their wrapped lines; <see cref="Degraded"/> when text was cut or a word split.</summary>
    sealed record CentreBlock(List<CentreItem> Items, List<IReadOnlyList<string>> Lines, bool Degraded);

    CentreBlock LayoutCentreBlock(List<CentreItem> items, double radius)
    {
        if (items.Count == 0)
            return new CentreBlock(items, [], false);

        // Lines are wrapped to the chord of the circle at their own height, which depends on the block height:
        // estimate with the widest chord, then re-wrap at the real positions until stable. If the block is too
        // tall, give the last paragraph fewer lines.
        var maxLines = items.Select(i => i is CentreText t ? t.MaxLines : 1).ToArray();
        var wrapped = items.Select((item, k) => Wrap(item, maxLines[k], _ => 2 * radius)).ToList();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var top = -BlockHeight(items, wrapped) / 2;
                var next = new List<IReadOnlyList<string>>(items.Count);
                for (var k = 0; k < items.Count; k++)
                {
                    top += items[k].SpaceBefore;
                    var lineHeight = LineHeight(items[k]);
                    var itemTop = top;
                    var lines = Wrap(items[k], maxLines[k], line => Chord(radius, itemTop + line * lineHeight, itemTop + (line + 1) * lineHeight));
                    next.Add(lines);
                    top += lines.Count * lineHeight;
                }
                wrapped = next;
            }
            if (BlockHeight(items, wrapped) <= radius * 2 - 6)
                break;
            var last = Array.FindLastIndex(maxLines, n => n > 1);
            if (last < 0)
                break;
            maxLines[last]--;
        }

        var degraded = BlockHeight(items, wrapped) > radius * 2 - 6
            || items.Select((item, k) => item is CentreText { Tracking: <= 0 } text && TextWrap.IsDegraded(text.Text, wrapped[k])).Any(d => d);
        return new CentreBlock(items, wrapped, degraded);
    }

    void DrawCentreBlock(DrawingContext dc, CentreBlock block)
    {
        var (items, wrapped) = (block.Items, block.Lines);
        if (items.Count == 0)
            return;
        var y = Centre.Y - BlockHeight(items, wrapped) / 2;
        for (var k = 0; k < items.Count; k++)
        {
            y += items[k].SpaceBefore;
            var lineHeight = LineHeight(items[k]);
            foreach (var line in wrapped[k])
            {
                switch (items[k])
                {
                    case CentreGlyph glyph:
                    {
                        var icon = Format(glyph.Text, glyph.GlyphSize, new Typeface(glyph.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), glyph.Brush);
                        dc.DrawText(icon, Snap(new Point(Centre.X - icon.Width / 2, y + (lineHeight - icon.Height) / 2)));
                        break;
                    }
                    case CentreText { Tracking: > 0 } text:
                        DrawTracked(dc, line, text, y + (lineHeight - Format(line, text.FontSize, text.Typeface, text.Brush).Height) / 2);
                        break;
                    case CentreText text:
                    {
                        var formatted = Format(line, text.FontSize, text.Typeface, text.Brush);
                        dc.DrawText(formatted, Snap(new Point(Centre.X - formatted.Width / 2, y + (lineHeight - formatted.Height) / 2)));
                        break;
                    }
                }
                y += lineHeight;
            }
        }
    }

    IReadOnlyList<string> Wrap(CentreItem item, int maxLines, Func<int, double> widthOfLine) => item switch
    {
        CentreGlyph glyph => [glyph.Text],
        CentreText { Tracking: > 0 } text => [text.Text],
        CentreText text => TextWrap.WrapBalanced(text.Text, s => Measure(s, text.FontSize, text.Typeface), widthOfLine, maxLines),
        _ => [],
    };

    static double LineHeight(CentreItem item) => item is CentreGlyph glyph ? glyph.GlyphSize + 2 : LabelFitter.LineHeightFor(item.Size);

    static double BlockHeight(List<CentreItem> items, List<IReadOnlyList<string>> wrapped)
    {
        var height = 0.0;
        for (var k = 0; k < items.Count; k++)
            height += items[k].SpaceBefore + wrapped[k].Count * LineHeight(items[k]);
        return height;
    }

    static double Chord(double radius, double y0, double y1)
    {
        var y = y0 < 0 && y1 > 0 ? 0 : Math.Max(Math.Abs(y0), Math.Abs(y1));
        return y >= radius ? 0 : 2 * Math.Sqrt(radius * radius - y * y);
    }

    void DrawTracked(DrawingContext dc, string text, CentreText style, double y)
    {
        var glyphs = text.Select(ch => Format(ch.ToString(), style.FontSize, style.Typeface, style.Brush)).ToList();
        var width = glyphs.Sum(g => g.WidthIncludingTrailingWhitespace) + style.Tracking * (glyphs.Count - 1);
        var x = Centre.X - width / 2;
        foreach (var glyph in glyphs)
        {
            dc.DrawText(glyph, Snap(new Point(x, y)));
            x += glyph.WidthIncludingTrailingWhitespace + style.Tracking;
        }
    }

    // ---------------------------------------------------------------- text & paint helpers

    FormattedText Format(string text, double size, Typeface typeface, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, null, TextFormattingMode.Display, PixelsPerDip);

    double Measure(string text, double size, Typeface typeface)
    {
        var key = (text, size, typeface.Weight.ToOpenTypeWeight());
        if (!_widths.TryGetValue(key, out var width))
        {
            width = Format(text, size, typeface, Brushes.Black).Width;
            if (_widths.Count > 4000)
                _widths.Clear();
            _widths[key] = width;
        }
        return width;
    }

    Point Snap(Point point)
    {
        var ppd = PixelsPerDip;
        return new Point(Math.Round(point.X * ppd) / ppd, Math.Round(point.Y * ppd) / ppd);
    }

    /// <summary>Shadow brushes by size and colour: one frozen instance each, so their dithered fills are cached too.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(double Disc, double Shadow, Color Colour), Brush> ShadowBrushes = new();

    Brush ShadowBrush(double discRadius, double shadowRadius) =>
        ShadowBrushes.GetOrAdd((Math.Round(discRadius, 3), Math.Round(shadowRadius, 3), _palette.ShadowColor), key =>
        {
            var shadow = key.Colour;
            var start = Math.Max(0, (key.Disc - 12) / key.Shadow);
            var brush = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(shadow, 0),
                    new GradientStop(shadow, start),
                    new GradientStop(Color.FromArgb((byte)(shadow.A * 0.35), shadow.R, shadow.G, shadow.B), start + (1 - start) * 0.45),
                    new GradientStop(Color.FromArgb(0, shadow.R, shadow.G, shadow.B), 1),
                },
            };
            brush.Freeze();
            return brush;
        });

    static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
