using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

/// <summary>Builds small archives and trees for the tests (in code, or with 7-Zip when a format needs it).</summary>
internal static class Fixtures
{
    public static string? SevenZip => new ToolLocator(() => new ClementineSettings()).SevenZip;

    /// <summary>A tree with nested folders, Unicode names, an empty folder and a few kinds of file.</summary>
    public static string SourceTree(TestWorkspace workspace, string name = "Ünïcødé Pack")
    {
        var root = Path.Combine(workspace.Root, "src", name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "readme.txt"), "Hello from Clementine\n");
        Directory.CreateDirectory(Path.Combine(root, "東京 – sub", "deeper"));
        File.WriteAllText(Path.Combine(root, "東京 – sub", "файл.txt"), "Привет, мир — ünïcødé ✓\n");
        File.WriteAllBytes(Path.Combine(root, "東京 – sub", "deeper", "data.bin"), Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
        Directory.CreateDirectory(Path.Combine(root, "empty folder"));
        return root;
    }

    /// <summary>Relative path → SHA-256 for every file under <paramref name="root"/>, plus "dir/" markers for folders.</summary>
    public static SortedDictionary<string, string> Snapshot(string root)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            map[entry is DirectoryInfo ? relative + "/" : relative] = entry is FileInfo file ? Hash(file.FullName) : "";
        }
        return map;
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static string Zip(string path, params (string Name, string? Content)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            if (content is null)
                continue;
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return path;
    }

    /// <summary>A ZIP entry flagged as a Unix symbolic link (mode 0120777).</summary>
    public static string ZipWithSymlink(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var link = zip.CreateEntry("link-to-passwd");
        link.ExternalAttributes = unchecked((int)(0xA1FF_0000u));
        using (var writer = new StreamWriter(link.Open()))
            writer.Write("../../../../Windows/win.ini");
        var ok = zip.CreateEntry("ok.txt");
        using (var writer = new StreamWriter(ok.Open()))
            writer.Write("fine");
        return path;
    }

    public static string Tar(string path, Func<Stream, Stream>? compress, params TarEntry[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var stream = compress?.Invoke(file) ?? file;
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
                writer.WriteEntry(entry);
        }
        return path;
    }

    public static PaxTarEntry TarFile(string name, string content) => new(TarEntryType.RegularFile, name)
    {
        DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
    };

    public static PaxTarEntry TarDirectory(string name) => new(TarEntryType.Directory, name);

    public static PaxTarEntry TarLink(string name, string target, bool hard = false) =>
        new(hard ? TarEntryType.HardLink : TarEntryType.SymbolicLink, name) { LinkName = target };

    public static Stream Gzip(Stream s) => new GZipStream(s, CompressionLevel.Fastest, leaveOpen: true);

    public static Stream Bzip2(Stream s) => SharpCompress.Compressors.BZip2.BZip2Stream.Create(
        s, SharpCompress.Compressors.CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true, tolerateTruncatedStream: false);

    public static Stream Zstd(Stream s) => new SharpCompress.Compressors.ZStandard.CompressionStream(s, 3, 0, leaveOpen: true);

    /// <summary>Runs 7-Zip (tests that need it are skipped when it isn't installed).</summary>
    public static async Task SevenZipAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(SevenZip!, arguments, new ProcessRunOptions { WorkingDirectory = workingDirectory });
        if (!result.Succeeded)
            throw new InvalidOperationException($"7-Zip failed ({result.ExitCode}): {result.StdErrTail}");
    }

    /// <summary>
    /// A RAR 4.x archive with stored (uncompressed) entries, assembled from the publicly documented block layout
    /// (marker, archive header, file headers, end block). No RAR compression is involved, so no RAR software is needed.
    /// </summary>
    public static string StoredRar(string path, params (string Name, string? Content)[] entries)
    {
        using var output = new MemoryStream();
        output.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]); // marker block "Rar!\x1A\x07\0"
        WriteBlock(output, 0x73, 0x0000, new byte[6]);           // archive header (RESERVED1, RESERVED2)

        var dosTime = DosDateTime(new DateTime(2024, 5, 1, 12, 30, 0));
        foreach (var (name, content) in entries)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name.Replace('/', '\\'));
            var data = content is null ? [] : Encoding.UTF8.GetBytes(content);
            var isDirectory = content is null;
            var body = new MemoryStream();
            var w = new BinaryWriter(body);
            w.Write((uint)data.Length);                        // PACK_SIZE
            w.Write((uint)data.Length);                        // UNP_SIZE
            w.Write((byte)2);                                  // HOST_OS = Win32
            w.Write(isDirectory ? 0u : Crc32.HashToUInt32(data)); // FILE_CRC
            w.Write(dosTime);                                  // FTIME
            w.Write((byte)20);                                 // UNP_VER 2.0
            w.Write((byte)0x30);                               // METHOD = store
            w.Write((ushort)nameBytes.Length);                 // NAME_SIZE
            w.Write(isDirectory ? 0x10u : 0x20u);              // ATTR
            w.Write(nameBytes);
            w.Flush();
            var flags = (ushort)(0x8000 | (isDirectory ? 0x00E0 : 0));
            WriteBlock(output, 0x74, flags, body.ToArray());
            output.Write(data);
        }
        WriteBlock(output, 0x7B, 0x4000, []);                  // end of archive
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, output.ToArray());
        return path;
    }

    static void WriteBlock(Stream output, byte type, ushort flags, byte[] body)
    {
        var header = new MemoryStream();
        var w = new BinaryWriter(header);
        w.Write(type);
        w.Write(flags);
        w.Write((ushort)(7 + body.Length));
        w.Write(body);
        w.Flush();
        var bytes = header.ToArray();
        var crc = (ushort)(Crc32.HashToUInt32(bytes) & 0xFFFF);
        output.Write(BitConverter.GetBytes(crc));
        output.Write(bytes);
    }

    static uint DosDateTime(DateTime t) =>
        (uint)(((t.Year - 1980) << 25) | (t.Month << 21) | (t.Day << 16) | (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
}
