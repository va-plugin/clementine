using System.Diagnostics;
using System.IO;
using Clementine.Core;

namespace Clementine.App.Preferences;

/// <summary>An external program Clementine uses, and whether this machine has it.</summary>
/// <param name="IsBundled">Found in the tools folder next to Clementine.exe (release builds bundle FFmpeg and 7-Zip).</param>
/// <param name="IsOptional">Clementine works without it; only some conversions need it (LibreOffice).</param>
/// <param name="MissingNote">What the Tools page says when it isn't found.</param>
public sealed record ToolInfo(string Key, string Name, string Purpose, string? Path, string? Version, string DownloadUrl, bool CanOverride,
    bool IsBundled = false, bool IsOptional = false, string? MissingNote = null)
{
    public bool IsInstalled => Path is not null;
}

/// <summary>How the Tools page shows a tool.</summary>
public enum ToolState
{
    /// <summary>The copy bundled with Clementine (tools folder): "Built in · FFmpeg 8.1.2".</summary>
    BuiltIn,

    /// <summary>Installed on this PC (or chosen by the user): its path.</summary>
    Installed,

    /// <summary>An optional tool that isn't installed: a neutral note, nothing is wrong.</summary>
    NotInstalledOptional,

    /// <summary>A tool Clementine needs and couldn't find.</summary>
    Missing,
}

/// <summary>Finds FFmpeg, LibreOffice, 7-Zip and Edge and reads their versions (never downloads anything).</summary>
public static class ToolStatus
{
    public const string FfmpegKey = "ffmpeg";
    public const string LibreOfficeKey = "libreoffice";
    public const string SevenZipKey = "7zip";
    public const string EdgeKey = "edge";

    /// <summary>Where "Get FFmpeg" sends the user (Windows builds; nothing is downloaded automatically).</summary>
    public const string FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/";

    /// <summary>True when FFmpeg or ffprobe can't be found — the same test the media engine uses to mark its operations unavailable.</summary>
    public static bool IsFfmpegMissing(ToolLocator tools) => tools.Ffmpeg is null || tools.Ffprobe is null;

    /// <summary>Whether an operation is unavailable because FFmpeg is missing (its reason names FFmpeg).</summary>
    public static bool NeedsFfmpeg(Availability availability) =>
        !availability.IsAvailable && availability.Reason?.Contains("FFmpeg", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The program file the "Choose…" dialog asks for.</summary>
    public static string ExecutableName(string key) => key switch
    {
        FfmpegKey => "ffmpeg.exe",
        LibreOfficeKey => "soffice.exe",
        SevenZipKey => "7z.exe",
        _ => "msedge.exe",
    };

    /// <summary>Asks where <paramref name="key"/>'s program is; returns the chosen path, or null when cancelled.</summary>
    public static string? ChooseExecutable(System.Windows.Window? owner, string key)
    {
        var exe = ExecutableName(key);
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Where is {exe}?", Filter = $"{exe}|{exe}|Programs (*.exe)|*.exe", FileName = exe };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    /// <summary>What the Tools page says about LibreOffice when it isn't installed.</summary>
    public const string LibreOfficeMissingNote = "Optional — for PowerPoint and older Office files";

    /// <param name="appDirectory">Clementine's folder (default: this app's); tools found in its tools subfolder are the bundled ones.</param>
    public static async Task<IReadOnlyList<ToolInfo>> DetectAsync(ToolLocator tools, CancellationToken cancellationToken = default, string? appDirectory = null)
    {
        tools.Refresh();
        var bundledRoot = BundledToolsDirectory(appDirectory);
        var ffmpeg = tools.Ffmpeg;
        var ffmpegVersion = ffmpeg is null ? null : await FfmpegVersionAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        var sevenZip = tools.SevenZip;
        return
        [
            new ToolInfo(FfmpegKey, "FFmpeg", "Video and audio: every conversion and tool", ffmpeg, ffmpegVersion,
                FfmpegDownloadUrl, CanOverride: true, IsBundled: IsUnder(ffmpeg, bundledRoot)),
            new ToolInfo(LibreOfficeKey, "LibreOffice", "PowerPoint → PDF and images, .doc/.xls/.ppt and OpenDocument files", tools.LibreOffice, FileVersion(tools.LibreOffice),
                "https://www.libreoffice.org/download/download-libreoffice/", CanOverride: true, IsOptional: true, MissingNote: LibreOfficeMissingNote),
            new ToolInfo(SevenZipKey, "7-Zip", "Creating .7z archives", sevenZip, FileVersion(sevenZip),
                "https://www.7-zip.org/download.html", CanOverride: true, IsBundled: IsUnder(sevenZip, bundledRoot)),
            new ToolInfo(EdgeKey, "Microsoft Edge", "HTML, Markdown, e-books and Word/Excel → PDF (part of Windows)", tools.Edge, FileVersion(tools.Edge),
                "https://www.microsoft.com/edge", CanOverride: false),
        ];
    }

    /// <summary>&lt;app&gt;\tools — where release builds bundle FFmpeg (tools\ffmpeg\bin) and 7-Zip (tools\7zip).</summary>
    public static string BundledToolsDirectory(string? appDirectory = null) =>
        System.IO.Path.Combine(appDirectory ?? AppContext.BaseDirectory, "tools");

    /// <summary>Whether <paramref name="path"/> lies inside <paramref name="directory"/> (full paths, case-insensitive).</summary>
    public static bool IsUnder(string? path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory)) + System.IO.Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return false;
        }
    }

    public static ToolState StateOf(ToolInfo tool) =>
        tool.IsInstalled
            ? tool.IsBundled ? ToolState.BuiltIn : ToolState.Installed
            : tool.IsOptional ? ToolState.NotInstalledOptional : ToolState.Missing;

    /// <summary>The Tools page's status line: "Built in · FFmpeg 8.1.2", the path, the optional note or "Not found on this PC".</summary>
    public static string StatusLine(ToolInfo tool) => StateOf(tool) switch
    {
        ToolState.BuiltIn => tool.Version is { Length: > 0 } version ? $"Built in · {tool.Name} {version}" : "Built in",
        ToolState.Installed => tool.Path!,
        ToolState.NotInstalledOptional => tool.MissingNote ?? "Optional",
        _ => tool.MissingNote ?? "Not found on this PC",
    };

    /// <summary>"8.1.2" from "ffmpeg version 8.1.2-full_build-www.gyan.dev Copyright …".</summary>
    public static string? ParseFfmpegVersion(string? firstLine)
    {
        if (string.IsNullOrWhiteSpace(firstLine))
            return null;
        const string marker = "version ";
        var start = firstLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        var rest = firstLine[(start + marker.Length)..];
        var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null)
            return null;
        // Release builds look like "8.1.2-full_build-…"; git builds like "N-118448-g…" or "2025-01-20-git-…".
        var dash = token.IndexOf('-');
        return dash > 0 && char.IsDigit(token[0]) && token[..dash].Contains('.') ? token[..dash] : token;
    }

    static async Task<string?> FfmpegVersionAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-version"],
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(8) }, cancellationToken).ConfigureAwait(false);
            return ParseFfmpegVersion(result.StdOut.Split('\n').FirstOrDefault());
        }
        catch (OperationException)
        {
            return null;
        }
    }

    static string? FileVersion(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var version = info.ProductVersion ?? info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Split(' ')[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
