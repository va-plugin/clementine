using System.Globalization;
using Clementine.Core;
using Clementine.Engines.Media.Encoders;
using Clementine.Engines.Media.FFmpeg;

namespace Clementine.Engines.Media.Video;

/// <summary>Where a crop window sits inside the frame.</summary>
public enum CropAnchor
{
    Center,
    Top,
    Bottom,
    Left,
    Right,
}

/// <summary>Size and timing arithmetic for the video tools (pure, unit-tested).</summary>
public static class VideoGeometry
{
    /// <summary>The upright, square-pixel size the user sees.</summary>
    public static FrameSize DisplaySize(VideoStreamInfo video) => new(video.DisplayWidth, video.DisplayHeight);

    /// <summary>Scales so the short side is <paramref name="shortSide"/> ("1080p" for landscape and portrait alike).</summary>
    public static FrameSize ShortSide(FrameSize size, int shortSide)
    {
        if (size.Width >= size.Height)
            return new FrameSize(Even(size.Width * (double)shortSide / size.Height), Even(shortSide));
        return new FrameSize(Even(shortSide), Even(size.Height * (double)shortSide / size.Width));
    }

    /// <summary>Like <see cref="ShortSide"/> but never enlarges.</summary>
    public static FrameSize CapShortSide(FrameSize size, int maxShortSide) =>
        Math.Min(size.Width, size.Height) <= maxShortSide ? size : ShortSide(size, maxShortSide);

    public static FrameSize Percent(FrameSize size, double percent) =>
        new(Even(size.Width * percent / 100), Even(size.Height * percent / 100));

    /// <summary>Fits inside width × height keeping the aspect ratio; a zero side means "follow the aspect".</summary>
    public static FrameSize Fit(FrameSize size, int width, int height)
    {
        if (width <= 0 && height <= 0)
            throw new OperationException("Enter a width, a height, or both.");
        if (width <= 0)
            return new FrameSize(Even(size.Width * (double)height / size.Height), Even(height));
        if (height <= 0)
            return new FrameSize(Even(width), Even(size.Height * (double)width / size.Width));
        var scale = Math.Min((double)width / size.Width, (double)height / size.Height);
        return new FrameSize(Even(size.Width * scale), Even(size.Height * scale));
    }

    /// <summary>The largest window of <paramref name="aspect"/> inside the frame, placed by <paramref name="anchor"/>.</summary>
    public static (FrameSize Size, int X, int Y) CropToAspect(FrameSize frame, double aspect, CropAnchor anchor)
    {
        int width, height;
        if (frame.Aspect > aspect)
        {
            height = frame.Height;
            width = Math.Min(frame.Width, Even(frame.Height * aspect));
        }
        else
        {
            width = frame.Width;
            height = Math.Min(frame.Height, Even(frame.Width / aspect));
        }
        return Place(frame, new FrameSize(width & ~1, height & ~1), anchor);
    }

    /// <summary>A fixed-size window (clamped to the frame), placed by <paramref name="anchor"/>.</summary>
    public static (FrameSize Size, int X, int Y) CropToSize(FrameSize frame, int width, int height, CropAnchor anchor)
    {
        if (width < 2 || height < 2)
            throw new OperationException("The crop size must be at least 2 × 2 pixels.");
        return Place(frame, new FrameSize(Math.Min(frame.Width, width) & ~1, Math.Min(frame.Height, height) & ~1), anchor);
    }

    /// <summary>atempo stages for a speed factor (each stage must stay within 0.5–2 for best quality).</summary>
    public static IReadOnlyList<string> AtempoChain(double factor)
    {
        if (!(factor > 0))
            throw new OperationException("The speed must be greater than zero.");
        var stages = new List<string>();
        var remaining = factor;
        while (remaining > 2)
        {
            stages.Add("atempo=2");
            remaining /= 2;
        }
        while (remaining < 0.5)
        {
            stages.Add("atempo=0.5");
            remaining /= 0.5;
        }
        if (Math.Abs(remaining - 1) > 1e-9 || stages.Count == 0)
            stages.Add($"atempo={remaining.ToString("0.######", CultureInfo.InvariantCulture)}");
        return stages;
    }

    /// <summary>"0.5x", "2x", "1.25x".</summary>
    public static string SpeedLabel(double factor) => factor.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    public static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2, MidpointRounding.AwayFromZero) * 2);

    static (FrameSize Size, int X, int Y) Place(FrameSize frame, FrameSize crop, CropAnchor anchor)
    {
        var centerX = (frame.Width - crop.Width) / 2;
        var centerY = (frame.Height - crop.Height) / 2;
        return anchor switch
        {
            CropAnchor.Top => (crop, centerX, 0),
            CropAnchor.Bottom => (crop, centerX, frame.Height - crop.Height),
            CropAnchor.Left => (crop, 0, centerY),
            CropAnchor.Right => (crop, frame.Width - crop.Width, centerY),
            _ => (crop, centerX, centerY),
        };
    }
}
