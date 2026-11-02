using System.Collections.Concurrent;
using Microsoft.Win32;

namespace Clementine.Core;

public enum OfficeApp
{
    Word,
    Excel,
    PowerPoint,
}

/// <summary>
/// Finds external programs. Lookups are cached; call <see cref="Refresh"/> after settings change.
/// Search order for FFmpeg: settings override → &lt;app&gt;\tools\ffmpeg\ → PATH → winget/scoop/choco shims.
/// </summary>
public sealed class ToolLocator
{
    readonly Func<ClementineSettings> _settings;
    readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ToolLocator(Func<ClementineSettings> settings) => _settings = settings;

    public string? Ffmpeg => Cached("ffmpeg", () => FindFfmpegFamily("ffmpeg.exe", _settings().FfmpegPath));

    /// <summary>ffprobe next to the located ffmpeg first, so both always come from the same build.</summary>
    public string? Ffprobe => Cached("ffprobe", () =>
    {
        if (Ffmpeg is { } ffmpeg && Path.GetDirectoryName(ffmpeg) is { } dir && File.Exists(Path.Combine(dir, "ffprobe.exe")))
            return Path.Combine(dir, "ffprobe.exe");
        return FindFfmpegFamily("ffprobe.exe", null);
    });

    /// <summary>LibreOffice's soffice.exe (document conversions), if installed.</summary>
    public string? LibreOffice => Cached("soffice", () =>
        FirstExisting(
            FromOverride(_settings().LibreOfficePath, "soffice.exe"),
            ProgramFiles(@"LibreOffice\program\soffice.exe"),
            ProgramFilesX86(@"LibreOffice\program\soffice.exe"),
            FindOnPath("soffice.exe")));

    /// <summary>
    /// 7-Zip (writing .7z archives and a fallback extractor): the standalone 7za.exe bundled with release builds in
    /// &lt;app&gt;\tools\7zip\ first, then an installed 7-Zip. Both take the same command line.
    /// </summary>
    public string? SevenZip => Cached("7z", () =>
        FirstExisting(
            FromOverride(_settings().SevenZipPath, "7z.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7za.exe"),
            ProgramFiles(@"7-Zip\7z.exe"),
            ProgramFilesX86(@"7-Zip\7z.exe"),
            FindOnPath("7z.exe"),
            FindOnPath("7za.exe")));

    /// <summary>Microsoft Edge (headless HTML → PDF printing). Present on every Windows 11 machine.</summary>
    public string? Edge => Cached("msedge", () =>
        FirstExisting(
            ProgramFilesX86(@"Microsoft\Edge\Application\msedge.exe"),
            ProgramFiles(@"Microsoft\Edge\Application\msedge.exe"),
            AppPathsRegistry("msedge.exe")));

    /// <summary>Whether a desktop Office app is registered for COM automation.</summary>
    public bool IsOfficeInstalled(OfficeApp app) => Cached($"office:{app}", () =>
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{app}.Application\CLSID");
        return key is null ? null : "yes";
    }) is not null;

    public void Refresh() => _cache.Clear();

    public string RequireFfmpeg() => Ffmpeg ?? throw new OperationException(
        "FFmpeg isn't installed. Install it (for example: winget install Gyan.FFmpeg) or set its location in Settings → Tools.");

    public string RequireFfprobe() => Ffprobe ?? throw new OperationException(
        "ffprobe (part of FFmpeg) isn't installed. Install FFmpeg or set its location in Settings → Tools.");

    public string RequireLibreOffice() => LibreOffice ?? throw new OperationException(
        "This conversion needs LibreOffice (free). Install it from libreoffice.org, then try again.");

    /// <summary>Full path of <paramref name="exeName"/> on PATH, or null.</summary>
    public static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    string? Cached(string key, Func<string?> find) => _cache.GetOrAdd(key, _ => find());

    static string? FindFfmpegFamily(string exeName, string? overridePath)
    {
        var appDir = AppContext.BaseDirectory;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return FirstExisting(
            FromOverride(overridePath, exeName),
            Path.Combine(appDir, "tools", "ffmpeg", "bin", exeName),
            Path.Combine(appDir, "tools", "ffmpeg", exeName),
            Path.Combine(appDir, "tools", exeName),
            FindOnPath(exeName),
            Path.Combine(local, "Microsoft", "WinGet", "Links", exeName),
            Path.Combine(profile, "scoop", "shims", exeName),
            Path.Combine(programData, "chocolatey", "bin", exeName),
            Path.Combine(@"C:\ffmpeg\bin", exeName));
    }

    static string? FromOverride(string? overridePath, string exeName)
    {
        if (string.IsNullOrWhiteSpace(overridePath))
            return null;
        var trimmed = overridePath.Trim().Trim('"');
        if (Directory.Exists(trimmed))
        {
            var inDir = Path.Combine(trimmed, exeName);
            var inBin = Path.Combine(trimmed, "bin", exeName);
            return File.Exists(inDir) ? inDir : inBin;
        }
        if (!File.Exists(trimmed))
            return null;
        // A file override names one tool; for its siblings (ffprobe next to ffmpeg) look in the same folder.
        return string.Equals(Path.GetFileName(trimmed), exeName, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(Path.GetDirectoryName(trimmed)!, exeName);
    }

    static string? ProgramFiles(string relative) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), relative);

    static string? ProgramFilesX86(string relative) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), relative);

    static string? AppPathsRegistry(string exeName)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            if (key?.GetValue(null) is string path)
                return path.Trim('"');
        }
        return null;
    }

    static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
}
