using System.Formats.Tar;

namespace Clementine.Engines.Files.Archives;

/// <summary>Tar archives, optionally gzip/bzip2/xz/zstd compressed, read with System.Formats.Tar (PAX, GNU, ustar, v7).</summary>
internal sealed class TarStreamReader : IArchiveReader
{
    readonly ProgressStream _file;
    readonly StreamCompression _compression;

    public TarStreamReader(string path, ArchiveFormat format)
    {
        Format = format;
        _compression = ArchiveFormats.CompressionOf(format);
        _file = new ProgressStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
    }

    public ArchiveFormat Format { get; }

    public bool IsStreamed => true;

    public IReadOnlyList<ArchiveEntry>? TableOfContents => null;

    public double Progress => _file.Fraction;

    public void ReadAll(Func<ArchiveEntry, bool> wantData, Action<ArchiveEntry, Stream?> visit, CancellationToken cancellationToken)
    {
        _file.Position = 0;
        using var data = Decompressors.Open(_file, _compression);
        using var reader = new TarReader(data, leaveOpen: true);
        while (reader.GetNextEntry(copyData: false) is { } tarEntry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tarEntry.EntryType == TarEntryType.GlobalExtendedAttributes)
                continue; // archive-wide PAX metadata, not a member

            var entry = ToEntry(tarEntry);
            if (entry.Kind == ArchiveEntryKind.File && wantData(entry))
                visit(entry, tarEntry.DataStream ?? Stream.Null);
            else
                visit(entry, null);
        }
    }

    static ArchiveEntry ToEntry(TarEntry e)
    {
        var kind = e.EntryType switch
        {
            TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => ArchiveEntryKind.File,
            TarEntryType.Directory => ArchiveEntryKind.Directory,
            TarEntryType.SymbolicLink => ArchiveEntryKind.SymbolicLink,
            TarEntryType.HardLink => ArchiveEntryKind.HardLink,
            _ => ArchiveEntryKind.Special,
        };
        return new ArchiveEntry
        {
            Path = e.Name,
            Kind = kind,
            Size = kind == ArchiveEntryKind.File ? e.Length : null,
            Modified = e.ModificationTime.UtcDateTime,
            LinkTarget = string.IsNullOrEmpty(e.LinkName) ? null : e.LinkName,
        };
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>A single compressed file (.gz, .bz2, .xz, .zst) presented as an archive holding one entry.</summary>
internal sealed class SingleStreamReader : IArchiveReader
{
    readonly ProgressStream _file;
    readonly ArchiveEntry _entry;

    public SingleStreamReader(string path, ArchiveFormat format)
    {
        Format = format;
        _file = new ProgressStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
        // The uncompressed size is unknown until decompressed (gzip's ISIZE trailer is mod 2³² and per member).
        _entry = new ArchiveEntry
        {
            Path = InnerName(path),
            Kind = ArchiveEntryKind.File,
            PackedSize = _file.Length,
            Modified = File.GetLastWriteTimeUtc(path),
        };
    }

    public ArchiveFormat Format { get; }

    public bool IsStreamed => true;

    public IReadOnlyList<ArchiveEntry> TableOfContents => [_entry];

    public double Progress => _file.Fraction;

    public void ReadAll(Func<ArchiveEntry, bool> wantData, Action<ArchiveEntry, Stream?> visit, CancellationToken cancellationToken)
    {
        _file.Position = 0;
        if (!wantData(_entry))
        {
            visit(_entry, null);
            return;
        }
        using var data = Decompressors.Open(_file, ArchiveFormats.CompressionOf(Format));
        visit(_entry, data);
    }

    /// <summary>"notes.txt.gz" → "notes.txt"; a bare ".gz" → "data".</summary>
    internal static string InnerName(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        var inner = name.Length > extension.Length ? name[..^extension.Length] : "";
        return string.IsNullOrWhiteSpace(inner) ? "data" : inner;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Opens the right reader for a file, translating library failures into friendly messages.</summary>
internal static class ArchiveReaders
{
    public static IArchiveReader Open(string path, string? password = null, string hint = ArchiveErrors.ExtractHint)
    {
        var format = ArchiveFormats.Detect(path);
        try
        {
            return format switch
            {
                ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Rar => new SharpCompressReader(path, format, password),
                _ when ArchiveFormats.IsTar(format) => new TarStreamReader(path, format),
                _ => new SingleStreamReader(path, format),
            };
        }
        catch (Exception ex) when (ArchiveErrors.IsReadFailure(ex, password, entry: null))
        {
            throw ArchiveErrors.Translate(ex, password, entry: null, hint);
        }
    }
}
