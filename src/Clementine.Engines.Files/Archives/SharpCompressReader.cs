using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Clementine.Engines.Files.Text;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Readers;

namespace Clementine.Engines.Files.Archives;

/// <summary>ZIP, 7z and RAR through SharpCompress (passwords, ZipCrypto/AES, solid archives, multi-part RAR).</summary>
internal sealed partial class SharpCompressReader : IArchiveReader
{
    readonly IArchive _archive = null!;
    readonly Stream? _stream;
    IReadOnlyList<ArchiveEntry>? _tableOfContents;
    int _visited;

    public SharpCompressReader(string path, ArchiveFormat format, string? password)
    {
        Format = format;
        var options = new ReaderOptions
        {
            Password = string.IsNullOrEmpty(password) ? null : password,
            LeaveStreamOpen = false,
            ArchiveEncoding = NameEncoding.Create(),
        };

        if (format == ArchiveFormat.Rar && FirstVolumePattern().IsMatch(Path.GetFileName(path)))
        {
            var parts = ArchiveFactory.GetFileParts(path).Select(p => new FileInfo(p)).ToList();
            _archive = RarArchive.OpenArchive(parts, options);
            try
            {
                LoadTableOfContents();
            }
            catch
            {
                _archive.Dispose();
                throw;
            }
        }
        else
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            try
            {
                _archive = format switch
                {
                    ArchiveFormat.Zip => ZipArchive.OpenArchive(_stream, options),
                    ArchiveFormat.SevenZip => SevenZipArchive.OpenArchive(_stream, options),
                    ArchiveFormat.Rar => RarArchive.OpenArchive(_stream, options),
                    _ => throw new ArgumentOutOfRangeException(nameof(format)),
                };
                LoadTableOfContents();
            }
            catch
            {
                _archive?.Dispose();
                _stream.Dispose();
                throw;
            }
        }
    }

    public ArchiveFormat Format { get; }

    public bool IsStreamed => Format == ArchiveFormat.SevenZip || _archive.IsSolid;

    public IReadOnlyList<ArchiveEntry> TableOfContents => _tableOfContents!;

    /// <summary>Read eagerly so header problems (encrypted 7z headers, damage) surface while opening.</summary>
    void LoadTableOfContents() => _tableOfContents = _archive.Entries.Select(e => ToEntry(e, Format)).ToList();

    /// <summary>Entries visited so far (the file position is meaningless: headers live at the end of ZIP and 7z files).</summary>
    public double Progress => TableOfContents.Count == 0 ? 0 : Math.Clamp((double)_visited / TableOfContents.Count, 0, 1);

    public void ReadAll(Func<ArchiveEntry, bool> wantData, Action<ArchiveEntry, Stream?> visit, CancellationToken cancellationToken)
    {
        if (IsStreamed)
        {
            // Solid archives must be decompressed front to back; random access would be quadratic.
            using var reader = _archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Visit(ToEntry(reader.Entry, Format), entry => reader.OpenEntryStream(), wantData, visit);
            }
            return;
        }

        foreach (var archiveEntry in _archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Visit(ToEntry(archiveEntry, Format), _ => archiveEntry.OpenEntryStream(), wantData, visit);
        }
    }

    void Visit(ArchiveEntry entry, Func<ArchiveEntry, Stream> open, Func<ArchiveEntry, bool> wantData, Action<ArchiveEntry, Stream?> visit)
    {
        _visited++;
        if (entry.Kind == ArchiveEntryKind.File && wantData(entry))
        {
            if (Format == ArchiveFormat.SevenZip && entry.Size == 0)
            {
                // Empty 7z files have no data stream (and no folder) to open.
                visit(entry, Stream.Null);
                return;
            }
            using var data = open(entry);
            visit(entry, data);
        }
        else
        {
            visit(entry, null);
        }
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream?.Dispose();
    }

    static ArchiveEntry ToEntry(IEntry e, ArchiveFormat format)
    {
        var kind = KindOf(e);
        return new ArchiveEntry
        {
            Path = e.Key ?? "",
            Kind = kind,
            Size = kind == ArchiveEntryKind.File && e.Size >= 0 ? e.Size : null,
            PackedSize = e.CompressedSize > 0 ? e.CompressedSize : null,
            Modified = e.LastModifiedTime,
            // SharpCompress reports 7z entries without a data stream (empty files, folders) as encrypted, which
            // made every 7z holding an empty file ask for a password. There is nothing to decrypt in them.
            IsEncrypted = e.IsEncrypted && !(format == ArchiveFormat.SevenZip && (e.IsDirectory || e.Size == 0)),
            Crc32 = e.Crc is > 0 and <= uint.MaxValue ? (uint)e.Crc : null,
            LinkTarget = e is RarEntry { IsRedir: true } rar ? rar.RedirTargetName : NullIfEmpty(e.LinkTarget),
        };
    }

    /// <summary>
    /// Directories, Unix symlinks/devices (mode bits in the high word of ZIP/7z attributes), Windows reparse points
    /// and RAR5 redirections are told apart so links are never written to disk.
    /// </summary>
    internal static ArchiveEntryKind KindOf(IEntry e)
    {
        if (e.IsDirectory)
            return ArchiveEntryKind.Directory;
        if (e is SevenZipArchiveEntry { IsAnti: true })
            return ArchiveEntryKind.Special;
        if (e is RarEntry { IsRedir: true } || !string.IsNullOrEmpty(e.LinkTarget))
            return ArchiveEntryKind.SymbolicLink;
        if (e.Attrib is { } attributes)
            return KindFromAttributes(attributes);
        return ArchiveEntryKind.File;
    }

    internal static ArchiveEntryKind KindFromAttributes(int attributes)
    {
        const int ReparsePoint = 0x400;
        var unixType = (attributes >> 16) & 0xF000;
        return unixType switch
        {
            0xA000 => ArchiveEntryKind.SymbolicLink,
            0x4000 => ArchiveEntryKind.Directory,
            0x1000 or 0x2000 or 0x6000 or 0xC000 => ArchiveEntryKind.Special,
            _ when (attributes & ReparsePoint) != 0 => ArchiveEntryKind.SymbolicLink,
            _ => ArchiveEntryKind.File,
        };
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex(@"\.part0*1\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex FirstVolumePattern();

    /// <summary>
    /// Entry names without the ZIP "UTF-8" flag: many tools write UTF-8 anyway, older Windows tools use the OEM
    /// code page. Try strict UTF-8 first, then fall back to the system OEM code page.
    /// </summary>
    static class NameEncoding
    {
        static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

        public static ArchiveEncoding Create()
        {
            TextDecoder.EnsureCodePages();
            var oem = Oem();
            return new ArchiveEncoding
            {
                Default = oem,
                CustomDecoder = (bytes, start, length, type) =>
                {
                    if (type == EncodingType.UTF8)
                        return Encoding.UTF8.GetString(bytes, start, length);
                    try
                    {
                        return StrictUtf8.GetString(bytes, start, length);
                    }
                    catch (DecoderFallbackException)
                    {
                        return oem.GetString(bytes, start, length);
                    }
                },
            };
        }

        static Encoding Oem()
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return Encoding.GetEncoding(437);
            }
        }
    }
}
