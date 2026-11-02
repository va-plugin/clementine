using System.Globalization;
using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Subtitles;

/// <summary>Parsing and formatting of subtitle timestamps in each format's style.</summary>
internal static partial class SubtitleTime
{
    /// <summary>
    /// Lenient parse of "hh:mm:ss,fff", "hh:mm:ss.fff", "mm:ss.fff", "h:mm:ss.cc", "hh:mm:ss:fff", "0:0:1,5" …
    /// The fraction is decimal ("1,5" is 1.5 s, "1.05" is 1.05 s).
    /// </summary>
    public static bool TryParse(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var match = Timestamp().Match(text.Trim());
        if (!match.Success)
            return false;
        long hours = match.Groups["h"].Success ? long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (minutes > 59 && match.Groups["h"].Success || seconds > 59 || hours > 9999)
            return false;
        var fraction = match.Groups["f"].Success ? match.Groups["f"].Value : "0";
        var milliseconds = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        time = new TimeSpan(0, (int)hours, minutes, seconds, milliseconds);
        return true;
    }

    /// <summary>SRT: "00:01:02,345".</summary>
    public static string Srt(TimeSpan time) => Format(time, ',', 3, padHours: true);

    /// <summary>WebVTT: "00:01:02.345".</summary>
    public static string Vtt(TimeSpan time) => Format(time, '.', 3, padHours: true);

    /// <summary>SBV: "0:01:02.345".</summary>
    public static string Sbv(TimeSpan time) => Format(time, '.', 3, padHours: false);

    /// <summary>ASS/SSA: "0:01:02.35" (centiseconds).</summary>
    public static string Ass(TimeSpan time) => Format(time, '.', 2, padHours: false);

    static string Format(TimeSpan time, char separator, int digits, bool padHours)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        var unit = digits == 2 ? 10 : 1;
        var totalUnits = (long)Math.Round(time.TotalMilliseconds / unit, MidpointRounding.AwayFromZero);
        var fraction = totalUnits % (digits == 2 ? 100 : 1000);
        var totalSeconds = totalUnits / (digits == 2 ? 100 : 1000);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        var h = padHours ? hours.ToString("00", CultureInfo.InvariantCulture) : hours.ToString(CultureInfo.InvariantCulture);
        return $"{h}:{minutes:00}:{seconds:00}{separator}{fraction.ToString(digits == 2 ? "00" : "000", CultureInfo.InvariantCulture)}";
    }

    [GeneratedRegex(@"^(?:(?<h>\d{1,4}):)?(?<m>\d{1,2}):(?<s>\d{1,2})(?:[,.:](?<f>\d{1,3})\d*)?$")]
    private static partial Regex Timestamp();
}
