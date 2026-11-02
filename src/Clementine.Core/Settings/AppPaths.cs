namespace Clementine.Core;

/// <summary>
/// Well-known folders. Set the CLEMENTINE_DATA_DIR environment variable to redirect settings and logs
/// (tests do this so they never touch the real profile).
/// </summary>
public static class AppPaths
{
    static string? DataOverride => Environment.GetEnvironmentVariable("CLEMENTINE_DATA_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>%APPDATA%\Clementine — settings and remembered state.</summary>
    public static string DataDirectory => DataOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clementine");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>%LOCALAPPDATA%\Clementine\logs.</summary>
    public static string LogDirectory => DataOverride is { } dir
        ? Path.Combine(dir, "logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clementine", "logs");

    /// <summary>%TEMP%\Clementine — per-job scratch folders live below this.</summary>
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "Clementine");

    /// <summary>Where outputs go when the preferred folder isn't writable (Documents\Clementine).</summary>
    public static string FallbackOutputDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clementine");
}
