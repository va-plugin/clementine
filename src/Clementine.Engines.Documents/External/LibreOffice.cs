namespace Clementine.Engines.Documents.External;

/// <summary>An export target for <see cref="LibreOffice.ConvertAsync"/>: the extension and the "--convert-to" filter spec.</summary>
internal sealed record LibreOfficeTarget(string Extension, string Filter)
{
    public static readonly LibreOfficeTarget Pdf = new(".pdf", "pdf");
    public static readonly LibreOfficeTarget Docx = new(".docx", "docx:MS Word 2007 XML");
    public static readonly LibreOfficeTarget Doc = new(".doc", "doc:MS Word 97");
    public static readonly LibreOfficeTarget Odt = new(".odt", "odt:writer8");
    public static readonly LibreOfficeTarget Rtf = new(".rtf", "rtf:Rich Text Format");
    public static readonly LibreOfficeTarget Html = new(".html", "html:XHTML Writer File:UTF8");
    public static readonly LibreOfficeTarget Txt = new(".txt", "txt:Text (encoded):UTF8");
    public static readonly LibreOfficeTarget Xlsx = new(".xlsx", "xlsx:Calc MS Excel 2007 XML");
    public static readonly LibreOfficeTarget Ods = new(".ods", "ods:calc8");
    public static readonly LibreOfficeTarget Pptx = new(".pptx", "pptx:Impress MS PowerPoint 2007 XML");
    public static readonly LibreOfficeTarget Odp = new(".odp", "odp:impress8");
}

/// <summary>
/// Headless LibreOffice conversions. Runs with a private user profile (so it never collides with a LibreOffice the user
/// has open), one conversion at a time (across Clementine processes too), on a private copy of the input (an Office
/// Open XML copy without its links to outside resources, see <see cref="OpenXml.ExternalLinks"/>), with a 3-minute
/// timeout. Cancelling or timing out kills LibreOffice's whole process tree (see <see cref="ProcessRunner"/>).
/// </summary>
internal sealed class LibreOffice(ToolLocator tools, ILog log, bool enabled = true)
{
    public const string MissingReason = "Needs LibreOffice (free, optional) for PowerPoint and older Office files — libreoffice.org";

    static readonly SemaphoreSlim Gate = new(1, 1);

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// %LOCALAPPDATA%\Clementine\lo-profile, or below CLEMENTINE_DATA_DIR when that is set (tests, portable runs), so
    /// test runs never touch the real profile.
    /// </summary>
    public static string ProfileDirectory =>
        ProfileOverride ?? (Environment.GetEnvironmentVariable("CLEMENTINE_DATA_DIR") is { Length: > 0 } data
            ? Path.Combine(data, "lo-profile")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clementine", "lo-profile"));

    /// <summary>Tests point this at a fresh folder to exercise first-run profile creation.</summary>
    internal static string? ProfileOverride { get; set; }

    /// <summary>
    /// LibreOffice is installed (and not switched off for this provider: tests force Clementine's built-in Word and
    /// Excel converters this way on machines that have LibreOffice).
    /// </summary>
    public bool IsInstalled => enabled && tools.LibreOffice is not null;

    public Availability Availability => IsInstalled ? Availability.Available : Availability.Unavailable(MissingReason);

    /// <summary>
    /// Converts <paramref name="inputPath"/> and returns the path of the result inside <paramref name="workDirectory"/>.
    /// The caller copies it to its <see cref="PendingOutput"/>. On failure or cancellation nothing is left behind; on
    /// success only the result remains (the private copy of the input is deleted straight away).
    /// </summary>
    public async Task<string> ConvertAsync(
        string inputPath, LibreOfficeTarget target, string workDirectory, IProgress<JobProgress> progress, CancellationToken cancellationToken,
        string? inputFilter = null)
    {
        var soffice = tools.RequireLibreOffice();
        // soffice.com is the console launcher: it waits for the conversion and relays LibreOffice's messages.
        var console = Path.Combine(Path.GetDirectoryName(soffice)!, "soffice.com");
        var executable = File.Exists(console) ? console : soffice;

        // A headless LibreOffice can't ask for a password: say so straight away.
        Office.ProtectedDocuments.ThrowIfProtected(inputPath);

        // Work on a private copy with a plain ASCII name: LibreOffice never sees (or locks) the user's file, and its
        // output name is predictable.
        var job = Path.Combine(workDirectory, "lo-" + Guid.NewGuid().ToString("N")[..8]);
        var inDirectory = Path.Combine(job, "in");
        var outDirectory = Path.Combine(job, "out");
        var spareProfile = Path.Combine(job, "profile");
        var succeeded = false;
        try
        {
            Directory.CreateDirectory(inDirectory);
            Directory.CreateDirectory(outDirectory);
            var source = Path.Combine(inDirectory, "document" + Path.GetExtension(inputPath).ToLowerInvariant());
            try
            {
                File.Copy(inputPath, source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OperationException($"Couldn't read \"{Path.GetFileName(inputPath)}\" — it may be open in another program.", ex.Message, ex);
            }

            // LibreOffice would load linked pictures (and templates, linked objects…) from the web or network shares
            // while converting. The private copy loses those links first, so nothing is ever fetched.
            try
            {
                var removed = OpenXml.ExternalLinks.Strip(source, cancellationToken);
                if (removed > 0)
                    log.Info($"Removed {removed} external link(s) from the copy of {Path.GetFileName(inputPath)} given to LibreOffice.");
            }
            catch (InvalidDataException ex)
            {
                throw new OperationException("LibreOffice couldn't open this file — it may be password-protected or damaged.", ex.Message, ex);
            }

            progress.Report(JobProgress.Indeterminate("Waiting for LibreOffice…"));
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var output = Path.Combine(outDirectory, "document" + target.Extension);
                ProcessResult result;
                // Another Clementine process (the CLI beside the tray app) may be using the shared profile: take turns.
                using (await ProfileLock.AcquireAsync(ProfileDirectory, cancellationToken).ConfigureAwait(false))
                {
                    progress.Report(JobProgress.Indeterminate("Converting with LibreOffice…"));
                    Directory.CreateDirectory(ProfileDirectory);
                    result = await RunAsync(executable, ProfileDirectory, source, target, outDirectory, job, inputFilter, cancellationToken).ConfigureAwait(false);
                }

                if (!HasOutput(output) && !IsDocumentProblem(result))
                {
                    // Nothing came out and LibreOffice didn't blame the document: the shared profile is held by a
                    // LibreOffice we didn't start, or is damaged. Try once more with a throw-away profile.
                    log.Warn($"LibreOffice produced nothing with the shared profile (exit {result.ExitCode}); retrying with a private one. {result.StdOut} {result.StdErrTail}".Trim());
                    Directory.CreateDirectory(spareProfile);
                    result = await RunAsync(executable, spareProfile, source, target, outDirectory, job, inputFilter, cancellationToken).ConfigureAwait(false);
                }

                if (!HasOutput(output))
                {
                    var details = $"exit {result.ExitCode}\n{result.StdOut}\n{result.StdErrTail}".Trim();
                    log.Warn($"LibreOffice produced no output for {Path.GetFileName(inputPath)} → {target.Extension}: {details}");
                    throw new OperationException(FriendlyFailure(result, target), details);
                }
                succeeded = true;
                return output;
            }
            finally
            {
                Gate.Release();
            }
        }
        finally
        {
            // Never leave a copy of the user's document (or a half-written result) in the temp folder.
            if (succeeded)
            {
                await DeleteQuietlyAsync(inDirectory).ConfigureAwait(false);
                await DeleteQuietlyAsync(spareProfile).ConfigureAwait(false);
            }
            else
            {
                await DeleteQuietlyAsync(job).ConfigureAwait(false);
            }
        }
    }

    static async Task<ProcessResult> RunAsync(
        string executable, string profile, string source, LibreOfficeTarget target, string outDirectory, string job, string? inputFilter,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--headless", "--norestore", "--nologo", "--nodefault", "--nolockcheck",
            $"-env:UserInstallation={FileUris.For(profile)}",
        };
        if (inputFilter is not null)
            arguments.Add($"--infilter={inputFilter}");
        arguments.AddRange(["--convert-to", target.Filter, "--outdir", outDirectory, source]);

        try
        {
            return await ProcessRunner.RunAsync(executable, arguments, new ProcessRunOptions
            {
                WorkingDirectory = job,
                Timeout = Timeout,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationException ex) when (ex.Message.Contains("too long", StringComparison.Ordinal))
        {
            throw new OperationException($"LibreOffice took longer than {Timeout.TotalMinutes:0} minutes and was stopped. The file may be very large or damaged.", ex.Details, ex);
        }
    }

    static bool HasOutput(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>LibreOffice ran and said this document can't be loaded or saved that way (another try won't help).</summary>
    static bool IsDocumentProblem(ProcessResult result)
    {
        var text = result.StdOut + "\n" + result.StdErrTail;
        return text.Contains("source file could not be loaded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no export filter", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>LibreOffice can hold files for a moment after it is killed; retry briefly, then leave it to the job's temp cleanup.</summary>
    static async Task DeleteQuietlyAsync(string directory)
    {
        for (var attempt = 0; attempt < 15 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    static string FriendlyFailure(ProcessResult result, LibreOfficeTarget target)
    {
        var text = result.StdOut + "\n" + result.StdErrTail;
        if (text.Contains("source file could not be loaded", StringComparison.OrdinalIgnoreCase))
            return "LibreOffice couldn't open this file — it may be password-protected or damaged.";
        if (text.Contains("no export filter", StringComparison.OrdinalIgnoreCase))
            return $"LibreOffice can't save this kind of file as {target.Extension.TrimStart('.').ToUpperInvariant()}.";
        return "LibreOffice couldn't convert this file. It may be damaged, password-protected or in an unsupported format.";
    }

    /// <summary>
    /// A lock file beside the shared profile, held for the whole conversion, so two Clementine processes never run
    /// LibreOffice on the same profile at once. The OS releases it if the process dies.
    /// </summary>
    sealed class ProfileLock(FileStream stream) : IDisposable
    {
        public static async Task<ProfileLock> AcquireAsync(string profile, CancellationToken cancellationToken)
        {
            var path = Path.TrimEndingDirectorySeparator(profile) + ".clementine-lock";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            while (true)
            {
                try
                {
                    return new ProfileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public void Dispose() => stream.Dispose();
    }
}
