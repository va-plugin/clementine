using Clementine.Engines.Files.Archives;

namespace Clementine.Engines.Files.Operations;

/// <summary>Extract, List contents, Test integrity and repack (Convert) operations for archive inputs.</summary>
internal static class ArchiveOperations
{
    /// <summary>Every archive extension Clementine knows (".zip", ".tar.gz", ".7z", ".rar", ".gz" …).</summary>
    public static readonly Func<InputFile, bool> AnyArchive = Inputs.Extensions([.. FileTypes.Archive, ".tzst", ".tbz"]);

    public static IEnumerable<IOperation> Create(EngineContext context, ExtractionLimits limits)
    {
        yield return new ExtractOperation(withPassword: false, limits);
        yield return new ExtractOperation(withPassword: true, limits);
        yield return new ListOperation();
        yield return new TestOperation(withPassword: false, limits);
        yield return new TestOperation(withPassword: true, limits);

        yield return new RepackOperation(OutputArchiveFormat.Zip, context, limits, "archive.zip", 10, [".zip"]);
        yield return new RepackOperation(OutputArchiveFormat.TarGz, context, limits, "archive.tar-gz", 20, [".tar.gz", ".tgz"]);
        yield return new RepackOperation(OutputArchiveFormat.SevenZip, context, limits, "archive.7z", 30, [".7z"]);
        yield return new RepackOperation(OutputArchiveFormat.Tar, context, limits, "archive.tar", 40, [".tar"]);
    }

    static OptionSchema PasswordSchema(string confirm) => new("Archive password",
    [
        new TextField("password", "Password", "") { IsPassword = true, Placeholder = "Password" },
    ])
    {
        Description = "The password the archive was protected with.",
        ConfirmLabel = confirm,
    };

    static string? Password(OperationRequest request, bool required)
    {
        if (!required)
            return null;
        var password = request.Options.GetStringOrNull("password");
        if (string.IsNullOrEmpty(password))
            throw new OperationException("Enter the archive's password.");
        return password;
    }

    /// <summary>Extract (to a folder named after the archive, without double nesting).</summary>
    sealed class ExtractOperation(bool withPassword, ExtractionLimits limits) : FileOperation(
        withPassword
            ? Infos.Tool("archive.extract.password", "Extract", "Extract with password…", "Extract a password-protected archive.", "unlock", null, 11, "With password…").Heavy()
            : Infos.Tool("archive.extract", "Extract", "Extract here", "Extract into a folder next to the archive — a single top-level folder isn't nested twice.", "extract", null, 10, "Extract here").Heavy(),
        AnyArchive,
        withPassword ? PasswordSchema("Extract") : null)
    {
        public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var input = request.Input;
            var password = Password(request, withPassword);
            progress.Report(JobProgress.Indeterminate("Opening archive…"));

            var directory = request.Output.ResolveDirectory(input);
            using var staging = ExtractedOutput.BeginStaging(request.Output, directory);
            var options = new ExtractOptions(input.Length, password, ZoneIdentifier.Read(input.Path));
            var summary = await ArchiveExtraction.ExtractAsync(request.Context, input.Path, staging.WorkingPath, options, limits, progress, cancellationToken)
                .ConfigureAwait(false);

            if (summary.Files + summary.Directories == 0)
            {
                throw new OperationException(summary.SkippedLinks + summary.SkippedSpecial > 0
                    ? "This archive only contains links or special entries, which Clementine doesn't extract."
                    : "This archive is empty.");
            }

            progress.Report(new JobProgress(1, "Finishing…"));
            var final = ExtractedOutput.Commit(staging, request.Output, directory, FolderName(input));
            return new OperationResult { Outputs = [final], Warnings = summary.Warnings() };
        }

        static string FolderName(InputFile input) => string.IsNullOrWhiteSpace(input.BaseName) ? "Extracted" : input.BaseName;
    }

    /// <summary>List contents (text result).</summary>
    sealed class ListOperation() : FileOperation(
        Infos.Tool("archive.list", "List contents", "List contents", "Show the files inside the archive without extracting it.", "info", null, 20),
        AnyArchive)
    {
        protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            progress.Report(JobProgress.Indeterminate("Reading archive…"));
            const string hint = "Extract it with Extract › With password… to see its files.";
            using var reader = ArchiveReaders.Open(request.Input.Path, null, hint);
            try
            {
                var text = ArchiveInspector.List(reader, request.Input.Name, cancellationToken);
                return OperationResult.FromText($"Contents of {request.Input.Name}", text);
            }
            catch (Exception ex) when (ex is not OperationException and not OperationCanceledException && ArchiveErrors.IsReadFailure(ex))
            {
                throw ArchiveErrors.Translate(ex, null, null, hint);
            }
        }
    }

    /// <summary>Test integrity: decompress everything and verify checksums, writing nothing.</summary>
    sealed class TestOperation(bool withPassword, ExtractionLimits limits) : FileOperation(
        withPassword
            ? Infos.Tool("archive.test.password", "Test", "Test with password…", "Check a password-protected archive for damage.", "unlock", null, 31, "With password…").Heavy()
            : Infos.Tool("archive.test", "Test", "Test integrity", "Check every file in the archive for damage without extracting.", "info", null, 30, "Test integrity").Heavy(),
        AnyArchive,
        withPassword ? PasswordSchema("Test") : null)
    {
        public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var password = Password(request, withPassword);
            progress.Report(JobProgress.Indeterminate("Testing…"));
            var options = new ExtractOptions(request.Input.Length, password, PasswordHint: ArchiveErrors.TestHint);
            var text = await ArchiveExtraction.TestAsync(request.Context, request.Input, options, limits, progress, cancellationToken).ConfigureAwait(false);
            return OperationResult.FromText("Archive OK", text);
        }
    }

    /// <summary>Convert one archive format into another (extract to a private temp folder, then re-compress).</summary>
    sealed class RepackOperation : FileOperation
    {
        readonly OutputArchiveFormat _format;
        readonly EngineContext _context;
        readonly ExtractionLimits _limits;

        public RepackOperation(OutputArchiveFormat format, EngineContext context, ExtractionLimits limits, string id, int order, string[] sameFormat)
            : base(
                Infos.Convert(id, ArchiveWriters.Label(format), $"Convert to {ArchiveWriters.Label(format)}", Describe(format), "archive", ArchiveWriters.Extension(format), order, "Repack").Heavy(),
                file => AnyArchive(file) && !sameFormat.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
        {
            _format = format;
            _context = context;
            _limits = limits;
        }

        static string Describe(OutputArchiveFormat format) => format switch
        {
            OutputArchiveFormat.Zip => "Repack as ZIP — opens anywhere, including Windows and macOS without extra software.",
            OutputArchiveFormat.TarGz => "Repack as a gzip-compressed TAR, the usual format on Linux and macOS.",
            OutputArchiveFormat.SevenZip => "Repack as 7z for the smallest file (uses 7-Zip).",
            _ => "Repack as an uncompressed TAR.",
        };

        public override Availability CheckAvailability() =>
            _format == OutputArchiveFormat.SevenZip && _context.Tools.SevenZip is null
                ? Availability.Unavailable(ArchiveWriters.SevenZipMissing)
                : Availability.Available;

        public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var input = request.Input;
            var stage = Path.Combine(request.TempDirectory, "contents");
            progress.Report(JobProgress.Indeterminate("Reading archive…"));
            var summary = await ArchiveExtraction.ExtractAsync(
                request.Context, input.Path, stage, new ExtractOptions(input.Length, PasswordHint: ArchiveErrors.RepackHint), _limits,
                new ScaledProgress(progress, 0, 0.5), cancellationToken).ConfigureAwait(false);

            var items = FileCollector.CollectContents(stage, cancellationToken);
            if (items.Items.Count == 0)
                throw new OperationException("This archive is empty.");

            using var output = request.Output.BeginFile(input, ArchiveWriters.Extension(_format));
            var writeWarnings = await Task.Run(
                () => ArchiveWriters.WriteAsync(_format, items, output.WorkingPath, request.Context, request.TempDirectory, new ScaledProgress(progress, 0.5, 1), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            // A repacked download is still a download: without its mark, Explorer would extract the new archive's
            // files without SmartScreen or Protected View (the classic Mark-of-the-Web bypass).
            ZoneIdentifier.Write(output.WorkingPath, ZoneIdentifier.Read(input.Path));
            return new OperationResult
            {
                Outputs = [output.Commit()],
                Warnings = [.. summary.Warnings(), .. writeWarnings],
            };
        }
    }
}
