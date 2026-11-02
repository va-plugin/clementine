using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Files.Archives;
using Clementine.Engines.Files.Data;
using Clementine.Engines.Files.Subtitles;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

/// <summary>Regression tests for defects found in the adversarial review.</summary>
public class ReviewFixTests
{
    static IOperation Op(TestWorkspace workspace, string id) => workspace.Operation(new FilesOperationProvider(), id);

    // ---------- Subtitles ----------

    [Fact]
    public void Srt_cue_with_an_impossible_timestamp_is_skipped_not_glued_onto_the_previous_cue()
    {
        var text = "1\n00:00:01,000 --> 00:00:02,000\nFirst\n\n" +
                   "2\n00:00:75,000 --> 00:00:76,000\nBroken\n\n" +
                   "3\n00:00:05,000 --> 00:00:06,000\nThird\n";
        var doc = SubtitleParser.Parse(text, SubtitleFormat.Srt);
        Assert.Equal(2, doc.Cues.Count);
        Assert.Equal("First", doc.Cues[0].Text);
        Assert.Equal("Third", doc.Cues[1].Text);
        Assert.Contains(doc.Warnings, w => w.Contains("timestamp", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Data ----------

    [Fact]
    public void Yaml_complex_keys_built_from_aliases_share_one_size_budget()
    {
        // ~1 KB of YAML whose complex keys would each expand to ~700 KB of JSON text.
        var yaml = new StringBuilder("a: &a [lol, lol, lol, lol, lol, lol, lol, lol, lol, lol]\n");
        for (var level = 'b'; level <= 'e'; level++)
            yaml.Append($"{level}: &{level} [{string.Join(", ", Enumerable.Repeat("*" + (char)(level - 1), 10))}]\n");
        yaml.Append("keys:\n");
        for (var i = 0; i < 3; i++)
            yaml.Append($"  ? [*e, {i}]\n  : {i}\n");

        var error = Assert.Throws<OperationException>(() => YamlReader.Read(yaml.ToString()));
        Assert.Contains("enormous", error.Message);
    }

    [Fact]
    public void Yaml_huge_integers_do_not_burn_cpu_and_small_ones_still_normalise()
    {
        var digits = new string('7', 200_000);
        var watch = Stopwatch.StartNew();
        var (octal, _) = YamlReader.Read($"big: 0o{digits}\nhex: 0x{digits}\ndec: +{digits}\n");
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}"); // 15 s before the fix
        var obj = Assert.IsType<DataObject>(octal);
        Assert.Equal(digits, Assert.IsType<DataNumber>(obj.Properties.Single(p => p.Key == "dec").Value).Raw);

        var (small, _) = YamlReader.Read("a: +0042\nb: 0x1F\nc: 0o17\nd: -0\ne: -007\n");
        var json = JsonTextWriter.Write(small, indent: false);
        Assert.Equal("{\"a\":42,\"b\":31,\"c\":15,\"d\":0,\"e\":-7}", json);
    }

    [Fact]
    public void Yaml_mapping_with_many_keys_converts_in_linear_time()
    {
        // Before the fix this was quadratic: 150 000 keys took 8 minutes, so 60 000 would take well over one.
        const int count = 60_000;
        var yaml = new StringBuilder();
        for (var i = 0; i < count; i++)
            yaml.Append("key").Append(i.ToString("000000")).Append(": ").Append(i).Append('\n');
        yaml.Append("key000007: last wins\n");

        var watch = Stopwatch.StartNew();
        var (data, warnings) = YamlReader.Read(yaml.ToString());
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        var obj = Assert.IsType<DataObject>(data);
        Assert.Equal(count, obj.Properties.Count);
        Assert.Equal("last wins", Assert.IsType<DataString>(obj.Get("key000007")).Value);
        Assert.Equal("key000007", obj.Properties[7].Key);
        Assert.Contains(warnings, w => w.Contains("Duplicate key", StringComparison.Ordinal));
    }

    // ---------- Compress ----------

    [Fact]
    public async Task Tar_stays_valid_when_a_file_grows_while_it_is_being_archived()
    {
        using var workspace = new TestWorkspace("growing");
        var folder = Path.Combine(workspace.Root, "Recording");
        Directory.CreateDirectory(folder);
        var growing = Path.Combine(folder, "a-growing.bin");
        File.WriteAllBytes(growing, new byte[10 << 20]);
        File.WriteAllText(Path.Combine(folder, "z-after.txt"), "after");

        var appended = false;
        var progress = new SyncProgress(p =>
        {
            if (appended || p.Status != "Compressing…")
                return;
            appended = true;
            using var append = new FileStream(growing, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            append.Write(new byte[1 << 20]);
        });

        var result = await workspace.RunAsync(Op(workspace, "folder.tar-gz"), [folder], progress: progress);
        Assert.True(appended);
        var archive = Assert.Single(result.Outputs);

        var names = new Dictionary<string, long>();
        using (var file = File.OpenRead(archive))
        using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (tar.GetNextEntry() is { } entry)
                names[entry.Name] = entry.Length;
        }
        Assert.Equal(10 << 20, names["Recording/a-growing.bin"]);
        Assert.Equal(5, names["Recording/z-after.txt"]);
        Assert.Contains(result.Warnings, w => w.Contains("changed while", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("folder.7z")]
    [InlineData("folder.zip")]
    [InlineData("folder.tar-gz")]
    public async Task Names_starting_with_a_space_are_not_dropped_from_archives(string id)
    {
        using var workspace = new TestWorkspace("lead-space");
        var operation = Op(workspace, id);
        if (!operation.CheckAvailability().IsAvailable)
            return;
        var folder = Path.Combine(workspace.Root, " Set"); // 7-Zip list files start with the top-level name
        Directory.CreateDirectory(Path.Combine(folder, " sub"));
        File.WriteAllText(Path.Combine(folder, " lead.txt"), "lead");
        File.WriteAllText(Path.Combine(folder, " sub", " deep.txt"), "deep");
        File.WriteAllText(Path.Combine(folder, "-dash;semi.txt"), "dash");

        var result = await workspace.RunAsync(operation, [folder]);
        Assert.Empty(result.Warnings);
        var extracted = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [Assert.Single(result.Outputs)])).Outputs);
        Assert.Equal(Fixtures.Snapshot(folder), Fixtures.Snapshot(extracted));
    }

    [Fact]
    public async Task Zip_with_more_than_65535_entries_is_written_as_zip64_and_reads_back()
    {
        using var workspace = new TestWorkspace("zip64");
        var source = workspace.WriteText("src/one.txt", "one");
        var items = new CollectedItems();
        const int folders = 70_000;
        for (var i = 0; i < folders; i++)
            items.Items.Add(new ArchiveItem(Path.GetDirectoryName(source)!, $"d{i:00000}/", IsDirectory: true, 0, DateTime.UtcNow));
        items.Items.Add(new ArchiveItem(source, "d00000/one.txt", IsDirectory: false, 3, DateTime.UtcNow));
        items.TotalBytes = 3;
        items.FileCount = 1;

        var destination = workspace.Path("many.zip");
        await ArchiveWriters.WriteAsync(OutputArchiveFormat.Zip, items, destination, workspace.Context, workspace.Path("_tmp"), new Progress<JobProgress>(), default);

        using (var zip = ZipFile.OpenRead(destination))
            Assert.Equal(folders + 1, zip.Entries.Count);
        using var reader = ArchiveReaders.Open(destination);
        Assert.Equal(folders + 1, reader.TableOfContents!.Count);
    }

    [Theory]
    [InlineData("folder.zip")]
    [InlineData("folder.tar-gz")]
    [InlineData("folder.7z")]
    public async Task Cancelling_compression_midway_leaves_no_partial_archive(string id)
    {
        using var workspace = new TestWorkspace("cancel-compress");
        var operation = Op(workspace, id);
        if (!operation.CheckAvailability().IsAvailable)
            return;
        var parent = Path.Combine(workspace.Root, "parent");
        var folder = Path.Combine(parent, "Big");
        Directory.CreateDirectory(folder);
        var noise = new byte[24 << 20];
        new Random(7).NextBytes(noise);
        File.WriteAllBytes(Path.Combine(folder, "noise.bin"), noise);

        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Fraction is > 0 and < 1)
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.RunAsync(operation, [folder], progress: progress, cancellationToken: cancel.Token));
        Assert.True(cancel.IsCancellationRequested);
        for (var attempt = 0; attempt < 50 && Directory.GetFileSystemEntries(parent).Length > 1; attempt++)
            await Task.Delay(100); // a killed 7-Zip may take a moment to release its file
        Assert.Equal([folder], Directory.GetFileSystemEntries(parent));
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("archive.tar-gz")]
    public async Task Repacked_archives_keep_the_mark_of_the_web(string id)
    {
        using var workspace = new TestWorkspace("repack-motw");
        var source = Fixtures.Tar(workspace.Path("arc/download.tar"), null, Fixtures.TarFile("setup.exe", "MZ"));
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var output = Assert.Single((await workspace.RunAsync(Op(workspace, id), [source])).Outputs);
        Assert.Contains("ZoneId=3", File.ReadAllText(output + ":Zone.Identifier"));
    }

    [Fact]
    public async Task Single_extracted_file_keeps_the_case_of_its_extension()
    {
        using var workspace = new TestWorkspace("ext-case");
        var zip = Fixtures.Zip(workspace.Path("arc/Shot.zip"), ("Photo.JPG", "jpeg"));
        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);
        Assert.Equal("Photo.JPG", Path.GetFileName(output));
        Assert.Contains("Photo.JPG", Directory.GetFiles(Path.GetDirectoryName(output)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Entries_that_alias_an_earlier_one_through_an_8dot3_short_name_are_kept_apart()
    {
        using var workspace = new TestWorkspace("short-names");
        var zip = Fixtures.Zip(workspace.Path("arc/aliases.zip"),
            ("verylongfilename.txt", "long"),
            ("VERYLO~1.TXT", "short"),
            ("longdirectoryname/a.txt", "dir-long"),
            ("LONGDI~1/a.txt", "dir-short"));
        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);
        var contents = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Select(File.ReadAllText).Order().ToList();
        Assert.Equal(["dir-long", "dir-short", "long", "short"], contents);
        Assert.Equal("long", File.ReadAllText(Path.Combine(output, "verylongfilename.txt")));
        Assert.Equal("dir-long", File.ReadAllText(Path.Combine(output, "longdirectoryname", "a.txt")));
    }

    [Fact]
    public void Bidirectional_override_characters_cannot_disguise_extracted_names()
    {
        var path = EntryPathSanitizer.Sanitize("docs/‮gnp.exe");
        Assert.True(path.WasRenamed);
        Assert.Equal("_gnp.exe", path.Segments[^1]);
        Assert.Equal("a‍b.txt", EntryPathSanitizer.Sanitize("a‍b.txt").Segments[0]); // ZWJ (emoji sequences) is fine
    }

    [Fact]
    public void Absurd_declared_sizes_do_not_overflow_list_or_test()
    {
        const long half = long.MaxValue / 2 + 10;
        // What the inspector used to do: LINQ's long Sum is checked and throws, which surfaced as a raw crash.
        Assert.Throws<OverflowException>(() => new[] { half, half }.Sum());

        using var reader = new FakeReader(
            new ArchiveEntry { Path = "a.bin", Kind = ArchiveEntryKind.File, Size = half, PackedSize = half },
            new ArchiveEntry { Path = "b.bin", Kind = ArchiveEntryKind.File, Size = half, PackedSize = half });
        var listing = ArchiveInspector.List(reader, "huge.zip", default);
        Assert.Contains("a.bin", listing);
        var report = ArchiveInspector.Test(reader, new ExtractOptions(100), ExtractionLimits.Default, null, default);
        Assert.Equal(0, report.Files);
    }

    sealed class FakeReader(params ArchiveEntry[] entries) : IArchiveReader
    {
        public ArchiveFormat Format => ArchiveFormat.Zip;

        public bool IsStreamed => false;

        public IReadOnlyList<ArchiveEntry>? TableOfContents => entries;

        public double Progress => 0;

        public void ReadAll(Func<ArchiveEntry, bool> wantData, Action<ArchiveEntry, Stream?> visit, CancellationToken cancellationToken)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Seven_zip_archives_with_empty_files_extract_test_list_and_repack()
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("7z-empty");
        var source = Path.Combine(workspace.Root, "src", "Pkg");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllBytes(Path.Combine(source, "__init__.py"), []);
        File.WriteAllText(Path.Combine(source, "main.py"), "print('hi')\n");
        File.WriteAllBytes(Path.Combine(source, "sub", "empty.txt"), []);
        File.WriteAllText(Path.Combine(source, "sub", "data.txt"), new string('d', 5000));
        var archive = workspace.Path("arc/Pkg.7z");
        await Fixtures.SevenZipAsync(Path.GetDirectoryName(source)!, "a", "-t7z", "-y", archive, "Pkg");

        using (var reader = ArchiveReaders.Open(archive))
        {
            var encrypted = reader.TableOfContents!.Where(e => e.IsEncrypted).Select(e => e.Path).ToList();
            Assert.True(encrypted.Count == 0, "reported as encrypted: " + string.Join(", ", encrypted));
        }

        OperationResult extracted;
        try
        {
            extracted = await workspace.RunAsync(Op(workspace, "archive.extract"), [archive]);
        }
        catch (OperationException ex)
        {
            throw new Xunit.Sdk.XunitException($"{ex.Message}\n{ex.Details}\n{ex.InnerException}");
        }
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single(extracted.Outputs)));
        Assert.Contains("intact", (await workspace.RunAsync(Op(workspace, "archive.test"), [archive])).Text);
        Assert.Contains("__init__.py", (await workspace.RunAsync(Op(workspace, "archive.list"), [archive])).Text);
        var zip = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.zip"), [archive])).Outputs);
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs)));
        // Clementine's own .7z output must be readable by Clementine.
        var own = Assert.Single((await workspace.RunAsync(Op(workspace, "folder.7z"), [source])).Outputs);
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [own])).Outputs)));
    }

    // ---------- 7-Zip fallback ----------

    [Fact]
    public void Seven_zip_fallback_never_lets_7zip_create_links()
    {
        var arguments = SevenZipFallback.ExtractArguments(@"C:\out", null, @"C:\in\a.7z");
        Assert.Contains("-snl-", arguments);
        Assert.Contains("-snh-", arguments);
        Assert.Contains("-aou", arguments);
        Assert.Equal(@"C:\in\a.7z", arguments[^1]);
        Assert.Equal("--", arguments[^2]);
    }

    sealed class SyncProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }
}
