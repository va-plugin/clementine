using System.Globalization;

namespace Clementine.Engines.Media.FFmpeg;

/// <summary>An exact fraction as FFmpeg reports frame rates, time bases and aspect ratios ("30000/1001", "16:9").</summary>
public readonly record struct Rational(long Numerator, long Denominator)
{
    public static readonly Rational Zero = new(0, 1);

    /// <summary>True when the fraction is positive and finite.</summary>
    public bool IsValid => Numerator > 0 && Denominator > 0;

    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;

    /// <summary>Parses "30000/1001", "16:9", "25" or "29.97". Returns <see cref="Zero"/> for junk or "0/0".</summary>
    public static Rational Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Zero;
        var separator = text.IndexOfAny(['/', ':']);
        if (separator > 0)
        {
            return long.TryParse(text[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                && long.TryParse(text[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
                && d != 0
                    ? new Rational(n, d)
                    : Zero;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? FromDouble(value) : Zero;
    }

    /// <summary>Approximates a decimal rate, recognising the NTSC family (29.97 → 30000/1001).</summary>
    public static Rational FromDouble(double value)
    {
        if (!(value > 0) || double.IsInfinity(value))
            return Zero;
        foreach (var ntsc in new[] { 24, 30, 48, 60, 120 })
        {
            var rate = ntsc * 1000.0 / 1001;
            if (Math.Abs(value - rate) < 0.005)
                return new Rational(ntsc * 1000, 1001);
        }
        if (Math.Abs(value - Math.Round(value)) < 0.0005)
            return new Rational((long)Math.Round(value), 1);
        return new Rational((long)Math.Round(value * 1000), 1000).Reduce();
    }

    public Rational Reduce()
    {
        if (!IsValid)
            return this;
        var gcd = Gcd(Numerator, Denominator);
        return new Rational(Numerator / gcd, Denominator / gcd);
    }

    /// <summary>FFmpeg command-line form ("30000/1001", "25").</summary>
    public override string ToString() =>
        Denominator == 1 ? Numerator.ToString(CultureInfo.InvariantCulture) : $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";

    static long Gcd(long a, long b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return Math.Abs(a);
    }
}
