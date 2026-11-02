using System.Windows;
using System.Windows.Media;

namespace Clementine.Wheel;

/// <summary>
/// Frozen brushes, fonts and sizes for drawing the wheel, read once from <c>Themes/WheelTheme.xaml</c> (which merges
/// the shared <c>Themes/Colors.xaml</c>). Missing keys fall back to built-in values, so drawing never fails.
/// Frozen, hence usable from any thread.
/// </summary>
internal sealed class WheelPalette
{
    public const string ColorsUri = "pack://application:,,,/Clementine.Wheel;component/Themes/Colors.xaml";
    const string ThemeUri = "/Clementine.Wheel;component/Themes/WheelTheme.xaml";

    static readonly Lazy<WheelPalette> Shared = new(() => Load(out _), LazyThreadSafetyMode.ExecutionAndPublication);

    WheelPalette(ResourceDictionary? resources)
    {
        Accent = BrushOr(resources, "Clementine.Accent", "#FFFF8A1F");
        AccentLight = BrushOr(resources, "Clementine.AccentLight", "#FFFFA94D");
        AccentGradient = BrushOr(resources, "Clementine.AccentGradient", null) ?? DefaultAccentGradient();
        Text = BrushOr(resources, "Clementine.Text", "#FFF6F2EE");
        TextSecondary = BrushOr(resources, "Clementine.TextSecondary", "#FFB5AEA6");
        TextDisabled = BrushOr(resources, "Clementine.TextDisabled", "#66FFFFFF");
        Disc = BrushOr(resources, "Wheel.Disc", "#EE1B1917");
        DiscEdge = BrushOr(resources, "Wheel.DiscEdge", "#30FFFFFF");
        DiscSheen = BrushOr(resources, "Wheel.DiscSheen", "#00FFFFFF");
        Segment = BrushOr(resources, "Wheel.Segment", "#14FFFFFF");
        SegmentDisabled = BrushOr(resources, "Wheel.SegmentDisabled", "#07FFFFFF");
        SegmentExpanded = BrushOr(resources, "Wheel.SegmentExpanded", "#3DFF8A1F");
        SegmentHoverDisabled = BrushOr(resources, "Wheel.SegmentHoverDisabled", "#24FFFFFF");
        VariantHint = BrushOr(resources, "Wheel.VariantHint", "#4DFFFFFF");
        Variant = BrushOr(resources, "Wheel.Variant", "#F5231F1C");
        VariantEdge = BrushOr(resources, "Wheel.VariantEdge", "#33FFFFFF");
        Centre = BrushOr(resources, "Wheel.Centre", "#FF1A1816");
        CentreEdge = BrushOr(resources, "Wheel.CentreEdge", "#24FFFFFF");
        TextOnAccent = BrushOr(resources, "Wheel.TextOnAccent", "#FF1F140A");
        Reason = BrushOr(resources, "Wheel.Reason", "#FFFFA94D");
        Hint = BrushOr(resources, "Wheel.Hint", "#8CF6F2EE");
        CatchArea = Frozen(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        ShadowColor = resources?["Wheel.ShadowColor"] is Color shadow ? shadow : Color.FromArgb(0x80, 0, 0, 0);
        AccentColor = Accent is SolidColorBrush accent ? accent.Color : Color.FromRgb(0xFF, 0x8A, 0x1F);

        Font = resources?["Clementine.Font"] as FontFamily ?? new FontFamily("Segoe UI Variable Text, Segoe UI");
        DisplayFont = resources?["Clementine.DisplayFont"] as FontFamily ?? new FontFamily("Segoe UI Variable Display, Segoe UI");
        IconFont = resources?["Clementine.IconFont"] as FontFamily ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        LabelSize = resources?["Wheel.LabelSize"] is double label ? label : 13;
        VariantSize = resources?["Wheel.VariantSize"] is double variant ? variant : 11.5;
        GlyphSize = resources?["Wheel.GlyphSize"] is double glyph ? glyph : 15;

        Regular = new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Semibold = new Typeface(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        DisplaySemibold = new Typeface(DisplayFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    }

    /// <summary>The shared palette (loaded on first use).</summary>
    public static WheelPalette Current => Shared.Value;

    public Brush Accent { get; }

    public Brush AccentLight { get; }

    public Brush AccentGradient { get; }

    public Color AccentColor { get; }

    public Brush Text { get; }

    public Brush TextSecondary { get; }

    public Brush TextDisabled { get; }

    public Brush Disc { get; }

    public Brush DiscEdge { get; }

    public Brush DiscSheen { get; }

    public Brush Segment { get; }

    public Brush SegmentDisabled { get; }

    public Brush SegmentExpanded { get; }

    public Brush SegmentHoverDisabled { get; }

    public Brush VariantHint { get; }

    public Brush Variant { get; }

    public Brush VariantEdge { get; }

    public Brush Centre { get; }

    public Brush CentreEdge { get; }

    public Brush TextOnAccent { get; }

    public Brush Reason { get; }

    public Brush Hint { get; }

    /// <summary>Alpha 1/255: invisible, but makes the area receive mouse and OLE drag input.</summary>
    public Brush CatchArea { get; }

    public Color ShadowColor { get; }

    public FontFamily Font { get; }

    public FontFamily DisplayFont { get; }

    public FontFamily IconFont { get; }

    public double LabelSize { get; }

    public double VariantSize { get; }

    public double GlyphSize { get; }

    public Typeface Regular { get; }

    public Typeface Semibold { get; }

    public Typeface DisplaySemibold { get; }

    /// <summary>Loads the theme; <paramref name="error"/> explains a fallback to built-in values.</summary>
    internal static WheelPalette Load(out Exception? error)
    {
        error = null;
        ResourceDictionary? resources = null;
        try
        {
            // LoadComponent initialises the WPF application statics that pack URIs need, even without an Application.
            resources = (ResourceDictionary)Application.LoadComponent(new Uri(ThemeUri, UriKind.Relative));
        }
        catch (Exception ex)
        {
            error = ex;
        }
        return new WheelPalette(resources);
    }

    static Brush BrushOr(ResourceDictionary? resources, string key, string? fallback)
    {
        if (resources?[key] is Brush brush)
            return Frozen(brush.CloneCurrentValue());
        return fallback is null ? null! : Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)));
    }

    static Brush DefaultAccentGradient() => Frozen(new LinearGradientBrush(
        Color.FromRgb(0xFF, 0xA9, 0x4D), Color.FromRgb(0xE8, 0x65, 0x0A), new Point(0, 0), new Point(1, 1)));

    static Brush Frozen(Brush brush)
    {
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }
}
