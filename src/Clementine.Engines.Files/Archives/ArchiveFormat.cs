using System.IO.Compression;
using Clementine.Core;

namespace Clementine.Engines.Files.Archives;

/// <summary>Archive containers and single-stream compressors Clementine reads.</summary>
internal enum ArchiveFormat
{
    Zip,
    SevenZip,
    Rar,
    Tar,
    TarGz,
    TarBz2,
    TarXz,
    TarZst,
    /// <summary>A single gzip-compressed file (not a tar).</summary>
    Gz,
    Bz2,
    Xz,
    Zst,
}

/// <summary>Stream compression wrapped around a tar or a single file.</summary>
internal enum StreamCompression
{
    None,
    GZip,
    BZip2,
    Xz,
    Zstd,
}

/// <summary>Format detection by magic bytes (trusted) and extension (hint), plus labels.</summary>
internal static class ArchiveFormats
{
    public static string Label(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7Z",
        ArchiveFormat.Rar => "RAR",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.TarGz => "TAR.GZ",
        ArchiveFormat.TarBz2 => "TAR.BZ2",
        ArchiveFormat.TarXz => "TAR.XZ",
        ArchiveFormat.TarZst => "TAR.ZST",
        ArchiveFormat.Gz => "GZ",
        ArchiveFormat.Bz2 => "BZ2",
        ArchiveFormat.Xz => "XZ",
        ArchiveFormat.Zst => "ZST",
        _ => format.ToString().ToUpperInvariant(),
    };

    public static bool IsTar(ArchiveFormat format) =>
        format is ArchiveFormat.Tar or ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz or ArchiveFormat.TarZst;

    public static bool IsSingleStream(ArchiveFormat format) =>
        format is ArchiveFormat.Gz or ArchiveFormat.Bz2 or ArchiveFormat.Xz or ArchiveFormat.Zst;

    public static StreamCompression CompressionOf(ArchiveFormat format) => format switch
    {
        ArchiveFormat.TarGz or ArchiveFormat.Gz => StreamCompression.GZip,
        ArchiveFormat.TarBz2 or ArchiveFormat.Bz2 => StreamCompression.BZip2,
        ArchiveFormat.TarXz or ArchiveFormat.Xz => StreamCompression.Xz,
        ArchiveFormat.TarZst or ArchiveFormat.Zst => StreamCompression.Zstd,
        _ => StreamCompression.None,
    };

    /// <summary>The format an extension (as produced by <see cref="FileTypes.ExtensionOf"/>) suggests.</summary>
    public static ArchiveFormat? FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".zip" => ArchiveFormat.Zip,
        ".7z" => ArchiveFormat.SevenZip,
        ".rar" => ArchiveFormat.Rar,
        ".tar" => ArchiveFormat.Tar,
        ".tar.gz" or ".tgz" => ArchiveFormat.TarGz,
        ".tar.bz2" or ".tbz2" or ".tbz" => ArchiveFormat.TarBz2,
        ".tar.xz" or ".txz" => ArchiveFormat.TarXz,
        ".tar.zst" or ".tzst" => ArchiveFormat.TarZst,
        ".gz" => ArchiveFormat.Gz,
        ".bz2" => ArchiveFormat.Bz2,
        ".xz" => ArchiveFormat.Xz,
        ".zst" => ArchiveFormat.Zst,
        _ => null,
    };

    /// <summary>
    /// Identifies an archive from its first bytes. Compressed streams are reported as the single-stream format;
    /// <see cref="Detect"/> decides whether a tar is inside.
    /// </summary>
    public static ArchiveFormat? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("PK\u0003\u0004"u8) || head.StartsWith("PK\u0005\u0006"u8) || head.StartsWith("PK\u0007\u0008"u8))
            return ArchiveFormat.Zip;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
            return ArchiveFormat.SevenZip;
        if (head.StartsWith("Rar!\u001A\u0007"u8))
            return ArchiveFormat.Rar;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
            return ArchiveFormat.Gz;
        if (head.Length >= 4 && head.StartsWith("BZh"u8) && head[3] is >= (byte)'1' and <= (byte)'9')
            return ArchiveFormat.Bz2;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]))
            return ArchiveFormat.Xz;
        if (head.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]))
            return ArchiveFormat.Zst;
        if (LooksLikeTar(head))
            return ArchiveFormat.Tar;
        return null;
    }

    /// <summary>Works out what <paramref name="path"/> really is. Throws a friendly error for anything unreadable.</summary>
    public static ArchiveFormat Detect(string path)
    {
        var head = new byte[512];
        int read;
        using (var stream = File.OpenRead(path))
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        var hinted = FromExtension(FileTypes.ExtensionOf(path));
        var sniffed = Sniff(head.AsSpan(0, read));
        if (sniffed is null)
        {
            if (read == 0)
                throw new OperationException("This archive is empty (0 bytes).");
            var label = hinted is { } h ? Label(h) : "archive";
            throw new OperationException($"This doesn't look like a valid {label} file — it may be damaged or not an archive at all.");
        }

        if (!IsSingleStream(sniffed.Value))
            return sniffed.Value;

        // A compressed stream: a tar inside unless the file says otherwise (peek at the first decompressed block).
        var tarFormat = sniffed.Value switch
        {
            ArchiveFormat.Gz => ArchiveFormat.TarGz,
            ArchiveFormat.Bz2 => ArchiveFormat.TarBz2,
            ArchiveFormat.Xz => ArchiveFormat.TarXz,
            _ => ArchiveFormat.TarZst,
        };
        if (hinted == tarFormat)
            return tarFormat;
        return DecompressedLooksLikeTar(path, CompressionOf(sniffed.Value)) ? tarFormat : sniffed.Value;
    }

    /// <summary>A POSIX/GNU tar header: "ustar" magic, or (old v7 tars) a valid header checksum.</summary>
    public static bool LooksLikeTar(ReadOnlySpan<byte> block)
    {
        if (block.Length < 512)
            return false;
        if (block.Slice(257, 5).SequenceEqual("ustar"u8))
            return true;
        if (block[0] == 0)
            return false;

        // Checksum: sum of all header bytes with the checksum field (148..155) counted as spaces.
        long sum = 0;
        for (var i = 0; i < 512; i++)
            sum += i is >= 148 and < 156 ? (byte)' ' : block[i];
        var field = System.Text.Encoding.ASCII.GetString(block.Slice(148, 8)).Trim('\0', ' ');
        try
        {
            return field.Length > 0 && Convert.ToInt64(field, 8) == sum;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static bool DecompressedLooksLikeTar(string path, StreamCompression compression)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var stream = Decompressors.Open(file, compression);
            var block = new byte[512];
            var read = stream.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);
            return read == block.Length && LooksLikeTar(block);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or SharpCompress.Common.SharpCompressException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Opens decompression streams for each <see cref="StreamCompression"/>.</summary>
internal static class Decompressors
{
    /// <summary>A read-only decompressing view of <paramref name="compressed"/> (left open on dispose).</summary>
    public static Stream Open(Stream compressed, StreamCompression compression) => compression switch
    {
        StreamCompression.None => new NonClosingStream(compressed),
        StreamCompression.GZip => new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true),
        StreamCompression.BZip2 => SharpCompress.Compressors.BZip2.BZip2Stream.Create(
            compressed, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true, tolerateTruncatedStream: false),
        StreamCompression.Xz => new SharpCompress.Compressors.Xz.XZStream(new NonClosingStream(compressed)),
        StreamCompression.Zstd => new SharpCompress.Compressors.ZStandard.DecompressionStream(compressed, 0, checkEndOfStream: true, leaveOpen: true),
        _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };
}
