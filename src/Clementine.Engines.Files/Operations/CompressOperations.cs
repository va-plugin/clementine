using Clementine.Engines.Files.Archives;

namespace Clementine.Engines.Files.Operations;

/// <summary>Folder → ZIP / TAR.GZ / 7Z conversions and the "Archive" action for any selection.</summary>
internal static class CompressOperations
{
    public static IEnumerable<IOperation> Create(EngineContext context)
    {
        yield return new FolderOperation(OutputArchiveFormat.Zip, context, "folder.zip", 10,
            "Compress the folder into a ZIP — opens anywhere without extra software.");
        yield return new FolderOperation(OutputArchiveFormat.TarGz, context, "folder.tar-gz", 20,
            "Compress the folder into a gzip TAR, the usual format on Linux and macOS.");
        yield return new FolderOperation(OutputArchiveFormat.SevenZip, context, "folder.7z", 30,
            "Compress the folder into a 7z archive — usually the smallest (uses 7-Zip).");

        yield return new CompressAction(OutputArchiveFormat.Zip, context, "files.compress.zip", "ZIP", 10,
            "Put everything into one ZIP archive.");
        yield return new CompressEachAction();
        yield return new CompressAction(OutputArchiveFormat.TarGz, context, "files.compress.tar-gz", "TAR.GZ", 12,
            "Put everything into one gzip-compressed TAR.");
        yield return new CompressAction(OutputArchiveFormat.SevenZip, context, "files.compress.7z", "7Z", 13,
            "Put everything into one 7z archive (uses 7-Zip).");
    }

    static Availability SevenZipAvailability(OutputArchiveFormat format, EngineContext context) =>
        format == OutputArchiveFormat.SevenZip && context.Tools.SevenZip is null
            ? Availability.Unavailable(ArchiveWriters.SevenZipMissing)
            : Availability.Available;

    /// <summary>Collects <paramref name="inputs"/> and writes them to <paramref name="output"/>; returns the warnings.</summary>
    static async Task<IReadOnlyList<string>> CompressAsync(
        OutputArchiveFormat format, IReadOnlyList<InputFile> inputs, PendingOutput output, OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Gathering files…"));
        var items = await Task.Run(() => FileCollector.Collect(inputs.Select(i => i.Path).ToList(), cancellationToken), cancellationToken).ConfigureAwait(false);
        if (items.Items.Count == 0)
            throw new OperationException("There's nothing to compress — links and junctions are skipped.");

        var warnings = await Task.Run(
            () => ArchiveWriters.WriteAsync(format, items, output.WorkingPath, request.Context, request.TempDirectory, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return [.. items.Warnings(), .. warnings];
    }

    /// <summary>Folder → archive next to the folder ("Shots" → "Shots.zip").</summary>
    sealed class FolderOperation(OutputArchiveFormat format, EngineContext context, string id, int order, string description) : FileOperation(
        Infos.Convert(id, ArchiveWriters.Label(format), $"Compress to {ArchiveWriters.Label(format)}", description, "archive", ArchiveWriters.Extension(format), order, "Compress folder").Heavy(),
        Inputs.Folders)
    {
        public override Availability CheckAvailability() => SevenZipAvailability(format, context);

        public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            using var output = request.Output.BeginFile(request.Input, ArchiveWriters.Extension(format));
            var warnings = await CompressAsync(format, request.Inputs, output, request, progress, cancellationToken).ConfigureAwait(false);
            return new OperationResult { Outputs = [output.Commit()], Warnings = warnings };
        }
    }

    /// <summary>Everything dropped → one archive: "photo.zip" for one input, "photo + 3 more.zip" for several.</summary>
    sealed class CompressAction(OutputArchiveFormat format, EngineContext context, string id, string variant, int order, string description) : FileOperation(
        Infos.Action(id, "Archive", $"Compress to {ArchiveWriters.Label(format)}", description, "compress", ArchiveWriters.Extension(format), order, variant).Heavy(),
        Inputs.Anything,
        inputMode: InputMode.Combined,
        minInputs: 1)
    {
        public override Availability CheckAvailability() => SevenZipAvailability(format, context);

        public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            var first = request.Inputs[0];
            var extension = ArchiveWriters.Extension(format);
            using var output = request.Inputs.Count == 1
                ? request.Output.BeginFile(first, extension)
                : request.Output.BeginFile(request.Output.ResolveDirectory(first), $"{first.BaseName} + {request.Inputs.Count - 1} more", extension);
            var warnings = await CompressAsync(format, request.Inputs, output, request, progress, cancellationToken).ConfigureAwait(false);
            return new OperationResult { Outputs = [output.Commit()], Warnings = warnings };
        }
    }

    /// <summary>One ZIP per dropped file or folder.</summary>
    sealed class CompressEachAction() : FileOperation(
        Infos.Action("files.compress.zip-each", "Archive", "Compress each to ZIP", "Make a separate ZIP for every file or folder.", "compress", ".zip", 11, "ZIP each separately").Heavy(),
        Inputs.Anything)
    {
        public override async Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
        {
            using var output = request.Output.BeginFile(request.Input, ".zip");
            var warnings = await CompressAsync(OutputArchiveFormat.Zip, request.Inputs, output, request, progress, cancellationToken).ConfigureAwait(false);
            return new OperationResult { Outputs = [output.Commit()], Warnings = warnings };
        }
    }
}
