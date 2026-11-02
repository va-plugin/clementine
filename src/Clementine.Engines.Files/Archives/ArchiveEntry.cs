namespace Clementine.Engines.Files.Archives;

/// <summary>What an archive entry is. Only files and directories are ever written to disk.</summary>
internal enum ArchiveEntryKind
{
    File,
    Directory,
    SymbolicLink,
    HardLink,

    /// <summary>Devices, FIFOs, sparse files, tar meta entries — skipped.</summary>
    Special,
}

/// <summary>Metadata of one archive entry, independent of the library that read it.</summary>
internal sealed record ArchiveEntry
{
    /// <summary>The path exactly as stored in the archive (untrusted).</summary>
    public required string Path { get; init; }

    public required ArchiveEntryKind Kind { get; init; }

    /// <summary>Uncompressed size, when the format records it.</summary>
    public long? Size { get; init; }

    public long? PackedSize { get; init; }

    public DateTime? Modified { get; init; }

    public bool IsEncrypted { get; init; }

    /// <summary>Stored CRC-32 of the data, when the format records one (null or 0 = unknown).</summary>
    public uint? Crc32 { get; init; }

    public string? LinkTarget { get; init; }
}

/// <summary>
/// Sequential, library-neutral access to an archive. Implementations are single-use and not thread-safe.
/// </summary>
internal interface IArchiveReader : IDisposable
{
    ArchiveFormat Format { get; }

    /// <summary>True for formats where reading one entry means decompressing the ones before it (solid 7z/RAR, tar streams).</summary>
    bool IsStreamed { get; }

    /// <summary>
    /// The archive's table of contents when it can be read without decompressing anything (ZIP, 7z, RAR), else null.
    /// </summary>
    IReadOnlyList<ArchiveEntry>? TableOfContents { get; }

    /// <summary>Fraction of the archive file consumed so far (0..1).</summary>
    double Progress { get; }

    /// <summary>
    /// Visits every entry in archive order. Files get a readable data stream that is only valid during the call
    /// (null when <paramref name="wantData"/> says no). Other kinds always get null.
    /// </summary>
    void ReadAll(Func<ArchiveEntry, bool> wantData, Action<ArchiveEntry, Stream?> visit, CancellationToken cancellationToken);
}
