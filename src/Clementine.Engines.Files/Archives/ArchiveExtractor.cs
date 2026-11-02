using System.Buffers;
using System.IO.Hashing;

namespace Clementine.Engines.Files.Archives;

/// <summary>What an extraction wrote and skipped.</summary>
internal sealed class ExtractionSummary
{
    public int Files { get; set; }

    public int Directories { get; set; }

    public long Bytes { get; set; }

    public int SkippedLinks { get; set; }

    public int SkippedSpecial { get; set; }

    public int SkippedLongPaths { get; set; }

    public int MadeRelative { get; set; }

    public int Renamed { get; set; }

    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();
        if (SkippedLinks > 0)
            warnings.Add($"{Count(SkippedLinks, "link")} skipped — links inside archives can point anywhere on your PC.");
        if (SkippedSpecial > 0)
            warnings.Add($"{Count(SkippedSpecial, "special entry", "special entries")} (devices, pipes or sparse files) skipped.");
        if (SkippedLongPaths > 0)
            warnings.Add($"{Count(SkippedLongPaths, "file")} skipped because the path is too long for Windows.");
        if (MadeRelative > 0)
            warnings.Add($"{Count(MadeRelative, "entry", "entries")} had absolute paths and were extracted inside the folder instead.");
        if (Renamed > 0)
            warnings.Add($"{Count(Renamed, "name")} changed to suit Windows (reserved names, invalid characters or names that differ only in case).");
        return warnings;
    }

    static string Count(int n, string singular, string? plural = null) => n == 1 ? $"1 {singular}" : $"{n:N0} {plural ?? singular + "s"}";
}

/// <summary>Per-run inputs to <see cref="ArchiveExtractor.Extract"/>.</summary>
/// <param name="ArchiveBytes">Size of the archive on disk (all parts), for the expansion-ratio guard.</param>
/// <param name="Password">Password for encrypted entries, or null.</param>
/// <param name="ZoneIdentifier">The archive's Mark of the Web, copied to every extracted file.</param>
/// <param name="PasswordHint">What to tell the user when a password is needed ("Use Extract › With password…").</param>
internal sealed record ExtractOptions(long ArchiveBytes, string? Password = null, string? ZoneIdentifier = null, string PasswordHint = ArchiveErrors.ExtractHint);

/// <summary>
/// Writes an archive's files into a destination folder, defensively: no path ever leaves the folder (zip-slip),
/// links and devices are never created, nothing is overwritten, expansion and entry counts are bounded, free disk
/// space is watched, and every stored CRC-32 is verified.
/// </summary>
internal sealed class ArchiveExtractor(ExtractionLimits limits)
{
    const int ErrorFilenameTooLong = unchecked((int)0x800700CE);
    const int ErrorPathNotFound = unchecked((int)0x80070003);

    public ExtractionSummary Extract(IArchiveReader reader, string destination, ExtractOptions options, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var summary = new ExtractionSummary();
        var layout = new ExtractionLayout(destination);
        var allowed = limits.AllowedOutput(options.ArchiveBytes);
        var space = new SpaceGuard(destination, limits.FreeSpaceReserveBytes);

        long declared = 0;
        if (reader.TableOfContents is { } contents)
            declared = PreCheck(contents, allowed, options, space);

        var buffer = ArrayPool<byte>.Shared.Rent(1 << 17);
        long total = 0, entries = 0, sinceReport = 0;
        ArchiveEntry? current = null;
        try
        {
            // "current" is the entry being decoded, for error messages; failures between entries name none.
            reader.ReadAll(entry =>
            {
                current = entry;
                return entry.Kind == ArchiveEntryKind.File;
            }, (entry, data) =>
            {
                Visit(entry, data);
                current = null;
            }, cancellationToken);
        }
        catch (UnsafeEntryPathException ex)
        {
            throw ArchiveErrors.UnsafePath(ex.EntryPath);
        }
        catch (Exception ex) when (ex is not OperationException and not OperationCanceledException && ArchiveErrors.IsReadFailure(ex, options.Password, current))
        {
            throw ArchiveErrors.Translate(ex, options.Password, current, options.PasswordHint);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        summary.Renamed += layout.Renamed;
        return summary;

        void Visit(ArchiveEntry entry, Stream? data)
        {
            if (++entries > limits.MaxEntries)
                throw ArchiveErrors.TooManyEntries(limits.MaxEntries);

            switch (entry.Kind)
            {
                case ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink:
                    summary.SkippedLinks++;
                    return;
                case ArchiveEntryKind.Special:
                    summary.SkippedSpecial++;
                    return;
            }

            var path = EntryPathSanitizer.Sanitize(entry.Path);
            if (path.WasAbsolute)
                summary.MadeRelative++;
            if (path.WasRenamed)
                summary.Renamed++;
            if (path.IsRoot)
                return;

            if (entry.Kind == ArchiveEntryKind.Directory)
            {
                var directory = layout.ResolveDirectory(path.Segments);
                EnsureInside(destination, directory, entry);
                if (!TryCreateDirectory(directory, summary))
                    return;
                summary.Directories++;
                return;
            }

            if (entry.IsEncrypted && string.IsNullOrEmpty(options.Password))
                throw ArchiveErrors.PasswordRequired(options.PasswordHint);

            var target = layout.ResolveFile(path.Segments);
            EnsureInside(destination, target, entry);
            var written = WriteFile(entry, data ?? Stream.Null, target, buffer, summary, read =>
            {
                total += read;
                if (total > allowed)
                    throw ArchiveErrors.Bomb(allowed, options.ArchiveBytes);
                space.Track(read);
                sinceReport += read;
                if (sinceReport >= 8 << 20)
                {
                    sinceReport = 0;
                    progress?.Report(new JobProgress(Fraction(), "Extracting…"));
                }
            }, cancellationToken);
            if (written < 0)
                return; // skipped (path too long)

            ZoneIdentifier.Write(target, options.ZoneIdentifier);
            summary.Files++;
            summary.Bytes += written;
            if (entries % 64 == 0)
                progress?.Report(new JobProgress(Fraction(), "Extracting…"));
        }

        // Bytes written against the declared total where the archive has a table of contents (its readers seek around,
        // so file position means little); otherwise how much of the compressed stream has been consumed.
        double Fraction() => declared > 0 ? Math.Min(1, (double)total / declared) : reader.Progress;
    }

    /// <summary>Checks the table of contents up front; returns the declared uncompressed total.</summary>
    long PreCheck(IReadOnlyList<ArchiveEntry> contents, long allowed, ExtractOptions options, SpaceGuard space)
    {
        if (contents.Count > limits.MaxEntries)
            throw ArchiveErrors.TooManyEntries(limits.MaxEntries);

        long declared = 0;
        foreach (var entry in contents)
        {
            if (entry.Kind != ArchiveEntryKind.File)
                continue;
            declared = SaturatingAdd(declared, entry.Size ?? 0);
            if (entry.IsEncrypted && string.IsNullOrEmpty(options.Password))
                throw ArchiveErrors.PasswordRequired(options.PasswordHint);
        }
        if (declared > allowed)
            throw ArchiveErrors.Bomb(allowed, options.ArchiveBytes);
        space.EnsureRoomFor(declared);
        return declared;
    }

    /// <summary>Writes one entry; returns bytes written, or -1 when Windows couldn't create the path.</summary>
    static long WriteFile(ArchiveEntry entry, Stream data, string target, byte[] buffer, ExtractionSummary summary, Action<int> onChunk, CancellationToken cancellationToken)
    {
        FileStream output;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        }
        catch (Exception ex) when (IsPathTooLong(ex))
        {
            summary.SkippedLongPaths++;
            return -1;
        }

        long written = 0;
        var crc = new Crc32();
        using (output)
        {
            int read;
            while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += read;
                if (entry.Size is { } declared && written > declared)
                    throw ArchiveErrors.SizeMismatch(entry.Path, entry.IsEncrypted);
                onChunk(read);
                crc.Append(buffer.AsSpan(0, read));
                output.Write(buffer, 0, read);
            }
        }

        if (entry.Crc32 is { } expected && written > 0 && crc.GetCurrentHashAsUInt32() != expected)
            throw ArchiveErrors.CrcMismatch(entry.Path, entry.IsEncrypted);
        SetModified(target, entry.Modified);
        return written;
    }

    static bool TryCreateDirectory(string path, ExtractionSummary summary)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (IsPathTooLong(ex))
        {
            summary.SkippedLongPaths++;
            return false;
        }
    }

    static void EnsureInside(string root, string path, ArchiveEntry entry)
    {
        // Defence in depth: the sanitizer already rejects "..", but never trust a single check.
        if (!EntryPathSanitizer.IsInside(root, path))
            throw new UnsafeEntryPathException(entry.Path);
    }

    static bool IsPathTooLong(Exception ex) =>
        ex is PathTooLongException || (ex is IOException io && io.HResult is ErrorFilenameTooLong or ErrorPathNotFound && io is not FileNotFoundException);

    internal static void SetModified(string path, DateTime? modified)
    {
        if (modified is not { } time || time.Year < 1980 || time.Year > 2200)
            return;
        try
        {
            if (time.Kind == DateTimeKind.Utc)
                File.SetLastWriteTimeUtc(path, time);
            else
                File.SetLastWriteTime(path, time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    static long SaturatingAdd(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + Math.Max(0, b);

    /// <summary>Stops before the destination drive fills up.</summary>
    sealed class SpaceGuard
    {
        const long CheckEvery = 256L * 1024 * 1024;
        readonly DriveInfo? _drive;
        readonly long _threshold;
        readonly long _initialFree;
        long _sinceCheck;

        public SpaceGuard(string directory, long reserve)
        {
            try
            {
                _drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
                _initialFree = _drive.AvailableFreeSpace;
                // Never demand more headroom than a quarter of what was free when we started.
                _threshold = Math.Min(reserve, _initialFree / 4);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                _drive = null;
            }
        }

        public void EnsureRoomFor(long bytes)
        {
            if (_drive is not null && bytes > _initialFree - _threshold)
                throw ArchiveErrors.NotEnoughSpace(bytes, _initialFree);
        }

        public void Track(long bytes)
        {
            if (_drive is null)
                return;
            _sinceCheck += bytes;
            if (_sinceCheck < CheckEvery)
                return;
            _sinceCheck = 0;
            try
            {
                if (_drive.AvailableFreeSpace < _threshold)
                    throw ArchiveErrors.RunningOutOfSpace();
            }
            catch (IOException)
            {
            }
        }
    }
}
