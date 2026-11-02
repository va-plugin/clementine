using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Clementine.Engines.Files.Archives;

namespace Clementine.Engines.Files.Operations;

/// <summary>Hash algorithms offered by the Checksum action.</summary>
internal enum ChecksumAlgorithm
{
    Sha256,
    Sha1,
    Md5,
    Crc32,
}

/// <summary>Streaming file hashing.</summary>
internal static class Checksums
{
    public static string Label(ChecksumAlgorithm algorithm) => algorithm switch
    {
        ChecksumAlgorithm.Sha256 => "SHA-256",
        ChecksumAlgorithm.Sha1 => "SHA-1",
        ChecksumAlgorithm.Md5 => "MD5",
        _ => "CRC32",
    };

    /// <summary>Hex digest of a file: lower-case for SHA/MD5, upper-case for CRC32 (as 7-Zip and SFV files show it).</summary>
    public static string Compute(string path, ChecksumAlgorithm algorithm, Action<int>? onRead, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            if (algorithm == ChecksumAlgorithm.Crc32)
            {
                var crc = new System.IO.Hashing.Crc32();
                Pump(stream, buffer, span => crc.Append(span), onRead, cancellationToken);
                return crc.GetCurrentHashAsUInt32().ToString("X8");
            }

            using var hash = IncrementalHash.CreateHash(algorithm switch
            {
                ChecksumAlgorithm.Sha1 => HashAlgorithmName.SHA1,
                ChecksumAlgorithm.Md5 => HashAlgorithmName.MD5,
                _ => HashAlgorithmName.SHA256,
            });
            Pump(stream, buffer, span => hash.AppendData(span), onRead, cancellationToken);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    delegate void Append(ReadOnlySpan<byte> data);

    static void Pump(Stream stream, byte[] buffer, Append append, Action<int>? onRead, CancellationToken cancellationToken)
    {
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            append(buffer.AsSpan(0, read));
            onRead?.Invoke(read);
        }
    }
}

/// <summary>The Checksum action: one text result listing a digest per file ("sha256sum" format for several).</summary>
internal sealed class ChecksumOperation(ChecksumAlgorithm algorithm, int order) : FileOperation(
    Infos.Action(
        $"files.checksum.{algorithm.ToString().ToLowerInvariant()}",
        "Checksum",
        $"{Checksums.Label(algorithm)} checksum",
        algorithm switch
        {
            ChecksumAlgorithm.Sha256 => "Compute SHA-256 hashes to verify downloads and copies.",
            ChecksumAlgorithm.Sha1 => "Compute SHA-1 hashes (older, still common on download pages).",
            ChecksumAlgorithm.Md5 => "Compute MD5 hashes (quick comparisons; not for security).",
            _ => "Compute CRC32 checksums (as shown by 7-Zip and SFV files).",
        },
        "hash",
        null,
        order,
        Checksums.Label(algorithm)).Heavy(),
    Inputs.Anything,
    inputMode: InputMode.Combined,
    minInputs: 1)
{
    protected override OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(JobProgress.Indeterminate("Gathering files…"));
        var items = FileCollector.Collect(request.Inputs.Select(i => i.Path).ToList(), cancellationToken);
        var files = items.Items.Where(i => !i.IsDirectory).ToList();
        if (files.Count == 0)
            throw new OperationException("There are no files to checksum here.");

        var label = Checksums.Label(algorithm);
        long done = 0, lastReport = 0;
        var lines = new StringBuilder();
        var single = request.Inputs.Count == 1 && !request.Inputs[0].IsDirectory;
        string? digest = null;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                digest = Checksums.Compute(file.SourcePath, algorithm, read =>
                {
                    done += read;
                    if (done - lastReport >= 16 << 20)
                    {
                        lastReport = done;
                        // Files can grow while they're hashed (recordings, downloads): never report more than 100 %.
                        progress.Report(new JobProgress(items.TotalBytes == 0 ? null : Math.Clamp((double)done / items.TotalBytes, 0, 1), $"Computing {label}…"));
                    }
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OperationException($"Couldn't read \"{file.EntryName}\": {ex.Message}", ex.ToString(), ex);
            }
            lines.Append(digest).Append("  ").Append(file.EntryName).Append('\n');
        }

        var result = single
            ? OperationResult.FromText($"{label} of {request.Inputs[0].Name}", digest!)
            : OperationResult.FromText($"{label} of {files.Count:N0} files", lines.ToString().TrimEnd('\n'));
        return result with { Warnings = items.Warnings() };
    }
}
