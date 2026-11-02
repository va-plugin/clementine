using System.Windows;
using System.Windows.Media;
using Clementine.App.Branding;
using Clementine.App.Interop;

namespace Clementine.App.Theme;

/// <summary>Shared look for every shell window, and access to the theme dictionary.</summary>
public static class ShellTheme
{
    public static readonly Uri DictionaryUri = new("pack://application:,,,/Clementine;component/Theme/Shell.xaml", UriKind.Absolute);

    /// <summary>Makes sure the shell styles are available (the app merges them in App.xaml; tests call this).</summary>
    public static void EnsureLoaded(Application application)
    {
        if (application.Resources.MergedDictionaries.Any(d => d.Source == DictionaryUri))
            return;
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DictionaryUri });
    }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    static DitheredGradient? _windowGradient;

    /// <summary>
    /// The warm window background (stops from <c>Shell.WindowBackground</c>), drawn dithered: painted directly, that
    /// slow dark gradient shows 8-bit bands.
    /// </summary>
    public static DitheredGradient WindowGradient =>
        _windowGradient ??= DitheredGradient.FromBrush((LinearGradientBrush)Application.Current.FindResource("Shell.WindowBackground"));

    /// <summary>Dark Mica-like window: warm dithered gradient background, dark caption, app icon, crisp text.</summary>
    public static void ApplyWindow(Window window, bool framed = true)
    {
        window.FontFamily = (FontFamily)Application.Current.FindResource("Clementine.Font");
        window.FontSize = 13;
        window.Foreground = Brush("Clementine.Text");
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        try
        {
            window.Icon = AppIcon.WindowIcon;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
        }
        if (framed)
        {
            WindowBackdrop.Attach(window, WindowGradient);
            WindowEffects.UseDarkChrome(window);
        }
    }
}
