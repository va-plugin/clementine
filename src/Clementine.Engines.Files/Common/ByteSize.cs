using System.Globalization;

namespace Clementine.Engines.Files.Common;

/// <summary>Human-readable byte counts using Windows conventions (1 KB = 1024 bytes).</summary>
internal static class ByteSize
{
    static readonly string[] Units = ["KB", "MB", "GB", "TB", "PB", "EB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
            return "?";
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes";

        double value = bytes;
        var unit = -1;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
