using System.Windows;
using Clementine.App.Interop;
using Clementine.App.Theme;
using Clementine.Core.Hosting;

namespace Clementine.App.Toasts;

/// <summary>
/// Transparent, topmost, non-activating window that stacks toast cards in the bottom-right corner of the monitor
/// that had the cursor when the first card appeared (just above the taskbar).
/// </summary>
public partial class ToastHostWindow : Window
{
    MonitorArea? _monitor;

    public ToastHostWindow()
    {
        InitializeComponent();
        ShellTheme.ApplyWindow(this, framed: false);
        WindowEffects.MakeNoActivateToolWindow(this);
        SizeChanged += (_, _) => Reposition();
        // A monitor unplugged, a resolution or scale change, or the taskbar moving: re-anchor the stack.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    }

    void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(Reposition));

    public int CardCount => Cards.Children.Count;

    public void AddCard(ToastCard card)
    {
        if (Cards.Children.Count == 0 || !IsVisible)
            _monitor = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition());
        Cards.Children.Add(card);
        if (!IsVisible)
            Show();
        Reposition();
        card.AnimateIn();
    }

    public void RemoveCard(ToastCard card)
    {
        Cards.Children.Remove(card);
        if (Cards.Children.Count == 0)
            Hide();
        else
            Reposition();
    }

    public IEnumerable<ToastCard> AllCards => Cards.Children.OfType<ToastCard>().ToList();

    void Reposition()
    {
        if (!IsVisible || _monitor is not { } stale)
            return;
        // Re-read the monitor every time: its work area changes with the taskbar, and it may have been unplugged
        // (the nearest remaining monitor takes over).
        var monitor = ScreenGeometry.Refresh(stale);
        _monitor = monitor;
        // Make sure we're on the target monitor first so WPF uses its DPI, then align the bottom-right corner.
        var (width, height) = ScreenGeometry.PixelSize(this);
        var (x, y) = ScreenGeometry.BottomRight(monitor.WorkArea, width, height, margin: 0);
        ScreenGeometry.MoveTo(this, x, y, topmost: true);
        UpdateLayout();
        var (newWidth, newHeight) = ScreenGeometry.PixelSize(this);
        if (newWidth != width || newHeight != height)
        {
            (x, y) = ScreenGeometry.BottomRight(monitor.WorkArea, newWidth, newHeight, margin: 0);
            ScreenGeometry.MoveTo(this, x, y, topmost: true);
        }
    }

    /// <summary>Anchor used when a test or snapshot wants a specific monitor.</summary>
    internal void UseMonitorAt(ScreenPoint point) => _monitor = ScreenGeometry.MonitorAt(point);
}
