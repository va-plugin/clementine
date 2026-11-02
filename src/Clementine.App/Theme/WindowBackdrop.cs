using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Clementine.App.Theme;

/// <summary>
/// Paints a framed window's client area with a <see cref="DitheredGradient"/>, rendered at the client's exact device
/// pixel size and re-rendered when the size or DPI changes. While the user drags the frame the previous render is
/// stretched (no per-frame work) and a fresh one follows once the size settles.
/// </summary>
public sealed class WindowBackdrop
{
    /// <summary>Quiet time after the last size change before re-rendering during a continuous resize.</summary>
    static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    /// <summary>The gradient a window's background is drawn with (snapshots read it to render the same backdrop).</summary>
    public static readonly DependencyProperty GradientProperty = DependencyProperty.RegisterAttached(
        "Gradient", typeof(DitheredGradient), typeof(WindowBackdrop), new PropertyMetadata(null));

    readonly Window _window;
    readonly DitheredGradient _gradient;
    readonly DispatcherTimer _timer;
    (int Width, int Height) _rendered;
    long _lastRender = long.MinValue / 2;

    WindowBackdrop(Window window, DitheredGradient gradient)
    {
        _window = window;
        _gradient = gradient;
        _timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher) { Interval = Settle };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Render();
        };
    }

    public static DitheredGradient? GetGradient(DependencyObject element) => (DitheredGradient?)element.GetValue(GradientProperty);

    /// <summary>
    /// Gives <paramref name="window"/> the dithered background. Until its first layout (before anything is drawn) the
    /// background is the gradient's flat average colour.
    /// </summary>
    public static void Attach(Window window, DitheredGradient gradient)
    {
        window.SetValue(GradientProperty, gradient);
        var fallback = new SolidColorBrush(gradient.Fallback);
        fallback.Freeze();
        window.Background = fallback;
        var backdrop = new WindowBackdrop(window, gradient);
        window.SizeChanged += (_, _) => backdrop.Invalidate();
        window.DpiChanged += (_, _) => backdrop.Invalidate();
        window.Closed += (_, _) => backdrop._timer.Stop();
    }

    /// <summary>The client area in device pixels (the window's own size includes the frame).</summary>
    internal (int Width, int Height)? ClientPixels()
    {
        if (VisualTreeHelper.GetChildrenCount(_window) == 0 || VisualTreeHelper.GetChild(_window, 0) is not FrameworkElement client)
            return null;
        if (client.ActualWidth < 1 || client.ActualHeight < 1)
            return null;
        var dpi = VisualTreeHelper.GetDpi(_window);
        return DitheredGradient.PixelSize(client.ActualWidth, client.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY);
    }

    void Invalidate()
    {
        // Discrete changes (first layout, maximise, monitor change) render at once; a stream of changes from a frame
        // drag renders when it pauses.
        if (Environment.TickCount64 - _lastRender > 2 * Settle.TotalMilliseconds)
        {
            Render();
            return;
        }
        _timer.Stop();
        _timer.Start();
    }

    void Render()
    {
        if (ClientPixels() is not { } size || size == _rendered)
            return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(_window);
            var bitmap = _gradient.Render(size.Width, size.Height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY);
            var brush = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
            brush.Freeze();
            _window.Background = brush;
            _rendered = size;
            _lastRender = Environment.TickCount64;
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or InvalidOperationException)
        {
            // Keep whatever background is showing; a flat or stretched backdrop is better than a crash.
        }
    }
}
