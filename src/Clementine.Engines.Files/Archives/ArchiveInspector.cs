using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Clementine.Engines.Files.Archives;

/// <summary>Result of reading every byte of an archive without writing anything.</summary>
internal sealed record IntegrityReport(int Files, long Bytes, int Verified, IReadOnlyList<string> Problems)
{
    public bool IsOk => Problems.Count == 0;
}

/// <summary>"List contents" and "Test integrity".</summary>
internal static class ArchiveInspector
{
    const int MaxListedEntries = 5000;
    const int MaxReportedProblems = 20;

    /// <summary>A readable table of the archive's entries.</summary>
    public static string List(IArchiveReader reader, string archiveName, CancellationToken cancellationToken)
    {
        var entries = reader.TableOfContents?.ToList();
        if (entries is null)
        {
            entries = [];
            reader.ReadAll(_ => false, (entry, _) => entries.Add(entry), cancellationToken);
        }

        var files = entries.Where(e => e.Kind == ArchiveEntryKind.File).ToList();
        var folders = entries.Count(e => e.Kind == ArchiveEntryKind.Directory);
        var sizeKnown = files.All(f => f.Size is not null);
        // Sizes come from the archive and may be absurd (crafted Zip64 headers): never let a checked Sum overflow.
        var total = SaturatingSum(files.Select(f => f.Size ?? 0));
        var packed = SaturatingSum(entries.Select(e => e.PackedSize ?? 0));
        var encrypted = files.Count(f => f.IsEncrypted);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{archiveName} — {ArchiveFormats.Label(reader.Format)} archive, ");
        text.Append(Plural(files.Count, "file"));
        if (folders > 0)
            text.Append(", ").Append(Plural(folders, "folder"));
        if (sizeKnown && files.Count > 0)
            text.Append(", ").Append(ByteSize.Format(total));
        if (packed > 0 && sizeKnown)
            text.Append(" (").Append(ByteSize.Format(packed)).Append(" packed)");
        text.AppendLine();
        if (encrypted > 0)
            text.AppendLine(encrypted == files.Count ? "All files are password-protected." : $"{Plural(encrypted, "file")} password-protected.");
        text.AppendLine();
        text.AppendLine($"{"Size",10}  {"Modified",-16}  Name");

        foreach (var entry in entries.Take(MaxListedEntries))
        {
            var size = entry.Kind == ArchiveEntryKind.File ? entry.Size is { } s ? ByteSize.Format(s) : "?" : "";
            var modified = entry.Modified is { } m ? ToLocal(m).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
            var name = entry.Path + (entry.Kind == ArchiveEntryKind.Directory && !entry.Path.EndsWith('/') ? "/" : "");
            var suffix = entry.Kind switch
            {
                ArchiveEntryKind.SymbolicLink => $"  → {entry.LinkTarget ?? "?"} (link, not extracted)",
                ArchiveEntryKind.HardLink => $"  → {entry.LinkTarget ?? "?"} (hard link, not extracted)",
                ArchiveEntryKind.Special => "  (special entry, not extracted)",
                _ when entry.IsEncrypted => "  (encrypted)",
                _ => "",
            };
            text.AppendLine($"{size,10}  {modified,-16}  {name}{suffix}");
        }
        if (entries.Count > MaxListedEntries)
            text.AppendLine($"… and {entries.Count - MaxListedEntries:N0} more");
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Decompresses every file, verifying stored CRC-32s (ZIP, 7z, RAR) and the stream checksums built into
    /// gzip/xz/bzip2/zstd. Bounded by the same expansion limit as extraction.
    /// </summary>
    public static IntegrityReport Test(IArchiveReader reader, ExtractOptions options, ExtractionLimits limits, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var allowed = limits.AllowedOutput(options.ArchiveBytes);
        int files = 0, verified = 0;
        long total = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 17);
        ArchiveEntry? current = null;

        if (reader.TableOfContents is { } contents && string.IsNullOrEmpty(options.Password)
            && contents.Any(e => e.Kind == ArchiveEntryKind.File && e.IsEncrypted))
            throw ArchiveErrors.PasswordRequired(options.PasswordHint);
        var declared = SaturatingSum(reader.TableOfContents?.Select(e => e.Size ?? 0) ?? []);

        // "current" is the entry being decoded; errors between entries belong to no entry in particular.
        bool Want(ArchiveEntry entry)
        {
            current = entry;
            return entry.Kind == ArchiveEntryKind.File;
        }

        try
        {
            if (reader.IsStreamed)
            {
                // One broken block ruins everything after it in a stream: stop at the first problem.
                reader.ReadAll(Want, (entry, data) =>
                {
                    if (data is not null && Check(entry, data) is { } problem)
                        throw new IntegrityProblem(problem);
                    current = null;
                }, cancellationToken);
            }
            else
            {
                reader.ReadAll(Want, (entry, data) =>
                {
                    string? problem = null;
                    try
                    {
                        if (data is not null)
                            problem = Check(entry, data);
                    }
                    catch (Exception ex) when (ex is not OperationException and not OperationCanceledException && ArchiveErrors.IsReadFailure(ex, options.Password, entry))
                    {
                        problem = Describe(ex, entry);
                    }
                    if (problem is not null && problems.Count < MaxReportedProblems)
                        problems.Add(problem);
                    current = null;
                }, cancellationToken);
            }
        }
        catch (IntegrityProblem problem)
        {
            problems.Add(problem.Message);
        }
        catch (Exception ex) when (ex is not OperationException and not OperationCanceledException && ArchiveErrors.IsReadFailure(ex, options.Password, current))
        {
            problems.Add(Describe(ex, current));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new IntegrityReport(files, total, verified, problems);

        // A damage report line. Wrong passwords and unsupported methods aren't damage: they're thrown instead (the
        // latter so the caller can let 7-Zip try).
        string Describe(Exception ex, ArchiveEntry? entry)
        {
            var translated = ArchiveErrors.Translate(ex, options.Password, entry, options.PasswordHint, nameEntry: false);
            if (ex is NotSupportedException or NotImplementedException)
                throw translated;
            if (translated.Message.Contains("password", StringComparison.OrdinalIgnoreCase) && entry?.IsEncrypted != false)
                throw translated;
            var message = translated.Message.TrimEnd('.');
            return entry is null ? message : $"{entry.Path}: {message}";
        }

        string? Check(ArchiveEntry entry, Stream data)
        {
            if (entry.IsEncrypted && string.IsNullOrEmpty(options.Password))
                throw ArchiveErrors.PasswordRequired(options.PasswordHint);
            var crc = new Crc32();
            long length = 0;
            int read;
            while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                length += read;
                total += read;
                if (total > allowed)
                    throw ArchiveErrors.Bomb(allowed, options.ArchiveBytes);
                crc.Append(buffer.AsSpan(0, read));
            }
            files++;
            if (files % 32 == 0)
                progress?.Report(new JobProgress(declared > 0 ? Math.Min(1, (double)total / declared) : reader.Progress, "Testing…"));
            if (entry.Size is { } size && size != length)
                return entry.IsEncrypted
                    ? $"{entry.Path}: wrong password or damaged data"
                    : $"{entry.Path}: expected {ByteSize.Format(size)} but found {ByteSize.Format(length)}";
            if (entry.Crc32 is { } expected && length > 0)
            {
                if (crc.GetCurrentHashAsUInt32() != expected)
                    return entry.IsEncrypted ? $"{entry.Path}: wrong password or damaged data" : $"{entry.Path}: CRC check failed";
                verified++;
            }
            return null;
        }
    }

    static long SaturatingSum(IEnumerable<long> values)
    {
        long sum = 0;
        foreach (var value in values)
        {
            if (value > 0)
                sum = value > long.MaxValue - sum ? long.MaxValue : sum + value;
        }
        return sum;
    }

    static DateTime ToLocal(DateTime time) => time.Kind == DateTimeKind.Utc ? time.ToLocalTime() : time;

    static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";

    sealed class IntegrityProblem(string message) : Exception(message);
}
