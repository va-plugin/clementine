using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Clementine.App.Interop.NativeMethods;

namespace Clementine.App.Interop;

/// <summary>The icons Explorer shows for files and folders, as crisp WPF images. Cached per extension.</summary>
internal static class ShellIcons
{
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions whose icon is per-file rather than per-type.</summary>
    static readonly HashSet<string> PerFile = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms" };

    /// <summary>Icon for <paramref name="path"/> at roughly <paramref name="size"/> device-independent pixels (call on the UI thread).</summary>
    public static ImageSource? For(string path, int size = 32)
    {
        var isFolder = Directory.Exists(path);
        var extension = Path.GetExtension(path);
        var key = isFolder ? $"<folder>:{size}" : PerFile.Contains(extension) ? $"{path}:{size}" : $"{extension}:{size}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
        }

        ImageSource? image = null;
        try
        {
            // Render at 2x so the icon stays sharp on high-DPI monitors.
            image = File.Exists(path) || isFolder ? FromImageFactory(path, size * 2) : null;
            image ??= FromFileInfo(path, isFolder);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or ExternalException)
        {
        }

        image?.Freeze();
        lock (Cache)
            Cache[key] = image;
        return image;
    }

    static ImageSource? FromImageFactory(string path, int pixels)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, IShellItemImageFactoryGuid, out var item) != 0 || item is not IShellItemImageFactory factory)
            return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = pixels, cy = pixels }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbitmap) != 0 || hbitmap == IntPtr.Zero)
                return null;
            try
            {
                return FromHBitmap(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>Copies a 32-bit HBITMAP keeping its alpha channel (Imaging.CreateBitmapSourceFromHBitmap drops it).</summary>
    static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0)
            return null;
        var width = bm.bmWidth;
        var height = Math.Abs(bm.bmHeight);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        var format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        return BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
    }

    static ImageSource? FromFileInfo(string path, bool isFolder)
    {
        var info = new SHFILEINFO();
        var attributes = isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
