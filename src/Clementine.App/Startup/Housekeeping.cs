using System.IO;
using Clementine.Core;

namespace Clementine.App.Startup;

/// <summary>What one housekeeping pass removed.</summary>
internal sealed record HousekeepingReport(int TempFolders, int LogFiles, long LogBytes);

/// <summary>
/// Tidies up after earlier runs, once the shell is ready and off the UI thread: job scratch folders left behind by a
/// crash or a forced exit (older than a day) and old logs (older than two weeks, and at most ~20 MB in all). Never
/// throws; anything in use is simply left for next time.
/// </summary>
internal static class Housekeeping
{
    public static readonly TimeSpan TempMaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan LogMaxAge = TimeSpan.FromDays(14);
    public const long LogBudgetBytes = 20L * 1024 * 1024;

    /// <summary>Caches that live in the temp root on purpose (Magick.NET's OpenCL kernels take a while to rebuild).</summary>
    static readonly string[] KeepFolders = ["magick-opencl"];

    /// <summary>Runs on a worker thread a few seconds after start-up, so it never competes with the first window.</summary>
    public static Task RunInBackground(ILog log, TimeSpan? delay = null) => Task.Run(async () =>
    {
        await Task.Delay(delay ?? TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var report = Run(AppPaths.TempRoot, AppPaths.LogDirectory, DateTime.UtcNow, log);
        if (report.TempFolders > 0 || report.LogFiles > 0)
            log.Info($"Housekeeping: removed {report.TempFolders} old temp folder(s) and {report.LogFiles} old log file(s) ({report.LogBytes / 1024} KB)");
    });

    /// <param name="currentLog">Today's log, never removed (default: the file <see cref="FileLog"/> writes to today).</param>
    /// <param name="logBudgetBytes">Total size the logs may keep (default <see cref="LogBudgetBytes"/>).</param>
    public static HousekeepingReport Run(string tempRoot, string logDirectory, DateTime nowUtc, ILog log, string? currentLog = null, long? logBudgetBytes = null)
    {
        var temp = 0;
        var logs = 0;
        long logBytes = 0;
        try
        {
            temp = CleanTemp(tempRoot, nowUtc, log);
        }
        catch (Exception ex)
        {
            log.Warn("Housekeeping: cleaning the temp folder failed", ex);
        }
        try
        {
            (logs, logBytes) = CleanLogs(logDirectory, nowUtc, currentLog ?? new FileLog(logDirectory).CurrentFile, logBudgetBytes ?? LogBudgetBytes, log);
        }
        catch (Exception ex)
        {
            log.Warn("Housekeeping: cleaning the logs failed", ex);
        }
        return new HousekeepingReport(temp, logs, logBytes);
    }

    static int CleanTemp(string tempRoot, DateTime nowUtc, ILog log)
    {
        if (!Directory.Exists(tempRoot))
            return 0;
        var removed = 0;
        foreach (var folder in new DirectoryInfo(tempRoot).EnumerateDirectories())
        {
            if (KeepFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase))
                continue;
            if (nowUtc - LastTouched(folder) < TempMaxAge)
                continue;
            try
            {
                // A junction or symbolic link is removed as a link; its target is never touched.
                if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    folder.Delete();
                else
                    folder.Delete(recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Info($"Housekeeping: left {folder.FullName} ({ex.Message})");
            }
        }
        return removed;
    }

    static (int Files, long Bytes) CleanLogs(string logDirectory, DateTime nowUtc, string currentLog, long budget, ILog log)
    {
        if (!Directory.Exists(logDirectory))
            return (0, 0);
        var files = new DirectoryInfo(logDirectory).EnumerateFiles("*.log")
            .Where(f => !string.Equals(f.FullName, Path.GetFullPath(currentLog), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        var removed = 0;
        long bytes = 0;
        // Today's log is never removed and counts against the budget first; then newest to oldest, and once the budget
        // is used up every older file goes.
        var kept = File.Exists(currentLog) ? new FileInfo(currentLog).Length : 0;
        var full = false;
        foreach (var file in files)
        {
            var tooOld = nowUtc - file.LastWriteTimeUtc > LogMaxAge;
            full |= kept + file.Length > budget;
            if (!tooOld && !full)
            {
                kept += file.Length;
                continue;
            }
            try
            {
                var length = file.Length;
                file.Delete();
                removed++;
                bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Info($"Housekeeping: left {file.FullName} ({ex.Message})");
                kept += file.Length;
            }
        }
        return (removed, bytes);
    }

    /// <summary>
    /// The newest of the folder's own times and those of what's directly inside: a job still writing its output keeps
    /// the file's write time fresh, and the creation time covers folders copied in with old dates.
    /// </summary>
    static DateTime LastTouched(DirectoryInfo folder)
    {
        var newest = Max(folder.LastWriteTimeUtc, folder.CreationTimeUtc);
        try
        {
            foreach (var entry in folder.EnumerateFileSystemInfos())
                newest = Max(newest, Max(entry.LastWriteTimeUtc, entry.CreationTimeUtc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MaxValue; // can't look inside: leave it alone
        }
        return newest;
    }

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
