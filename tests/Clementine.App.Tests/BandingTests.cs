using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Clementine.App.Theme;

namespace Clementine.App.Tests;

/// <summary>
/// The window background is a slow, dark gradient (~16 levels across the whole window). Painted by WPF it showed
/// 8-bit steps; it is now rendered dithered. "Band edge" = two neighbouring runs of identical colour, both at least
/// 12 px long, one level apart: the staircase signature of an undithered gradient.
/// </summary>
public class BandingTests
{
    const int MinRun = 12;

    [Fact]
    public void Undithered_window_gradient_would_band_and_the_dithered_one_does_not()
    {
        // The theme lives on the shared UI thread; the pixel work runs here (frozen bitmaps are free-threaded) so the
        // other UI tests aren't held up.
        var gradient = WpfHost.Run(() => ShellTheme.WindowGradient);
        const int w = 2000, h = 1300;
        var dithered = Pixels(gradient.Render(w, h));
        var naive = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (r, g, b) = gradient.Sample(gradient.PositionAt(x + 0.5, y + 0.5, w, h));
                var o = (y * w + x) * 4;
                naive[o] = (byte)Math.Round(b);
                naive[o + 1] = (byte)Math.Round(g);
                naive[o + 2] = (byte)Math.Round(r);
            }
        }

        Assert.True(BandEdges(naive, w, h) > 1000, "the meter sees the bands of a plain 8-bit gradient");
        Assert.Equal(0, BandEdges(dithered, w, h));
    }

    [Fact]
    public void Dithering_keeps_the_exact_colour_and_stays_within_one_level()
    {
        var gradient = WpfHost.Run(() => ShellTheme.WindowGradient);
        const int w = 960, h = 640, block = 32;
        var pixels = Pixels(gradient.Render(w, h));
        var worstMean = 0.0;
        var worstPixel = 0.0;
        for (var by = 0; by < h; by += block)
        {
            for (var bx = 0; bx < w; bx += block)
            {
                double sum = 0, exact = 0;
                for (var y = by; y < by + block; y++)
                {
                    for (var x = bx; x < bx + block; x++)
                    {
                        var (r, _, _) = gradient.Sample(gradient.PositionAt(x + 0.5, y + 0.5, w, h));
                        var red = pixels[(y * w + x) * 4 + 2];
                        sum += red;
                        exact += r;
                        worstPixel = Math.Max(worstPixel, Math.Abs(red - r));
                    }
                }
                worstMean = Math.Max(worstMean, Math.Abs(sum - exact) / (block * block));
            }
        }
        Assert.True(worstMean < 0.25, $"block averages match the true gradient (worst {worstMean:0.###} levels)");
        Assert.True(worstPixel < 1.02, $"no pixel is more than one level off (worst {worstPixel:0.###})");
    }

    [Fact]
    public void Dithered_gradient_matches_wpfs_own_geometry_on_a_wide_area()
    {
        // WPF scales relative start/end points to the box and keeps the bands perpendicular in box space; a wide
        // toast-sized area shows any mismatch clearly. Uses a high-contrast ramp so the comparison is meaningful.
        const int w = 840, h = 328;
        var (wpf, ours) = WpfHost.Run(() =>
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x10, 0x40, 0xF0), 0),
                    new GradientStop(Color.FromRgb(0xF0, 0x80, 0x20), 0.55),
                    new GradientStop(Color.FromRgb(0x20, 0x20, 0x20), 1),
                },
            };
            var rectangle = new Rectangle { Width = w, Height = h, Fill = brush };
            rectangle.Measure(new Size(w, h));
            rectangle.Arrange(new Rect(0, 0, w, h));
            var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            target.Render(rectangle);
            return (Pixels(target), Pixels(DitheredGradient.FromBrush(brush).Render(w, h)));
        });

        const int block = 8;
        var worst = 0.0;
        for (var by = 0; by + block <= h; by += block)
        {
            for (var bx = 0; bx + block <= w; bx += block)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    double a = 0, b = 0;
                    for (var y = by; y < by + block; y++)
                    {
                        for (var x = bx; x < bx + block; x++)
                        {
                            a += wpf[(y * w + x) * 4 + channel];
                            b += ours[(y * w + x) * 4 + channel];
                        }
                    }
                    worst = Math.Max(worst, Math.Abs(a - b) / (block * block));
                }
            }
        }
        Assert.True(worst < 4, $"same colours in the same places as WPF's gradient (worst block {worst:0.#} levels)");
    }

    [Fact]
    public void Huge_areas_are_capped_and_tiny_ones_still_get_a_pixel()
    {
        var (w, h) = DitheredGradient.PixelSize(3840, 2160, 2, 2);
        Assert.True((long)w * h <= DitheredGradient.MaxPixels);
        Assert.InRange(w / (double)h, 1.76, 1.79);
        Assert.Equal((1, 1), DitheredGradient.PixelSize(0.2, 0.2, 1, 1));
        Assert.Equal((1500, 1020), DitheredGradient.PixelSize(1000, 680, 1.5, 1.5));
    }

    [Fact]
    public void Framed_windows_paint_a_dithered_backdrop_at_device_pixels_and_follow_resizes()
    {
        var (drawn, plain, w, h) = WpfHost.Run(() =>
        {
            var window = new Window
            {
                Width = 420,
                Height = 300,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false,
            };
            ShellTheme.ApplyWindow(window);
            Assert.Same(ShellTheme.WindowGradient, WindowBackdrop.GetGradient(window));
            try
            {
                window.Show();
                PumpFor(TimeSpan.FromMilliseconds(50));
                AssertBackdropFitsClient(window);

                window.Width = 900;
                window.Height = 640;
                PumpFor(TimeSpan.FromMilliseconds(600));
                AssertBackdropFitsClient(window);

                // What the window actually draws, and the plain theme brush painted on the same area.
                var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
                var drawn = RenderAtDevicePixels(client, window, out var width, out var height);
                var reference = new Border { Background = (Brush)Application.Current.FindResource("Shell.WindowBackground") };
                reference.Measure(new Size(client.ActualWidth, client.ActualHeight));
                reference.Arrange(new Rect(0, 0, client.ActualWidth, client.ActualHeight));
                return (drawn, RenderAtDevicePixels(reference, window, out _, out _), width, height);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(0, BandEdges(drawn, w, h));
        Assert.True(BandEdges(plain, w, h) > 100, "the plain gradient bands at this size");
    }

    static void AssertBackdropFitsClient(Window window)
    {
        var brush = Assert.IsType<ImageBrush>(window.Background);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(brush.ImageSource);
        var client = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.Equal((int)Math.Round(client.ActualWidth * dpi.DpiScaleX), bitmap.PixelWidth);
        Assert.Equal((int)Math.Round(client.ActualHeight * dpi.DpiScaleY), bitmap.PixelHeight);
    }

    static byte[] RenderAtDevicePixels(FrameworkElement element, Window window, out int width, out int height)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        width = (int)Math.Round(element.ActualWidth * dpi.DpiScaleX);
        height = (int)Math.Round(element.ActualHeight * dpi.DpiScaleY);
        var target = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        target.Render(element);
        return Pixels(target);
    }

    static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static byte[] Pixels(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgr32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>Counts band edges along every 8th row and column.</summary>
    static int BandEdges(byte[] pixels, int width, int height)
    {
        var edges = 0;
        for (var y = 0; y < height; y += 8)
            edges += EdgesAlong(pixels, width, i => (y * width + i) * 4);
        for (var x = 0; x < width; x += 8)
            edges += EdgesAlong(pixels, height, i => (i * width + x) * 4);
        return edges;
    }

    static int EdgesAlong(byte[] pixels, int length, Func<int, int> offset)
    {
        var edges = 0;
        var runStart = 0;
        var previousRun = 0;
        var previousColour = -1;
        for (var i = 1; i <= length; i++)
        {
            if (i < length && Same(pixels, offset(i), offset(runStart)))
                continue;
            var run = i - runStart;
            var colour = offset(runStart);
            if (run >= MinRun && previousRun >= MinRun && previousColour >= 0 && OneLevelApart(pixels, previousColour, colour))
                edges++;
            previousRun = run;
            previousColour = colour;
            runStart = i;
        }
        return edges;
    }

    static bool Same(byte[] p, int a, int b) => p[a] == p[b] && p[a + 1] == p[b + 1] && p[a + 2] == p[b + 2];

    static bool OneLevelApart(byte[] p, int a, int b)
    {
        var d = 0;
        for (var c = 0; c < 3; c++)
            d = Math.Max(d, Math.Abs(p[a + c] - p[b + c]));
        return d == 1;
    }
}
