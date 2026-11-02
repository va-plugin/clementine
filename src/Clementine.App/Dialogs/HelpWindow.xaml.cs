using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clementine.App.Branding;
using Clementine.App.Preferences;
using Clementine.App.Theme;
using Clementine.Core;

namespace Clementine.App.Dialogs;

/// <summary>"How to use" page opened from the tray menu, the main window and the first-run hint.</summary>
public partial class HelpWindow : Window
{
    /// <summary>A fixed snapshot (design snapshots, tests).</summary>
    public HelpWindow(ClementineSettings settings)
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this);
        Logo.Source = AppIcon.Logo;
        CloseButton.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        Build(settings);
    }

    /// <summary>Follows <paramref name="store"/>: keys changed in Settings while this is open show up at once.</summary>
    public HelpWindow(SettingsStore store)
        : this(store.Current)
    {
        EventHandler changed = (_, _) =>
        {
            if (Dispatcher.CheckAccess())
                Build(store.Current);
            else
                Dispatcher.BeginInvoke(() => Build(store.Current));
        };
        store.Changed += changed;
        Closed += (_, _) => store.Changed -= changed;
    }

    void Build(ClementineSettings settings)
    {
        Steps.Children.Clear();
        AddStep("1", KeyText.Parts(settings.ConvertModifiers), "Hold the key while dragging files",
            "Start dragging files in Explorer (or any app), then hold the key. A wheel of formats appears under the pointer.");
        AddStep("2", [], "Drop on a format",
            "The inner ring holds formats such as MP4, ProRes, HAP or PNG. Hover one to see its variants on the outer ring, then let go on the one you want. Drop in the middle to cancel.");
        AddStep("3", [], "Find the result next to the original",
            "Clementine saves the new file beside the original and never overwrites anything. A card in the corner shows progress, with Show in folder when it's done.");
        AddStep("+", KeyText.Parts(settings.ToolsModifiers), "Tools",
            "Hold these keys instead for tools: trim, resize, compress, rotate, ping-pong loops, frame snapshots and more.");
        AddStep("+", KeyText.Parts(settings.ActionsModifiers), "Actions",
            "And these for actions: copy paths, zip, checksums, rename, pin to the shelf.");
        if (!string.IsNullOrWhiteSpace(settings.SelectionHotkey))
        {
            AddStep("+", settings.SelectionHotkey.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), "Selected files",
                "Select files in Explorer and press this shortcut to open the wheel without dragging.");
        }

        Tips.Text = string.Join(Environment.NewLine,
            "•  Drop several files at once: they're converted in parallel and shown as one card.",
            "•  For Resolume, DXV, HAP and ProRes come with or without audio, and HAP Alpha / ProRes 4444 keep transparency.",
            "•  Not dragging? Open Clementine from the tray icon and drop files on its window.",
            "•  Change keys, output folder and more in Settings.");
    }

    /// <summary>The key-cap texts of each step, in order (tests).</summary>
    internal IReadOnlyList<string> StepKeys() =>
        Steps.Children.OfType<Grid>()
            .Select(step => string.Join("+", step.Children.OfType<StackPanel>().SelectMany(b => b.Children.OfType<WrapPanel>())
                .SelectMany(h => h.Children.OfType<Border>()).Select(cap => (cap.Child as TextBlock)?.Text)))
            .ToList();

    void AddStep(string number, IEnumerable<string> keys, string title, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = number == "+" ? ShellTheme.Brush("Shell.Card") : ShellTheme.Brush("Clementine.AccentGradient"),
            BorderBrush = ShellTheme.Brush("Shell.CardStroke"),
            BorderThickness = new Thickness(number == "+" ? 1 : 0),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock
            {
                Text = number,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = number == "+" ? ShellTheme.Brush("Clementine.TextSecondary") : ShellTheme.Brush("Shell.OnAccent"),
            },
        };
        grid.Children.Add(badge);

        var body = new StackPanel();
        var heading = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var key in keys)
            heading.Children.Add(new Border { Style = (Style)FindResource("Shell.KeyCap"), Child = new TextBlock { Text = key, FontSize = 11.5, FontWeight = FontWeights.SemiBold }, Margin = new Thickness(0, 0, 4, 0) });
        heading.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Shell.Heading"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(keys.Any() ? 6 : 0, 0, 0, 0) });
        body.Children.Add(heading);
        body.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Shell.Caption"), FontSize = 13, LineHeight = 19 });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        Steps.Children.Add(grid);
    }
}
