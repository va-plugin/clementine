using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Files.Archives;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

public class ArchiveTests
{
    static IOperation Op(TestWorkspace workspace, string id, ExtractionLimits? limits = null) =>
        workspace.Operation(limits is null ? new FilesOperationProvider() : new FilesOperationProvider(limits), id);

    static string ArchivesDir(TestWorkspace workspace)
    {
        var dir = Path.Combine(workspace.Root, "archives");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Builds an archive of <paramref name="source"/> (top folder included) in the given format.</summary>
    static async Task<string> MakeArchive(TestWorkspace workspace, string source, string format)
    {
        var dir = ArchivesDir(workspace);
        var path = Path.Combine(dir, "pack." + format);
        switch (format)
        {
            case "zip":
                ZipFile.CreateFromDirectory(source, path, CompressionLevel.Optimal, includeBaseDirectory: true, Encoding.UTF8);
                break;
            case "tar":
                await using (var file = File.Create(path))
                    TarFile.CreateFromDirectory(source, file, includeBaseDirectory: true);
                break;
            case "tar.gz" or "tgz" or "tar.bz2" or "tar.zst":
                await using (var file = File.Create(path))
                await using (var compressed = format switch { "tar.bz2" => Fixtures.Bzip2(file), "tar.zst" => Fixtures.Zstd(file), _ => Fixtures.Gzip(file) })
                    TarFile.CreateFromDirectory(source, compressed, includeBaseDirectory: true);
                break;
            case "tar.xz":
                var tar = Path.Combine(dir, "pack-for-xz.tar");
                await using (var file = File.Create(tar))
                    TarFile.CreateFromDirectory(source, file, includeBaseDirectory: true);
                await Fixtures.SevenZipAsync(dir, "a", "-txz", "-y", path, tar);
                File.Delete(tar);
                break;
            case "7z":
                await Fixtures.SevenZipAsync(Path.GetDirectoryName(source)!, "a", "-t7z", "-y", path, Path.GetFileName(source));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        return path;
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("tar.gz")]
    [InlineData("tgz")]
    [InlineData("tar.bz2")]
    [InlineData("tar.zst")]
    [InlineData("tar.xz")]
    [InlineData("7z")]
    public async Task Extract_round_trips_unicode_names_nested_and_empty_folders(string format)
    {
        if (format is "tar.xz" or "7z" && Fixtures.SevenZip is null)
            return; // needs 7-Zip to build the fixture
        using var workspace = new TestWorkspace("x-" + format.Replace('.', '-'));
        var source = Fixtures.SourceTree(workspace);
        var archive = await MakeArchive(workspace, source, format);
        var before = Fixtures.Hash(archive);

        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [archive]);

        var output = Assert.Single(result.Outputs);
        // The single top-level folder isn't nested twice: archives/Ünïcødé Pack, not archives/pack/Ünïcødé Pack.
        Assert.Equal(Path.Combine(ArchivesDir(workspace), "Ünïcødé Pack"), output);
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(output));
        Assert.Equal(before, Fixtures.Hash(archive));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(ArchivesDir(workspace)), p => p.Contains(".partial", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("gz")]
    [InlineData("bz2")]
    [InlineData("zst")]
    [InlineData("xz")]
    public async Task Single_compressed_file_extracts_next_to_the_archive(string format)
    {
        if (format == "xz" && Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("single-" + format);
        var dir = ArchivesDir(workspace);
        var original = Path.Combine(workspace.Root, "notes.txt");
        var content = string.Concat(Enumerable.Repeat("Some notes — ünïcødé.\n", 500));
        File.WriteAllText(original, content);
        var archive = Path.Combine(dir, "notes.txt." + format);
        if (format == "xz")
        {
            await Fixtures.SevenZipAsync(workspace.Root, "a", "-txz", "-y", archive, original);
        }
        else
        {
            await using var file = File.Create(archive);
            await using var compressed = format switch { "bz2" => Fixtures.Bzip2(file), "zst" => Fixtures.Zstd(file), _ => Fixtures.Gzip(file) };
            compressed.Write(File.ReadAllBytes(original));
        }

        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [archive]);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(dir, "notes.txt"), output);
        Assert.Equal(content, File.ReadAllText(output));
    }

    [Fact]
    public async Task Gz_holding_a_tar_is_recognised_by_its_content()
    {
        using var workspace = new TestWorkspace("gztar");
        var archive = Fixtures.Tar(Path.Combine(ArchivesDir(workspace), "backup.gz"), Fixtures.Gzip,
            Fixtures.TarFile("a.txt", "alpha"), Fixtures.TarFile("b.txt", "beta"));

        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [archive]);

        var output = Assert.Single(result.Outputs);
        Assert.Equal("backup", Path.GetFileName(output));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(output, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(output, "b.txt")));
    }

    [Fact]
    public async Task Wrong_extension_is_sniffed_from_magic_bytes()
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("sniff");
        var source = Fixtures.SourceTree(workspace);
        var sevenZip = await MakeArchive(workspace, source, "7z");
        var disguised = Path.Combine(ArchivesDir(workspace), "really-7z.zip");
        File.Copy(sevenZip, disguised);

        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [disguised]);

        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single(result.Outputs)));
    }

    [Fact]
    public async Task Hand_built_stored_rar_extracts_lists_and_tests()
    {
        using var workspace = new TestWorkspace("rar");
        var rar = Fixtures.StoredRar(Path.Combine(ArchivesDir(workspace), "music.rar"),
            ("Set", null), ("Set/intro.txt", "first track"), ("Set/outro.txt", "last track"));

        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [rar]);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("Set", Path.GetFileName(output));
        Assert.Equal("first track", File.ReadAllText(Path.Combine(output, "intro.txt")));
        Assert.Equal("last track", File.ReadAllText(Path.Combine(output, "outro.txt")));

        var list = await workspace.RunAsync(Op(workspace, "archive.list"), [rar]);
        Assert.Contains("RAR archive", list.Text);
        Assert.Contains("Set\\intro.txt".Replace('\\', Path.DirectorySeparatorChar), list.Text!.Replace('/', Path.DirectorySeparatorChar));

        var test = await workspace.RunAsync(Op(workspace, "archive.test"), [rar]);
        Assert.Contains("intact", test.Text);
        Assert.Contains("CRC-32", test.Text);
    }

    [Fact]
    public async Task Repack_chain_zip_targz_7z_tar_zip_keeps_every_byte()
    {
        using var workspace = new TestWorkspace("repack");
        var source = Fixtures.SourceTree(workspace);
        var zip = await MakeArchive(workspace, source, "zip");
        var before = Fixtures.Hash(zip);

        var targz = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.tar-gz"), [zip])).Outputs);
        Assert.EndsWith("pack.tar.gz", targz);
        var current = targz;
        if (Fixtures.SevenZip is not null)
        {
            current = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.7z"), [current])).Outputs);
            Assert.EndsWith("pack.7z", current);
        }
        var tar = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.tar"), [current])).Outputs);
        var back = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.zip"), [tar])).Outputs);
        Assert.EndsWith("pack (2).zip", back); // never overwrites the original pack.zip

        var extracted = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [back])).Outputs);
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(extracted));
        Assert.Equal(before, Fixtures.Hash(zip));
    }

    [Fact]
    public void Repack_is_not_offered_to_the_same_format()
    {
        using var workspace = new TestWorkspace("same");
        var zip = Fixtures.Zip(workspace.Path("a.zip"), ("a.txt", "a"));
        var targz = Fixtures.Tar(workspace.Path("b.tgz"), Fixtures.Gzip, Fixtures.TarFile("b.txt", "b"));
        var file = InputFile.FromPath(zip);
        var tgz = InputFile.FromPath(targz);
        Assert.False(Op(workspace, "archive.zip").Accepts(file));
        Assert.True(Op(workspace, "archive.tar-gz").Accepts(file));
        Assert.False(Op(workspace, "archive.tar-gz").Accepts(tgz));
        Assert.True(Op(workspace, "archive.zip").Accepts(tgz));
    }

    [Theory]
    [InlineData("folder.zip", ".zip")]
    [InlineData("folder.tar-gz", ".tar.gz")]
    [InlineData("folder.7z", ".7z")]
    public async Task Folder_converts_round_trip(string id, string extension)
    {
        if (id == "folder.7z" && Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace(id.Replace('.', '-'));
        var source = Fixtures.SourceTree(workspace);

        var archive = Assert.Single((await workspace.RunAsync(Op(workspace, id), [source])).Outputs);
        Assert.Equal(Path.Combine(Path.GetDirectoryName(source)!, "Ünïcødé Pack" + extension), archive);

        var extracted = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [archive])).Outputs);
        Assert.Equal("Ünïcødé Pack (2)", Path.GetFileName(extracted)); // the source folder is still there
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(extracted));
    }

    [Fact]
    public async Task Archive_action_names_multi_selection_after_the_first_file()
    {
        using var workspace = new TestWorkspace("compress");
        var a = workspace.WriteText("sel/alpha.txt", "a");
        var b = workspace.WriteText("sel/beta.txt", "b");
        var folder = Path.Combine(workspace.Root, "sel", "gamma");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inner.txt"), "g");

        var result = await workspace.RunAsync(Op(workspace, "files.compress.zip"), [a, b, folder]);

        var zip = Assert.Single(result.Outputs);
        Assert.Equal("alpha + 2 more.zip", Path.GetFileName(zip));
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("alpha.txt", names);
        Assert.Contains("beta.txt", names);
        Assert.Contains("gamma/inner.txt", names);
    }

    [Fact]
    public async Task Archive_action_single_file_and_each_separately()
    {
        using var workspace = new TestWorkspace("each");
        var a = workspace.WriteText("one/photo.jpg", "not really a jpeg");
        var b = workspace.WriteText("one/clip.mov", "nor a movie");

        var single = await workspace.RunAsync(Op(workspace, "files.compress.zip"), [a]);
        Assert.Equal("photo.zip", Path.GetFileName(Assert.Single(single.Outputs)));

        var each = await workspace.RunAsync(Op(workspace, "files.compress.zip-each"), [a, b]);
        Assert.Equal(["photo (2).zip", "clip.zip"], each.Outputs.Select(p => Path.GetFileName(p)).ToArray());

        var targz = await workspace.RunAsync(Op(workspace, "files.compress.tar-gz"), [a, b]);
        Assert.Equal("photo + 1 more.tar.gz", Path.GetFileName(Assert.Single(targz.Outputs)));
    }

    [Fact]
    public async Task Compress_skips_symbolic_links_and_junctions()
    {
        using var workspace = new TestWorkspace("links");
        var folder = Path.Combine(workspace.Root, "withlink");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "real.txt"), "real");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "loop"), folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // symlinks need Developer Mode or admin rights on Windows
        }

        var result = await workspace.RunAsync(Op(workspace, "folder.zip"), [folder]);

        using var zip = ZipFile.OpenRead(Assert.Single(result.Outputs));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("loop", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("link", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Safety ----------

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData(".. /escape.txt")]
    [InlineData("a/.../../../escape.txt")]
    public async Task Zip_slip_is_rejected_and_nothing_is_written(string evilName)
    {
        using var workspace = new TestWorkspace("slip");
        var dir = ArchivesDir(workspace);
        var zip = Fixtures.Zip(Path.Combine(dir, "evil.zip"), ("ok.txt", "fine"), (evilName, "gotcha"));

        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [zip]));

        Assert.Contains("outside", error.Message);
        Assert.Equal([zip], Directory.GetFileSystemEntries(dir));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "escape.txt")));
    }

    [Fact]
    public async Task Tar_traversal_is_rejected_and_absolute_paths_are_made_relative()
    {
        using var workspace = new TestWorkspace("tarslip");
        var dir = ArchivesDir(workspace);
        var evil = Fixtures.Tar(Path.Combine(dir, "evil.tar"), null, Fixtures.TarFile("../../evil.txt", "x"));
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [evil]));
        Assert.Contains("outside", error.Message);

        var absolute = Fixtures.Tar(Path.Combine(dir, "absolute.tar"), null,
            Fixtures.TarFile("/etc/passwd", "root"), Fixtures.TarFile("C:/Windows/evil.ini", "no"));
        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [absolute]);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("root", File.ReadAllText(Path.Combine(output, "etc", "passwd")));
        Assert.Equal("no", File.ReadAllText(Path.Combine(output, "Windows", "evil.ini")));
        Assert.Contains(result.Warnings, w => w.Contains("absolute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Symlink_and_hardlink_entries_are_skipped()
    {
        using var workspace = new TestWorkspace("symlinks");
        var dir = ArchivesDir(workspace);
        var zip = Fixtures.ZipWithSymlink(Path.Combine(dir, "links.zip"));
        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [zip]);
        var output = Assert.Single(result.Outputs);
        Assert.Equal(Path.Combine(dir, "ok.txt"), output); // the link was skipped, leaving a single file
        Assert.Contains(result.Warnings, w => w.Contains("link", StringComparison.Ordinal));

        var tar = Fixtures.Tar(Path.Combine(dir, "tarlinks.tar"), null,
            Fixtures.TarFile("data/real.txt", "real"),
            Fixtures.TarLink("data/soft", "/etc/passwd"),
            Fixtures.TarLink("data/hard", "data/real.txt", hard: true));
        var tarResult = await workspace.RunAsync(Op(workspace, "archive.extract"), [tar]);
        var data = Assert.Single(tarResult.Outputs);
        Assert.Equal(["real.txt"], Directory.GetFileSystemEntries(data).Select(p => Path.GetFileName(p)).ToArray());
        Assert.Contains(tarResult.Warnings, w => w.StartsWith("2 links", StringComparison.Ordinal));

        var onlyLinks = Fixtures.Tar(Path.Combine(dir, "only.tar"), null, Fixtures.TarLink("x", "/etc/passwd"));
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [onlyLinks]));
        Assert.Contains("only contains links", error.Message);
    }

    [Fact]
    public async Task Expansion_bomb_is_stopped_from_the_table_of_contents()
    {
        using var workspace = new TestWorkspace("bomb");
        var dir = ArchivesDir(workspace);
        var zip = Path.Combine(dir, "zeros.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var stream = archive.CreateEntry("zeros.bin", CompressionLevel.SmallestSize).Open();
            stream.Write(new byte[8 * 1024 * 1024]);
        }
        var limits = new ExtractionLimits { AlwaysAllowedBytes = 1024 * 1024, MaxExpansionRatio = 50 };

        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract", limits), [zip]));

        Assert.Contains("decompression bomb", error.Message);
        Assert.Equal([zip], Directory.GetFileSystemEntries(dir));
        await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.test", limits), [zip]));
    }

    [Fact]
    public async Task Expansion_bomb_is_stopped_while_streaming_a_tar_gz()
    {
        using var workspace = new TestWorkspace("bomb-tgz");
        var dir = ArchivesDir(workspace);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "zeros.bin") { DataStream = new MemoryStream(new byte[8 * 1024 * 1024]) };
        var tgz = Fixtures.Tar(Path.Combine(dir, "zeros.tar.gz"), s => new GZipStream(s, CompressionLevel.SmallestSize, leaveOpen: true), entry);
        var limits = new ExtractionLimits { AlwaysAllowedBytes = 1024 * 1024, MaxExpansionRatio = 50 };

        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract", limits), [tgz]));

        Assert.Contains("decompression bomb", error.Message);
        Assert.Equal([tgz], Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public async Task Entry_count_limit_is_enforced()
    {
        using var workspace = new TestWorkspace("many");
        var entries = Enumerable.Range(0, 30).Select(i => ($"f{i}.txt", (string?)"x")).ToArray();
        var zip = Fixtures.Zip(Path.Combine(ArchivesDir(workspace), "many.zip"), entries);
        var error = await Assert.ThrowsAsync<OperationException>(
            () => workspace.RunAsync(Op(workspace, "archive.extract", new ExtractionLimits { MaxEntries = 10 }), [zip]));
        Assert.Contains("more than 10 entries", error.Message);
    }

    [Fact]
    public async Task Names_windows_cannot_store_are_made_legal_and_case_clashes_kept()
    {
        using var workspace = new TestWorkspace("names");
        var tar = Fixtures.Tar(Path.Combine(ArchivesDir(workspace), "linux.tar"), null,
            Fixtures.TarFile("Readme.txt", "first"),
            Fixtures.TarFile("README.txt", "second"),
            Fixtures.TarFile("CON.txt", "device"),
            Fixtures.TarFile("what?.txt", "question"),
            Fixtures.TarFile("ends with dot.", "dot"),
            Fixtures.TarFile("stream:hidden", "ads"));

        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [tar]);

        var output = Assert.Single(result.Outputs);
        var names = Directory.GetFiles(output).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["README (2).txt", "Readme.txt", "_CON.txt", "ends with dot", "stream_hidden", "what_.txt"], names);
        Assert.Equal("second", File.ReadAllText(Path.Combine(output, "README (2).txt")));
        Assert.Contains(result.Warnings, w => w.Contains("changed to suit Windows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mark_of_the_web_is_copied_to_extracted_files()
    {
        using var workspace = new TestWorkspace("motw");
        var zip = Fixtures.Zip(Path.Combine(ArchivesDir(workspace), "download.zip"), ("a.txt", "a"), ("b.txt", "b"));
        File.WriteAllText(zip + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);

        Assert.Contains("ZoneId=3", File.ReadAllText(Path.Combine(output, "a.txt") + ":Zone.Identifier"));
        Assert.Contains("ZoneId=3", File.ReadAllText(Path.Combine(output, "b.txt") + ":Zone.Identifier"));
    }

    [Fact]
    public async Task Single_top_level_folder_with_the_archive_name_and_a_child_of_the_same_name()
    {
        using var workspace = new TestWorkspace("unwrap");
        var dir = ArchivesDir(workspace);
        var zip = Fixtures.Zip(Path.Combine(dir, "Pack.zip"), ("Pack/a.txt", "a"), ("Pack/Pack/b.txt", "b"));

        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);

        Assert.Equal(Path.Combine(dir, "Pack"), output);
        Assert.Equal("a", File.ReadAllText(Path.Combine(output, "a.txt")));
        Assert.Equal("b", File.ReadAllText(Path.Combine(output, "Pack", "b.txt")));
    }

    [Fact]
    public async Task Several_top_level_items_go_in_a_folder_named_after_the_archive_and_never_overwrite()
    {
        using var workspace = new TestWorkspace("multi");
        var dir = ArchivesDir(workspace);
        var zip = Fixtures.Zip(Path.Combine(dir, "bundle.zip"), ("one.txt", "1"), ("two/three.txt", "3"));
        Directory.CreateDirectory(Path.Combine(dir, "bundle"));

        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);

        Assert.Equal(Path.Combine(dir, "bundle (2)"), output);
        Assert.Equal("1", File.ReadAllText(Path.Combine(output, "one.txt")));
        Assert.Equal("3", File.ReadAllText(Path.Combine(output, "two", "three.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(dir, "bundle")));
    }

    // ---------- Passwords ----------

    [Theory]
    [InlineData("zip", "-mem=ZipCrypto")]
    [InlineData("zip", "-mem=AES256")]
    [InlineData("7z", "-mhe=off")]
    [InlineData("7z", "-mhe=on")]
    public async Task Password_protected_archives(string type, string method)
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("pw-" + type);
        var source = Fixtures.SourceTree(workspace, "Secret");
        var dir = ArchivesDir(workspace);
        var archive = Path.Combine(dir, $"locked.{type}");
        await Fixtures.SevenZipAsync(Path.GetDirectoryName(source)!, "a", $"-t{type}", "-pS3cret pass", method, "-y", archive, "Secret");

        var missing = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [archive]));
        Assert.Contains("password-protected", missing.Message);
        Assert.Contains("With password", missing.Message);

        var wrong = await Assert.ThrowsAsync<OperationException>(
            () => workspace.RunAsync(Op(workspace, "archive.extract.password"), [archive], new Dictionary<string, object?> { ["password"] = "nope" }));
        Assert.Contains("password", wrong.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([archive], Directory.GetFileSystemEntries(dir));

        var result = await workspace.RunAsync(Op(workspace, "archive.extract.password"), [archive], new Dictionary<string, object?> { ["password"] = "S3cret pass" });
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single(result.Outputs)));

        var tested = await workspace.RunAsync(Op(workspace, "archive.test.password"), [archive], new Dictionary<string, object?> { ["password"] = "S3cret pass" });
        Assert.Contains("intact", tested.Text);
        await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.test"), [archive]));
    }

    /// <summary>
    /// Regression for the flaky Password_protected_archives(7z, -mhe=off): not a race. 7z has no password check (and
    /// ZipCrypto lets one wrong password in 256 past its check byte), so a wrong password hands the decoder random
    /// bytes — and 7-Zip writes a new random IV into every archive, so the same wrong password gives different bytes
    /// each run. About one in 20 used to surface as "damaged", a size/CRC mismatch or a raw NullReferenceException
    /// instead of a password message. Many wrong passwords against one archive walk through all those paths (each 7z
    /// attempt costs ~0.1 s of key derivation, hence fewer of them: 64 miss every bad path only ~3 % of the time).
    /// </summary>
    [Theory]
    [InlineData("7z", "-mhe=off", 64)]
    [InlineData("7z", "-mhe=on", 16)]
    [InlineData("zip", "-mem=ZipCrypto", 1500)]
    public async Task Every_wrong_password_is_reported_as_a_password_problem(string type, string method, int attempts)
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("pw-many");
        var source = Fixtures.SourceTree(workspace, "Secret");
        var archive = Path.Combine(ArchivesDir(workspace), $"locked.{type}");
        await Fixtures.SevenZipAsync(Path.GetDirectoryName(source)!, "a", $"-t{type}", "-pS3cret pass", method, "-y", archive, "Secret");
        var input = InputFile.FromPath(archive);
        var limits = new ExtractionLimits();
        var failures = new List<string>();

        for (var i = 0; i < attempts; i++)
        {
            var password = $"wrong {i}";
            var destination = Path.Combine(workspace.Root, "out", $"{i}");
            var extract = await Record.ExceptionAsync(() => ArchiveExtraction.ExtractAsync(
                workspace.Context, archive, destination, new ExtractOptions(input.Length, password), limits, new Progress<JobProgress>(), default));
            var test = await Record.ExceptionAsync(() => ArchiveExtraction.TestAsync(
                workspace.Context, input, new ExtractOptions(input.Length, password, PasswordHint: ArchiveErrors.TestHint), limits, new Progress<JobProgress>(), default));
            foreach (var (what, error) in new[] { ("extract", extract), ("test", test) })
            {
                if (error?.GetType() != typeof(OperationException) || !error.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                    failures.Add($"{what} \"{password}\": {error?.GetType().Name ?? "no error"}: {error?.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} wrong passwords not reported as such:\n{string.Join("\n", failures.Take(20))}");
    }

    [Fact]
    public async Task Encrypted_archives_sharpcompress_can_decode_never_reach_7zip()
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("pw-sc");
        var source = Fixtures.SourceTree(workspace, "Secret");
        var options = new Dictionary<string, object?> { ["password"] = "S3cret pass" };
        (string Type, string[] Switches, bool NeedsSevenZip)[] cases =
        [
            ("zip", ["-mem=ZipCrypto"], false),
            ("zip", ["-mem=AES128"], false),
            ("zip", ["-mem=AES256", "-mm=Deflate64"], false),
            ("zip", ["-mem=AES256", "-mm=BZip2"], false),
            ("zip", ["-mem=AES256", "-mm=PPMd"], false),
            ("7z", ["-mhe=on"], false),
            ("7z", ["-mhe=off", "-ms=off"], false),
            ("7z", ["-m0=PPMd"], false),
            ("7z", ["-m0=BZip2"], false),
            ("7z", ["-mf=BCJ2"], false),
            // Only methods SharpCompress can't decode go to 7-Zip (and put the password on its command line).
            ("zip", ["-mem=AES256", "-mm=LZMA"], true),
            ("7z", ["-mhe=on", "-m0=Deflate64"], true),
        ];

        foreach (var (i, (type, switches, needsSevenZip)) in cases.Index())
        {
            var label = $"{type} {string.Join(' ', switches)}";
            var archive = Path.Combine(ArchivesDir(workspace), $"case{i}.{type}");
            await Fixtures.SevenZipAsync(Path.GetDirectoryName(source)!, ["a", $"-t{type}", "-pS3cret pass", .. switches, "-y", archive, "Secret"]);

            var before = workspace.Log.Lines.Count;
            var extracted = await workspace.RunAsync(Op(workspace, "archive.extract.password"), [archive], options);
            Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single(extracted.Outputs)));
            var extractedWith7Zip = workspace.Log.Lines.Skip(before).Any(l => l.Contains("Retrying with 7-Zip", StringComparison.Ordinal));
            Assert.True(needsSevenZip == extractedWith7Zip, $"{label}: extracted with 7-Zip = {extractedWith7Zip}");

            var tested = await workspace.RunAsync(Op(workspace, "archive.test.password"), [archive], options);
            Assert.True(needsSevenZip == tested.Text!.Contains("checked with 7-Zip", StringComparison.Ordinal), $"{label}: {tested.Text}");
        }
    }

    [Fact]
    public async Task Seven_zip_is_given_the_password_only_when_the_archive_needs_it()
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("pw-7z");
        var source = Fixtures.SourceTree(workspace, "Secret");
        var dir = ArchivesDir(workspace);
        var parent = Path.GetDirectoryName(source)!;
        // Deflate64 inside 7z: SharpCompress can't decode it, so all three need 7-Zip.
        var plain = Path.Combine(dir, "plain.7z");
        var locked = Path.Combine(dir, "locked.7z");
        var hidden = Path.Combine(dir, "hidden.7z");
        await Fixtures.SevenZipAsync(parent, "a", "-t7z", "-m0=Deflate64", "-y", plain, "Secret");
        await Fixtures.SevenZipAsync(parent, "a", "-t7z", "-m0=Deflate64", "-pS3cret pass", "-mhe=off", "-y", locked, "Secret");
        await Fixtures.SevenZipAsync(parent, "a", "-t7z", "-m0=Deflate64", "-pS3cret pass", "-mhe=on", "-y", hidden, "Secret");

        Assert.Null(SevenZipFallback.PasswordFor(plain, "S3cret pass"));       // nothing encrypted: stays off the command line
        Assert.Equal("S3cret pass", SevenZipFallback.PasswordFor(locked, "S3cret pass")); // encrypted entries
        Assert.Equal("S3cret pass", SevenZipFallback.PasswordFor(hidden, "S3cret pass")); // encrypted headers: can't tell, so needed
        Assert.Null(SevenZipFallback.PasswordFor(locked, null));
        Assert.Null(SevenZipFallback.PasswordFor(locked, ""));

        // "With password…" on an archive without one still works through 7-Zip …
        var options = new Dictionary<string, object?> { ["password"] = "S3cret pass" };
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract.password"), [plain], options)).Outputs)));
        Assert.Contains("checked with 7-Zip", (await workspace.RunAsync(Op(workspace, "archive.test.password"), [plain], options)).Text);
        // … and archives that need the password get it.
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract.password"), [locked], options)).Outputs)));
        var wrong = await Assert.ThrowsAsync<OperationException>(
            () => workspace.RunAsync(Op(workspace, "archive.extract.password"), [locked], new Dictionary<string, object?> { ["password"] = "nope" }));
        Assert.Contains("password", wrong.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_password_is_refused_politely()
    {
        using var workspace = new TestWorkspace("nopw");
        var zip = Fixtures.Zip(workspace.Path("a.zip"), ("a.txt", "a"));
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract.password"), [zip]));
        Assert.Equal("Enter the archive's password.", error.Message);
    }

    // ---------- Damage and odd inputs ----------

    [Fact]
    public async Task Corrupted_entry_fails_its_crc_check()
    {
        using var workspace = new TestWorkspace("crc");
        var dir = ArchivesDir(workspace);
        var zip = Path.Combine(dir, "damaged.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("story.txt", CompressionLevel.NoCompression).Open());
            writer.Write("Once upon a time there was a perfectly good file.");
        }
        var bytes = File.ReadAllBytes(zip);
        var at = Encoding.ASCII.GetString(bytes).IndexOf("perfectly", StringComparison.Ordinal);
        bytes[at] = (byte)'P';
        File.WriteAllBytes(zip, bytes);

        var test = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.test"), [zip]));
        Assert.Contains("damaged", test.Message);

        var extract = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [zip]));
        Assert.Contains("CRC", extract.Message);
        Assert.Equal([zip], Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public async Task Truncated_tar_gz_fails_cleanly()
    {
        using var workspace = new TestWorkspace("trunc");
        var dir = ArchivesDir(workspace);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, "big.bin") { DataStream = new MemoryStream(Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 % 251)).ToArray()) };
        var tgz = Fixtures.Tar(Path.Combine(dir, "cut.tar.gz"), Fixtures.Gzip, entry);
        var bytes = File.ReadAllBytes(tgz);
        File.WriteAllBytes(tgz, bytes[..(bytes.Length / 2)]);

        await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [tgz]));
        Assert.Equal([tgz], Directory.GetFileSystemEntries(dir));
        await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.test"), [tgz]));
    }

    [Fact]
    public async Task Not_an_archive_and_empty_files_get_friendly_errors()
    {
        using var workspace = new TestWorkspace("bogus");
        var fake = workspace.WriteText("fake.zip", "this is just text pretending to be a zip");
        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [fake]));
        Assert.Contains("doesn't look like a valid ZIP", error.Message);

        var empty = workspace.WriteText("empty.7z", "");
        var emptyError = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.list"), [empty]));
        Assert.Contains("empty", emptyError.Message);

        var emptyZip = Fixtures.Zip(workspace.Path("nothing.zip"));
        var nothing = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [emptyZip]));
        Assert.Contains("empty", nothing.Message);
    }

    [Fact]
    public async Task List_contents_describes_entries()
    {
        using var workspace = new TestWorkspace("list");
        var zip = Fixtures.Zip(workspace.Path("photos.zip"), ("Trip/", null), ("Trip/day1.jpg", "jpeg?"), ("Trip/day2.jpg", "jpeg!"));
        var result = await workspace.RunAsync(Op(workspace, "archive.list"), [zip]);
        Assert.Equal("Contents of photos.zip", result.TextTitle);
        Assert.Contains("ZIP archive, 2 files, 1 folder", result.Text);
        Assert.Contains("Trip/day1.jpg", result.Text);
        Assert.Contains("Trip/day2.jpg", result.Text);

        var tgz = Fixtures.Tar(workspace.Path("code.tar.gz"), Fixtures.Gzip, Fixtures.TarFile("src/main.c", "int main;"), Fixtures.TarLink("src/link", "main.c"));
        var tarList = await workspace.RunAsync(Op(workspace, "archive.list"), [tgz]);
        Assert.Contains("TAR.GZ archive", tarList.Text);
        Assert.Contains("src/main.c", tarList.Text);
        Assert.Contains("link, not extracted", tarList.Text);
    }

    [Fact]
    public async Task Cancelled_extraction_leaves_nothing_behind()
    {
        using var workspace = new TestWorkspace("cancel");
        var dir = ArchivesDir(workspace);
        var zip = Fixtures.Zip(Path.Combine(dir, "a.zip"), ("a.txt", "a"), ("b.txt", "b"));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.RunAsync(Op(workspace, "archive.extract"), [zip], cancellationToken: cancel.Token));

        Assert.Equal([zip], Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public void Seven_zip_operations_report_availability()
    {
        using var workspace = new TestWorkspace("avail");
        var installed = workspace.Context.Tools.SevenZip is not null;
        foreach (var id in new[] { "folder.7z", "archive.7z", "files.compress.7z" })
        {
            var availability = Op(workspace, id).CheckAvailability();
            Assert.Equal(installed, availability.IsAvailable);
            if (!installed)
                Assert.Contains("7-Zip", availability.Reason);
        }
        Assert.True(Op(workspace, "folder.zip").CheckAvailability().IsAvailable);
        Assert.True(Op(workspace, "archive.extract").CheckAvailability().IsAvailable);
    }
}
