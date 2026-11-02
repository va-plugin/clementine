using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Clementine.App.Branding;

/// <summary>Loads the app icon for windows, the tray and in-app logos.</summary>
public static class AppIcon
{
    static readonly Uri IcoUri = new("pack://application:,,,/Clementine;component/assets/clementine.ico", UriKind.Absolute);
    static ImageSource? _windowIcon;
    static ImageSource? _logo;

    /// <summary>Multi-size icon for <see cref="Window.Icon"/> (falls back to the vector art).</summary>
    public static ImageSource WindowIcon => _windowIcon ??= LoadWindowIcon();

    /// <summary>The vector mark, crisp at any size.</summary>
    public static ImageSource Logo => _logo ??= IconArt.CreateImage();

    /// <summary>A GDI icon for the tray at <paramref name="pixels"/> px (WinForms is used only for the tray icon).</summary>
    public static System.Drawing.Icon CreateTrayIcon(int pixels)
    {
        try
        {
            var resource = Application.GetResourceStream(IcoUri);
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, pixels, pixels);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
        }

        // Fallback: build a one-size icon from the vector art.
        using var memory = new MemoryStream();
        IcoWriter.Write(memory, [IconRenderer.Render(pixels)]);
        memory.Position = 0;
        return new System.Drawing.Icon(memory, pixels, pixels);
    }

    static ImageSource LoadWindowIcon()
    {
        try
        {
            if (Application.GetResourceStream(IcoUri) is not null)
            {
                // A frame of the decoded .ico lets WPF pick the best size for the caption and the taskbar.
                var frame = BitmapFrame.Create(IcoUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException or FileFormatException)
        {
        }
        return IconRenderer.Render(64);
    }
}
