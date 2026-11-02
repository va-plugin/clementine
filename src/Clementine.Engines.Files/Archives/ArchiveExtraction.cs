namespace Clementine.Engines.Files.Archives;

/// <summary>Extract/Test entry points: Clementine's own readers first, 7-Zip only for methods they can't decode.</summary>
internal static class ArchiveExtraction
{
    public static async Task<ExtractionSummary> ExtractAsync(
        EngineContext context, string archive, string destination, ExtractOptions options, ExtractionLimits limits, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var format = ArchiveFormats.Detect(archive);
        try
        {
            return await Task.Run(() =>
            {
                using var reader = ArchiveReaders.Open(archive, options.Password, options.PasswordHint);
                return new ArchiveExtractor(limits).Extract(reader, destination, options, progress, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationException ex) when (SevenZipFallback.CanHelp(ex, format, context))
        {
            context.Log.Info($"{Path.GetFileName(archive)}: {ex.Message} Retrying with 7-Zip. {ex.Details}");
            return await SevenZipFallback.ExtractAsync(context, archive, destination, options, limits, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The "Archive OK" text, or an <see cref="OperationException"/> describing the damage.</summary>
    public static async Task<string> TestAsync(
        EngineContext context, InputFile input, ExtractOptions options, ExtractionLimits limits, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var format = ArchiveFormats.Detect(input.Path);
        IntegrityReport report;
        try
        {
            report = await Task.Run(() =>
            {
                using var reader = ArchiveReaders.Open(input.Path, options.Password, options.PasswordHint);
                return ArchiveInspector.Test(reader, options, limits, progress, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationException ex) when (SevenZipFallback.CanHelp(ex, format, context))
        {
            await SevenZipFallback.TestAsync(context, input.Path, options, progress, cancellationToken).ConfigureAwait(false);
            return $"{input.Name}: every file is intact (checked with 7-Zip, which verified every CRC).";
        }

        if (!report.IsOk)
        {
            var more = report.Problems.Count > 1 ? $" (and {report.Problems.Count - 1} more problem{(report.Problems.Count > 2 ? "s" : "")})" : "";
            throw new OperationException($"The archive is damaged: {report.Problems[0].TrimEnd('.')}{more}.", string.Join(Environment.NewLine, report.Problems));
        }
        if (report.Files == 0)
            return $"{input.Name} contains no files (only folders).";

        var files = report.Files == 1 ? "The file is" : $"All {report.Files:N0} files are";
        var how = report.Verified == report.Files
            ? "every CRC-32 matched"
            : format == ArchiveFormat.Tar
                ? "plain TAR has no checksums, so only its structure was checked"
                : ArchiveFormats.IsTar(format) || ArchiveFormats.IsSingleStream(format)
                    ? "the stream checksum matched"
                    : $"{report.Verified:N0} of {report.Files:N0} have a stored CRC-32 and all matched";
        return $"{input.Name}: {files} intact ({ByteSize.Format(report.Bytes)} read; {how}).";
    }
}
