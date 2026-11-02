using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>A rectangle in physical screen pixels (right/bottom exclusive).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool Contains(ScreenPoint point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;
}

/// <summary>Where a wheel window goes on screen. Pure arithmetic in physical pixels.</summary>
public static class WheelPlacement
{
    /// <summary>
    /// Moves <paramref name="desired"/> just enough that a disc of <paramref name="radius"/> around it stays inside
    /// <paramref name="workArea"/>. If the work area is too small, the disc is centred in it.
    /// </summary>
    public static ScreenPoint ClampCentre(ScreenPoint desired, int radius, PixelRect workArea)
    {
        radius = Math.Max(0, radius);
        return new ScreenPoint(
            ClampAxis(desired.X, radius, workArea.Left, workArea.Right),
            ClampAxis(desired.Y, radius, workArea.Top, workArea.Bottom));
    }

    /// <summary>Top-left corner of a square window of <paramref name="size"/> pixels centred on <paramref name="centre"/>.</summary>
    public static ScreenPoint TopLeft(ScreenPoint centre, int size) => new(centre.X - size / 2, centre.Y - size / 2);

    static int ClampAxis(int value, int radius, int min, int max)
    {
        if (max - min < radius * 2)
            return min + (max - min) / 2;
        return Math.Clamp(value, min + radius, max - radius);
    }
}
