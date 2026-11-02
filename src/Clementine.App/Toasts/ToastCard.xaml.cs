using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Clementine.App.Interop;
using Clementine.App.Theme;

namespace Clementine.App.Toasts;

/// <summary>Things a user can do on a toast card.</summary>
public enum ToastAction
{
    Close,
    ShowInFolder,
    Open,
    CopyText,
    ViewText,
    Details,
    Custom,
}

/// <summary>One job (or batch) card in the toast stack. Purely visual: call <see cref="Apply"/> with a new state.</summary>
public partial class ToastCard : UserControl
{
    string? _iconPath;

    public ToastCard()
    {
        InitializeComponent();
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        CloseButton.Click += (_, _) => Raise(ToastAction.Close);
        ShowInFolderButton.Click += (_, _) => Raise(ToastAction.ShowInFolder);
        OpenButton.Click += (_, _) => Raise(ToastAction.Open);
        CopyButton.Click += (_, _) => Raise(ToastAction.CopyText);
        ViewButton.Click += (_, _) => Raise(ToastAction.ViewText);
        DetailsButton.Click += (_, _) => Raise(ToastAction.Details);
        ActionButton.Click += (_, _) => Raise(ToastAction.Custom);
    }

    public ToastState? State { get; private set; }

    public event EventHandler<ToastAction>? ActionRequested;

    /// <summary>Updates every visual from <paramref name="state"/>.</summary>
    public void Apply(ToastState state)
    {
        var announce = State is null || State.Kind != state.Kind || State.Title != state.Title;
        State = state;
        // Cards appear without taking focus, so they are a live region: screen readers read new titles politely.
        AutomationProperties.SetName(this, string.IsNullOrEmpty(state.Subtitle) ? state.Title : $"{state.Title}, {state.Subtitle}");
        if (announce)
            UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        TitleText.Text = state.Title;
        TitleText.ToolTip = state.Title;
        SubtitleText.Text = state.Subtitle ?? "";
        SubtitleText.Visibility = string.IsNullOrEmpty(state.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        // File names stay on one line; a message (first-run hint, "can't run this") wraps.
        SubtitleText.TextWrapping = state.Kind == ToastKind.Info ? TextWrapping.Wrap : TextWrapping.NoWrap;
        SubtitleText.Margin = new Thickness(0, 2, state.Kind == ToastKind.Info ? 6 : 0, 0);

        ApplyIcon(state);

        var running = state.Kind == ToastKind.Running;
        ProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            Progress.IsIndeterminate = state.Progress is null;
            if (state.Progress is { } p)
                Progress.Value = p;
            StatusText.Text = state.Status ?? "";
        }
        CloseButton.ToolTip = running ? "Cancel" : "Dismiss";

        var failed = state.Kind is ToastKind.Failed or ToastKind.PartlyFailed;
        ErrorText.Text = state.ErrorMessage ?? "";
        ErrorText.Visibility = failed && !string.IsNullOrEmpty(state.ErrorMessage) ? Visibility.Visible : Visibility.Collapsed;

        var preview = state.Kind == ToastKind.Succeeded ? state.TextPreview() : null;
        TextPreview.Text = preview ?? "";
        TextPreviewBox.Visibility = preview is null ? Visibility.Collapsed : Visibility.Visible;

        WarningsPanel.Children.Clear();
        foreach (var warning in state.Warnings.Take(3))
            WarningsPanel.Children.Add(WarningLine(warning));
        WarningsPanel.Visibility = state.Warnings.Count > 0 && !running ? Visibility.Visible : Visibility.Collapsed;

        DoneText.Text = state.Status ?? "";
        DoneText.Visibility = Show(state.Kind is ToastKind.Succeeded or ToastKind.PartlyFailed && !string.IsNullOrEmpty(state.Status));

        var done = state.Kind is ToastKind.Succeeded or ToastKind.PartlyFailed;
        ShowInFolderButton.Visibility = Show(done && state.Outputs.Count > 0);
        OpenButton.Visibility = Show(done && state.Outputs.Count == 1);
        CopyButton.Visibility = Show(done && state.HasText);
        ViewButton.Visibility = Show(done && state.HasText);
        DetailsButton.Visibility = Show(failed && !string.IsNullOrEmpty(state.ErrorDetails ?? state.ErrorMessage));
        ActionButton.Visibility = Show(state.ActionLabel is not null);
        ActionButton.Content = state.ActionLabel;
        ButtonsPanel.Visibility = ButtonsPanel.Children.OfType<Button>().Any(b => b.Visibility == Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Slide-and-fade entrance (~140 ms).</summary>
    public void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
    }

    /// <summary>Fade out, then run <paramref name="completed"/>.</summary>
    public void AnimateOut(Action completed)
    {
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) => completed();
        BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(120)));
    }

    void ApplyIcon(ToastState state)
    {
        if (state.IconPath != _iconPath)
        {
            _iconPath = state.IconPath;
            FileIcon.Source = state.IconPath is null ? null : ShellIcons.For(state.IconPath, 36);
        }
        var hasFileIcon = FileIcon.Source is not null;
        FileIcon.Visibility = hasFileIcon ? Visibility.Visible : Visibility.Collapsed;
        GlyphTile.Visibility = hasFileIcon ? Visibility.Collapsed : Visibility.Visible;
        KindGlyph.Text = state.Kind == ToastKind.Info ? state.Glyph ?? Glyphs.Info : Glyphs.ForIconKey(state.IconKey);

        (string Glyph, string Brush)? badge = state.Kind switch
        {
            ToastKind.Succeeded => (Glyphs.Check, "Clementine.Success"),
            ToastKind.PartlyFailed => ("!", "Shell.Warning"),
            ToastKind.Failed => (Glyphs.Cancel, "Clementine.Error"),
            ToastKind.Canceled => (Glyphs.Cancel, "Clementine.TextSecondary"),
            _ => null,
        };
        if (state.Kind == ToastKind.Succeeded && state.Warnings.Count > 0)
            badge = ("!", "Shell.Warning");
        Badge.Visibility = badge is null ? Visibility.Collapsed : Visibility.Visible;
        if (badge is { } b)
        {
            BadgeGlyph.Text = b.Glyph;
            BadgeGlyph.FontFamily = b.Glyph == "!" ? (FontFamily)FindResource("Clementine.Font") : (FontFamily)FindResource("Clementine.IconFont");
            BadgeGlyph.FontSize = b.Glyph == "!" ? 11 : 8;
            Badge.Background = ShellTheme.Brush(b.Brush);
        }
    }

    FrameworkElement WarningLine(string warning)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        var glyph = new TextBlock
        {
            Text = Glyphs.Warning,
            Style = (Style)FindResource("Shell.Glyph"),
            FontSize = 12,
            Foreground = ShellTheme.Brush("Shell.Warning"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 7, 0),
        };
        DockPanel.SetDock(glyph, Dock.Left);
        panel.Children.Add(glyph);
        panel.Children.Add(new TextBlock { Text = warning, Style = (Style)FindResource("Shell.Caption"), Foreground = ShellTheme.Brush("Shell.Warning") });
        return panel;
    }

    static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    void Raise(ToastAction action) => ActionRequested?.Invoke(this, action);
}
