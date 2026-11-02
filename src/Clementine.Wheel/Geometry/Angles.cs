using System.Windows;

namespace Clementine.Wheel;

/// <summary>
/// Polar helpers in the wheel's convention: angles in degrees, 0 = 12 o'clock, increasing clockwise on screen
/// (y grows downwards), so 90 = 3 o'clock.
/// </summary>
public static class Angles
{
    /// <summary>Maps any angle into [0, 360).</summary>
    public static double Normalize(double degrees)
    {
        var angle = degrees % 360;
        if (angle < 0)
            angle += 360;
        return angle >= 360 ? 0 : angle;
    }

    /// <summary>The angle of an offset from the wheel centre.</summary>
    public static double Of(Vector offset) => Normalize(Math.Atan2(offset.X, -offset.Y) * 180 / Math.PI);

    /// <summary>Unit vector pointing at <paramref name="degrees"/>.</summary>
    public static Vector Direction(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Vector(Math.Sin(radians), -Math.Cos(radians));
    }

    /// <summary>The point at <paramref name="radius"/> from <paramref name="centre"/> in direction <paramref name="degrees"/>.</summary>
    public static Point PointAt(Point centre, double degrees, double radius) => centre + Direction(degrees) * radius;

    public static double ToRadians(double degrees) => degrees * Math.PI / 180;

    public static double ToDegrees(double radians) => radians * 180 / Math.PI;
}

/// <summary>A clockwise arc of the wheel: <see cref="Start"/> in degrees (see <see cref="Angles"/>) and its <see cref="Sweep"/>.</summary>
public readonly record struct AngleRange(double Start, double Sweep)
{
    public double End => Start + Sweep;

    /// <summary>Angle halfway through the range, normalised.</summary>
    public double Mid => Angles.Normalize(Start + Sweep / 2);

    public bool IsFullCircle => Sweep >= 360 - 1e-9;

    /// <summary>Whether <paramref name="angle"/> lies in [Start, Start + Sweep).</summary>
    public bool Contains(double angle) => IsFullCircle || Angles.Normalize(angle - Start) < Sweep;
}
