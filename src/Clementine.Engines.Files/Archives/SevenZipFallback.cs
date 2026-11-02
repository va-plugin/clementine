using System.Globalization;
using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Archives;

/// <summary>
/// When SharpCompress can't decode a ZIP/7z/RAR compression method (e.g. Deflate64 inside 7z, filters added by newer
/// 7-Zip versions), 7-Zip — if installed — does the decoding. The same safety rules apply: the listing is checked
/// first (no "..", no absolute paths, declared size within the expansion limit, enough disk space), and afterwards
/// every link is removed, the real size is re-checked and the Mark of the Web is copied.
/// </summary>
/// <remarks>
/// <para>
/// Passwords: 7-Zip takes a password from its console prompt or as "-p&lt;password&gt;" on its command line. Clementine
/// must use the command line because <see cref="ProcessRunner"/> (Core) closes 7-Zip's stdin, and a process's command
/// line can be read by every other process of the same user (Task Manager, WMI, Process Explorer) while 7-Zip runs.
/// The password is never logged; the bare "-p" used when there is none gives 7-Zip an empty password, so it never
/// prompts (and with stdin closed it couldn't wait for one anyway). To keep the exposure as small as possible:
/// </para>
/// <list type="bullet">
/// <item>Encrypted archives are always decoded by SharpCompress (in process, password never leaves Clementine)
/// when it can; this class runs only after SharpCompress reported a compression method it can't decode
/// (<see cref="CanHelp"/>). Wrong passwords are reported by SharpCompress itself (see
/// <see cref="ArchiveErrors.IsGarbledDecryption"/>), not retried here.</item>
/// <item>The password is handed over only when the archive needs it (<see cref="PasswordFor"/>).</item>
/// </list>
/// <para>
/// Residual risk: an encrypted archive that also uses a method SharpCompress can't decode (for example AES ZIP with
/// LZMA entries, or Deflate64 inside an encrypted 7z) exposes its password to same-user processes for as long as
/// 7-Zip runs (a listing, then the extraction or test). Avoiding that would need the password written to 7-Zip's
/// prompt through stdin, which ProcessRunner doesn't offer.
/// </para>
/// </remarks>
internal static partial class SevenZipFallback
{
    /// <summary>True when <paramref name="error"/> means "unsupported method" and 7-Zip can try instead.</summary>
    public static bool CanHelp(Exception error, ArchiveFormat format, EngineContext context) =>
        context.Tools.SevenZip is not null
        && format is ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Rar
        && (error is NotSupportedException or NotImplementedException
            || error.InnerException is NotSupportedException or NotImplementedException);

    /// <summary>
    /// The password 7-Zip should get. SharpCompress has already read this archive's headers before it gave up on a
    /// compression method, so it can tell whether anything is encrypted: when the archive opens without a password
    /// and lists no encrypted entry, the password the user typed isn't needed and stays off 7-Zip's command line.
    /// Encrypted headers (7z "-mhe=on") or headers SharpCompress can't read keep the password.
    /// </summary>
    internal static string? PasswordFor(string archive, string? password)
    {
        if (string.IsNullOrEmpty(password))
            return null;
        try
        {
            using var reader = ArchiveReaders.Open(archive);
            return reader.TableOfContents is { } contents && !contents.Any(e => e.IsEncrypted) ? null : password;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return password; // can't tell without it (encrypted headers, unreadable): 7-Zip needs it
        }
    }

    public static async Task<ExtractionSummary> ExtractAsync(
        EngineContext context, string archive, string destination, ExtractOptions options, ExtractionLimits limits, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var sevenZip = context.Tools.SevenZip!;
        var password = PasswordFor(archive, options.Password);
        await PreCheckAsync(sevenZip, archive, password, options, limits, cancellationToken).ConfigureAwait(false);

        // Start from an empty folder: the first attempt may have written a few files before giving up.
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);

        progress.Report(new JobProgress(0, "Extracting with 7-Zip…"));
        var result = await ProcessRunner.RunAsync(
            sevenZip,
            ExtractArguments(destination, password, archive),
            new ProcessRunOptions { CaptureStdOut = false, OnStdOut = line => ReportPercent(line, progress, "Extracting with 7-Zip…") },
            cancellationToken).ConfigureAwait(false);
        ThrowOnFailure(result, options);

        return Secure(destination, options, limits);
    }

    /// <summary>
    /// "-snl- -snh-": 7-Zip would otherwise create the archive's symbolic and hard links (Developer Mode allows it
    /// without admin rights) and then write later entries <em>through</em> them — the root of 7-Zip's link traversal
    /// CVEs. Without them, link entries are not created at all; <see cref="Secure"/> stays as a second line of defence.
    /// </summary>
    internal static IReadOnlyList<string> ExtractArguments(string destination, string? password, string archive) =>
        // "-aou": names that collide on Windows (case, 8.3 aliases) get renamed instead of overwriting each other.
        ["x", "-y", "-aou", "-snl-", "-snh-", "-bsp1", "-bso0", "-sccUTF-8", "-o" + destination, "-p" + (password ?? ""), "--", archive];

    /// <summary>Runs "7z t" and returns the number of files 7-Zip verified.</summary>
    public static async Task TestAsync(EngineContext context, string archive, ExtractOptions options, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new JobProgress(0, "Testing with 7-Zip…"));
        var result = await ProcessRunner.RunAsync(
            context.Tools.SevenZip!,
            ["t", "-bsp1", "-bso0", "-sccUTF-8", "-p" + (PasswordFor(archive, options.Password) ?? ""), "--", archive],
            new ProcessRunOptions { CaptureStdOut = false, OnStdOut = line => ReportPercent(line, progress, "Testing with 7-Zip…") },
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !LooksLikePasswordProblem(result.StdErrTail))
            throw new OperationException("The archive is damaged: 7-Zip found errors while testing it.", result.StdErrTail);
        ThrowOnFailure(result, options);
    }

    static async Task PreCheckAsync(string sevenZip, string archive, string? password, ExtractOptions options, ExtractionLimits limits, CancellationToken cancellationToken)
    {
        var listing = await ProcessRunner.RunAsync(
            sevenZip, ["l", "-slt", "-ba", "-sccUTF-8", "-p" + (password ?? ""), "--", archive], null, cancellationToken).ConfigureAwait(false);
        ThrowOnFailure(listing, options);

        long declared = 0, entries = 0;
        foreach (Match match in PathLine().Matches(listing.StdOut))
        {
            entries++;
            EntryPathSanitizer.Sanitize(match.Groups[1].Value.Trim()); // throws on ".."
        }
        foreach (Match match in SizeLine().Matches(listing.StdOut))
        {
            if (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                declared += size;
        }
        if (entries > limits.MaxEntries)
            throw ArchiveErrors.TooManyEntries(limits.MaxEntries);
        var allowed = limits.AllowedOutput(options.ArchiveBytes);
        if (declared > allowed)
            throw ArchiveErrors.Bomb(allowed, options.ArchiveBytes);
    }

    /// <summary>Removes links, re-checks the size actually written and copies the Mark of the Web.</summary>
    static ExtractionSummary Secure(string destination, ExtractOptions options, ExtractionLimits limits)
    {
        var summary = new ExtractionSummary();
        var allowed = limits.AllowedOutput(options.ArchiveBytes);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(destination));
        while (pending.Count > 0)
        {
            foreach (var item in pending.Pop().EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = false }))
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (item is DirectoryInfo link)
                        link.Delete();
                    else
                        item.Delete();
                    summary.SkippedLinks++;
                }
                else if (item is DirectoryInfo folder)
                {
                    summary.Directories++;
                    pending.Push(folder);
                }
                else if (item is FileInfo file)
                {
                    summary.Files++;
                    summary.Bytes += file.Length;
                    if (summary.Bytes > allowed)
                        throw ArchiveErrors.Bomb(allowed, options.ArchiveBytes);
                    ZoneIdentifier.Write(file.FullName, options.ZoneIdentifier);
                }
            }
        }
        return summary;
    }

    static void ThrowOnFailure(ProcessResult result, ExtractOptions options)
    {
        if (result.ExitCode == 0)
            return;
        if (LooksLikePasswordProblem(result.StdErrTail))
        {
            throw string.IsNullOrEmpty(options.Password)
                ? ArchiveErrors.PasswordRequired(options.PasswordHint)
                : new OperationException("That password isn't right for this archive.", result.StdErrTail);
        }
        throw new OperationException("7-Zip couldn't extract this archive — it may be damaged.", $"Exit code {result.ExitCode}\n{result.StdErrTail}");
    }

    static bool LooksLikePasswordProblem(string stderr) =>
        stderr.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("encrypted", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("password", StringComparison.OrdinalIgnoreCase);

    static void ReportPercent(string line, IProgress<JobProgress> progress, string status)
    {
        var match = Percent().Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
            progress.Report(new JobProgress(Math.Clamp(percent / 100.0, 0, 0.99), status));
    }

    [GeneratedRegex(@"^Path = (.+)$", RegexOptions.Multiline)]
    private static partial Regex PathLine();

    [GeneratedRegex(@"^Size = (\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex SizeLine();

    [GeneratedRegex(@"^\s*(\d{1,3})%")]
    private static partial Regex Percent();
}
