using System.Collections.Concurrent;
using ImageMagick;

namespace Clementine.Engines.Images.Imaging;

/// <summary>
/// One-time Magick.NET setup plus a cached, runtime probe of what this build of ImageMagick can really
/// read and encode. A format is only offered when a tiny round trip (encode, then decode) succeeds,
/// because "supports writing" in the format table does not guarantee the encoder delegate is present
/// (HEIC, for example, is listed but has no encoder in the bundled libheif).
/// </summary>
public static class MagickRuntime
{
    static readonly Lazy<bool> Initialized = new(Initialize, LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly ConcurrentDictionary<MagickFormat, bool> EncodeProbe = new();

    /// <summary>Makes sure global Magick settings are applied. Cheap after the first call.</summary>
    public static void EnsureInitialized() => _ = Initialized.Value;

    /// <summary>The ImageMagick version line, for reports and logs.</summary>
    public static string Version
    {
        get
        {
            EnsureInitialized();
            return $"{MagickNET.Version} (ImageMagick {MagickNET.ImageMagickVersion})";
        }
    }

    /// <summary>Q16-HDRI keeps 4 bytes per channel, so an RGBA pixel costs 16 bytes.</summary>
    const long BytesPerPixel = 16;

    /// <summary>Share of the computer's memory ImageMagick's pixel cache may use.</summary>
    const double MemoryShare = 0.6;

    static readonly long PhysicalMemory = ReadPhysicalMemory();

    /// <summary>
    /// The largest image (width × height) Clementine will decode. A pipeline holds two or three 16-byte-per-pixel
    /// copies: a 100 MP photo peaks around 1.8 GB. Beyond this, ImageMagick would spill tens of gigabytes into the
    /// temp folder (a few-KB "decompression bomb" PNG can claim 50 000 × 50 000), so such files are refused from
    /// their header with a friendly message (<see cref="ImageLoader.EnsureNotTooLarge"/>). The ImageMagick resource
    /// limits below back that up for whatever the header can't tell (PSD layers, animation frames, tool results).
    /// </summary>
    public static long MaxPixels { get; } = ComputeMaxPixels();

    /// <summary>Longest side ImageMagick may allocate (ResourceLimits.Width/Height): Photoshop's own PSB maximum.</summary>
    public const ulong MaxSide = 300_000;

    /// <summary>
    /// ImageMagick's pixel-cache memory (ResourceLimits.Memory): the share of RAM <see cref="MaxPixels"/> is sized
    /// for; null when RAM is unknown (ImageMagick's own default, half of it, stays).
    /// </summary>
    public static ulong? MemoryLimit { get; } = PhysicalMemory > 0 ? (ulong)(PhysicalMemory * MemoryShare) : null;

    /// <summary>
    /// How much pixel cache may spill into the temp folder (ResourceLimits.Disk): two full copies of the largest
    /// image allowed, for when memory is busy. Anything above <see cref="MaxPixels"/> (ResourceLimits.Area) never
    /// gets memory and needs disk, so a bomb the header check missed is refused before a byte is written.
    /// </summary>
    public static ulong DiskLimit { get; } = (ulong)MaxPixels * BytesPerPixel * 2;

    static long ReadPhysicalMemory()
    {
        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    static long ComputeMaxPixels()
    {
        const long BytesPerPixelAndCopies = BytesPerPixel * 3;
        var byMemory = PhysicalMemory > 0 ? (long)(PhysicalMemory * MemoryShare) / BytesPerPixelAndCopies : 0;
        return Math.Clamp(byMemory, 250_000_000, 1_000_000_000);
    }

    /// <summary>Whether this ImageMagick build lists a decoder for <paramref name="format"/>.</summary>
    public static bool CanRead(MagickFormat format)
    {
        EnsureInitialized();
        return MagickNET.SupportedFormats.Any(f => f.Format == format && f.SupportsReading);
    }

    /// <summary>
    /// Whether <paramref name="format"/> can actually be encoded and decoded again (probed once per process
    /// with a 16×16 image, a few milliseconds per format).
    /// </summary>
    public static bool CanEncode(MagickFormat format)
    {
        EnsureInitialized();
        return EncodeProbe.GetOrAdd(format, static f => ProbeEncode(f));
    }

    static bool ProbeEncode(MagickFormat format)
    {
        if (!MagickNET.SupportedFormats.Any(f => f.Format == format && f.SupportsWriting))
            return false;
        try
        {
            using var image = new MagickImage(new MagickColor("#FF8A1F"), 16, 16);
            var bytes = image.ToByteArray(format);
            if (bytes.Length == 0)
                return false;
            if (format == MagickFormat.Pdf)
                return bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8); // decoding PDF needs Ghostscript; we never read PDFs here
            using var back = new MagickImage(bytes, format);
            return back.Width == 16 && back.Height == 16;
        }
        catch (MagickException)
        {
            return false;
        }
    }

    static bool Initialize()
    {
        // While it starts up, ImageMagick creates an OpenCL kernel-cache folder (by default %LOCALAPPDATA%\ImageMagick).
        // Point it at Clementine's temp area before the native library loads, so nothing lands in the user's profile.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(OpenClCacheVariable)))
            Environment.SetEnvironmentVariable(OpenClCacheVariable, Path.Combine(AppPaths.TempRoot, "magick-opencl"));
        OpenCL.IsEnabled = false; // GPU kernels would be compiled and cached on first use; CPU is plenty for stills

        // Process-wide, so set here once (never per operation, which would race between parallel jobs).
        ResourceLimits.Width = MaxSide;
        ResourceLimits.Height = MaxSide;
        ResourceLimits.Area = (ulong)MaxPixels;
        if (MemoryLimit is { } memory)
            ResourceLimits.Memory = memory;
        ResourceLimits.Disk = DiskLimit;
        return true;
    }

    const string OpenClCacheVariable = "MAGICK_OPENCL_CACHE_DIR";
}
