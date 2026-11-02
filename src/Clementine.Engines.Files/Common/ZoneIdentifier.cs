namespace Clementine.Engines.Files.Common;

/// <summary>
/// The Windows "Mark of the Web" (the Zone.Identifier NTFS stream a browser attaches to downloads). Explorer copies
/// it from an archive to everything extracted, so SmartScreen and Office Protected View keep working; we do the same.
/// </summary>
internal static class ZoneIdentifier
{
    const string StreamSuffix = ":Zone.Identifier";

    /// <summary>The archive's mark, if it has one (null on FAT drives, network shares without streams, etc.).</summary>
    public static string? Read(string path)
    {
        try
        {
            var stream = path + StreamSuffix;
            if (!File.Exists(stream))
                return null;
            var text = File.ReadAllText(stream);
            return text.Contains("ZoneId=", StringComparison.OrdinalIgnoreCase) && text.Length < 64 * 1024 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Best effort: a missing mark must never fail an extraction.</summary>
    public static void Write(string path, string? zoneIdentifier)
    {
        if (zoneIdentifier is null)
            return;
        try
        {
            File.WriteAllText(path + StreamSuffix, zoneIdentifier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }
    }
}
