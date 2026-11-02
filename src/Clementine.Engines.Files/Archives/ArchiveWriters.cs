using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Archives;

/// <summary>Archive formats Clementine writes.</summary>
internal enum OutputArchiveFormat
{
    Zip,
    Tar,
    TarGz,
    SevenZip,
}

/// <summary>Writes collected items as ZIP (System.IO.Compression), TAR / TAR.GZ (System.Formats.Tar, PAX) or 7z (7-Zip).</summary>
internal static partial class ArchiveWriters
{
    static readonly HashSet<string> AlreadyCompressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".cab", ".jar", ".apk", ".docx", ".xlsx", ".pptx",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma",
    };

    public static string Extension(OutputArchiveFormat format) => format switch
    {
        OutputArchiveFormat.Zip => ".zip",
        OutputArchiveFormat.Tar => ".tar",
        OutputArchiveFormat.TarGz => ".tar.gz",
        OutputArchiveFormat.SevenZip => ".7z",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string Label(OutputArchiveFormat format) => format switch
    {
        OutputArchiveFormat.Zip => "ZIP",
        OutputArchiveFormat.Tar => "TAR",
        OutputArchiveFormat.TarGz => "TAR.GZ",
        _ => "7Z",
    };

    /// <summary>Writes <paramref name="items"/> to <paramref name="destination"/> (which must not exist yet).</summary>
    /// <returns>Warnings worth showing (e.g. 7-Zip reported a non-fatal problem).</returns>
    public static async Task<IReadOnlyList<string>> WriteAsync(
        OutputArchiveFormat format,
        CollectedItems items,
        string destination,
        EngineContext context,
        string tempDirectory,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        switch (format)
        {
            case OutputArchiveFormat.Zip:
                WriteZip(items, destination, progress, cancellationToken);
                return [];
            case OutputArchiveFormat.Tar or OutputArchiveFormat.TarGz:
                var changed = WriteTar(items, destination, gzip: format == OutputArchiveFormat.TarGz, progress, cancellationToken);
                return changed.Count == 0
                    ? []
                    : [$"{Describe(changed)} changed while it was being compressed (a program is probably still writing it); the archive holds it as it was when Clementine started reading."];
            default:
                return await WriteSevenZipAsync(items, destination, context, tempDirectory, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    static void WriteZip(CollectedItems items, string destination, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var tracker = new CopyProgress(items.TotalBytes, items.FileCount, progress, "Compressing…");
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var item in items.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsDirectory)
            {
                var folder = zip.CreateEntry(item.EntryName, CompressionLevel.NoCompression);
                folder.LastWriteTime = DosTime(item.LastWriteTimeUtc);
                continue;
            }

            var level = AlreadyCompressed.Contains(Path.GetExtension(item.EntryName)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(item.EntryName, level);
            entry.LastWriteTime = DosTime(item.LastWriteTimeUtc);
            using (var input = OpenSource(item))
            using (var output = entry.Open())
                Copy(input, output, tracker, cancellationToken);
            tracker.FileDone();
        }
    }

    /// <returns>Entry names of files that grew or shrank while they were being read.</returns>
    static List<string> WriteTar(CollectedItems items, string destination, bool gzip, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var changed = new List<string>();
        const UnixFileMode FileMode644 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        const UnixFileMode DirectoryMode755 = FileMode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        var tracker = new CopyProgress(items.TotalBytes, items.FileCount, progress, "Compressing…");
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        using (var compressed = gzip ? new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true) : (Stream)new NonClosingStream(file))
        using (var tar = new TarWriter(compressed, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var item in items.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.IsDirectory)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, item.EntryName)
                    {
                        ModificationTime = item.LastWriteTimeUtc,
                        Mode = DirectoryMode755,
                    });
                    continue;
                }

                // TAR writes each size before the data: pin it, or a file still being written corrupts the archive.
                var fixedLength = new FixedLengthStream(OpenSource(item));
                using var input = new ReadCallbackStream(fixedLength, tracker.Add, cancellationToken);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.EntryName)
                {
                    ModificationTime = item.LastWriteTimeUtc,
                    Mode = FileMode644,
                    DataStream = input,
                });
                if (fixedLength.Changed)
                    changed.Add(item.EntryName);
                tracker.FileDone();
            }
        }
        return changed;
    }

    static string Describe(List<string> names) => names.Count == 1 ? $"\"{ArchiveErrors.Shorten(names[0])}\"" : $"{names.Count:N0} files";

    static async Task<IReadOnlyList<string>> WriteSevenZipAsync(
        CollectedItems items, string destination, EngineContext context, string tempDirectory, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sevenZip = context.Tools.SevenZip ?? throw new OperationException(SevenZipMissing);

        // Run 7-Zip from the folder the entry names are relative to, with an explicit list of files and empty
        // folders: it then stores exactly the paths (and skips exactly the links) our ZIP/TAR writers do.
        string workingDirectory;
        IEnumerable<string> listed;
        if (items.BaseDirectory is { } baseDirectory)
        {
            workingDirectory = baseDirectory;
            var nonEmpty = new HashSet<string>(
                items.Items.Select(i => ParentOf(i.EntryName)).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
            listed = items.Items
                .Where(i => !i.IsDirectory || !nonEmpty.Contains(i.EntryName.TrimEnd('/')))
                .Select(i => i.EntryName.TrimEnd('/').Replace('/', '\\'));
        }
        else
        {
            // Inputs from different folders: 7-Zip stores each top-level path under its own name.
            workingDirectory = tempDirectory;
            listed = items.Roots;
        }

        Directory.CreateDirectory(tempDirectory);
        var listFile = Path.Combine(tempDirectory, $"7z-list-{Guid.NewGuid():N}.txt");
        // Quoted: 7-Zip trims unquoted list-file lines, silently leaving out names that start with a space.
        // Windows names can't contain '"', so quoting is always safe.
        await File.WriteAllLinesAsync(listFile, listed.Select(name => $"\"{name}\""), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        progress.Report(new JobProgress(0, "Compressing with 7-Zip…"));
        var result = await ProcessRunner.RunAsync(
            sevenZip,
            ["a", "-t7z", "-mx=5", "-mmt=on", "-bsp1", "-bso0", "-bb0", "-sse", "-scsUTF-8", "-sccUTF-8", "-y", destination, "@" + listFile],
            new ProcessRunOptions
            {
                WorkingDirectory = workingDirectory,
                CaptureStdOut = false,
                OnStdOut = line =>
                {
                    var match = Percent().Match(line);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
                        progress.Report(new JobProgress(Math.Clamp(percent / 100.0, 0, 0.99), "Compressing with 7-Zip…"));
                },
            },
            cancellationToken).ConfigureAwait(false);

        return result.ExitCode switch
        {
            0 => [],
            1 => ["7-Zip reported a warning while compressing (a file may have changed while it was being read)."],
            8 => throw new OperationException("7-Zip ran out of memory while compressing.", result.StdErrTail),
            _ => throw new OperationException("7-Zip couldn't create the archive.", $"Exit code {result.ExitCode}\n{result.StdErrTail}"),
        };
    }

    public const string SevenZipMissing = "Writing .7z archives needs 7-Zip (free) — 7-zip.org";

    static string ParentOf(string entryName)
    {
        var trimmed = entryName.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? "" : trimmed[..slash];
    }

    static Stream OpenSource(ArchiveItem item)
    {
        try
        {
            // Share read/write/delete so files open in other apps (logs, projects) can still be archived.
            return new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never silently leave a file out of an archive someone may rely on as a backup: stop and say why.
            var name = Path.GetFileName(item.SourcePath);
            var message = ex switch
            {
                IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) } =>
                    $"\"{name}\" is open in another program that won't let other apps read it. Close it there and try again.",
                UnauthorizedAccessException => $"Windows denied access to \"{name}\", so it couldn't be compressed.",
                FileNotFoundException or DirectoryNotFoundException => $"\"{name}\" was moved or deleted while compressing.",
                _ => $"Couldn't read \"{name}\": {ex.Message}",
            };
            throw new OperationException(message, ex.ToString(), ex);
        }
    }

    static void Copy(Stream input, Stream output, CopyProgress tracker, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 17];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            tracker.Add(read);
        }
    }

    /// <summary>ZIP stores local DOS times between 1980 and 2107.</summary>
    static DateTimeOffset DosTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        if (local.Year < 1980)
            local = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        else if (local.Year > 2107)
            local = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex Percent();

    /// <summary>
    /// Progress shared across all files of one archive: every 4 MB, and at least every 200 ms while many small files
    /// go by (a folder of thousands of tiny files would otherwise sit at "Gathering files…" until it finished).
    /// </summary>
    sealed class CopyProgress(long totalBytes, int totalFiles, IProgress<JobProgress> progress, string status)
    {
        const long ReportBytes = 4 << 20;
        const long ReportMilliseconds = 200;
        long _done;
        long _lastReported;
        int _files;
        long _lastTick = Environment.TickCount64;

        public void Add(int bytes)
        {
            _done += bytes;
            if (_done - _lastReported >= ReportBytes)
                Report();
        }

        public void FileDone()
        {
            _files++;
            if (_files % 500 == 0 || Environment.TickCount64 - _lastTick >= ReportMilliseconds)
                Report();
        }

        void Report()
        {
            _lastReported = _done;
            _lastTick = Environment.TickCount64;
            double? fraction = totalBytes > 0 ? (double)_done / totalBytes : totalFiles > 0 ? (double)_files / totalFiles : null;
            progress.Report(new JobProgress(fraction is { } f ? Math.Min(0.99, f) : null, status));
        }
    }
}
