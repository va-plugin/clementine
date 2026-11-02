using System.IO.Compression;
using System.Text;
using Clementine.Core;
using Clementine.Engines.Files.Archives;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

public class ArchiveEdgeCaseTests
{
    static IOperation Op(TestWorkspace workspace, string id) => workspace.Operation(new FilesOperationProvider(), id);

    /// <summary>Synchronous progress sink (the BCL's Progress&lt;T&gt; posts asynchronously).</summary>
    sealed class Recorder(Action<JobProgress>? onReport = null) : IProgress<JobProgress>
    {
        public List<JobProgress> Reports { get; } = [];

        public void Report(JobProgress value)
        {
            lock (Reports)
                Reports.Add(value);
            onReport?.Invoke(value);
        }
    }

    [Fact]
    public async Task Zip_names_without_the_utf8_flag_are_read_with_the_oem_code_page()
    {
        using var workspace = new TestWorkspace("oem");
        var zip = workspace.Path("old/legacy.zip");
        using (var archive = new ZipArchive(File.Create(zip), ZipArchiveMode.Create, leaveOpen: false, Encoding.GetEncoding(437)))
        {
            using var writer = new StreamWriter(archive.CreateEntry("Übersicht.txt").Open());
            writer.Write("old school");
        }

        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);

        Assert.Equal("Übersicht.txt", Path.GetFileName(output));
    }

    [Fact]
    public async Task Modification_times_are_restored()
    {
        using var workspace = new TestWorkspace("mtime");
        var zip = workspace.Path("t/dated.zip");
        var stamp = new DateTime(2019, 6, 15, 10, 20, 30, DateTimeKind.Local);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("a.txt");
            entry.LastWriteTime = stamp;
            using (var writer = new StreamWriter(entry.Open()))
                writer.Write("a");
            archive.CreateEntry("b.txt").LastWriteTime = stamp;
        }

        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);

        Assert.InRange((File.GetLastWriteTime(Path.Combine(output, "a.txt")) - stamp).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(0, new FileInfo(Path.Combine(output, "b.txt")).Length);
    }

    [Fact]
    public async Task Cancelling_mid_extraction_removes_the_partial_folder()
    {
        using var workspace = new TestWorkspace("midcancel");
        var dir = Path.Combine(workspace.Root, "arc");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, "big.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            for (var i = 0; i < 200; i++)
            {
                using var stream = archive.CreateEntry($"part{i:000}.bin", CompressionLevel.Fastest).Open();
                stream.Write(new byte[64 * 1024]);
            }
        }
        using var cancel = new CancellationTokenSource();
        var progress = new Recorder(p =>
        {
            if (p.Fraction is > 0)
                cancel.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => workspace.RunAsync(Op(workspace, "archive.extract"), [zip], progress: progress, cancellationToken: cancel.Token));

        Assert.Equal(["big.zip"], Directory.GetFileSystemEntries(dir).Select(p => Path.GetFileName(p)).ToArray());
    }

    [Fact]
    public async Task Progress_is_reported_while_extracting_and_compressing()
    {
        using var workspace = new TestWorkspace("progress");
        var folder = Path.Combine(workspace.Root, "many");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 300; i++)
            File.WriteAllBytes(Path.Combine(folder, $"f{i:000}.bin"), Enumerable.Range(0, 40_000).Select(b => (byte)(b * i)).ToArray());

        var compress = new Recorder();
        var zip = Assert.Single((await workspace.RunAsync(Op(workspace, "folder.zip"), [folder], progress: compress)).Outputs);
        Assert.Contains(compress.Reports, r => r.Fraction is > 0.5 and <= 1);

        var extract = new Recorder();
        await workspace.RunAsync(Op(workspace, "archive.extract"), [zip], progress: extract);
        Assert.Contains(extract.Reports, r => r.Fraction is > 0.5 and < 1);
        Assert.Equal(1, extract.Reports[^1].Fraction);
    }

    [Fact]
    public async Task Seven_zip_compress_handles_inputs_from_different_folders()
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("7z-roots");
        var a = workspace.WriteText("left/alpha.txt", "a");
        var b = workspace.WriteText("right/deep/beta.txt", "b");

        var archive = Assert.Single((await workspace.RunAsync(Op(workspace, "files.compress.7z"), [a, b])).Outputs);
        Assert.Equal("alpha + 1 more.7z", Path.GetFileName(archive));

        var list = await workspace.RunAsync(Op(workspace, "archive.list"), [archive]);
        Assert.Contains("alpha.txt", list.Text);
        Assert.Contains("beta.txt", list.Text);
        Assert.DoesNotContain("deep", list.Text);
    }

    [Fact]
    public async Task Same_named_inputs_from_different_folders_are_kept_apart_in_a_zip()
    {
        using var workspace = new TestWorkspace("dupes");
        var a = workspace.WriteText("one/notes.txt", "first");
        var b = workspace.WriteText("two/notes.txt", "second");

        var zip = Assert.Single((await workspace.RunAsync(Op(workspace, "files.compress.zip"), [a, b])).Outputs);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["notes (2).txt", "notes.txt"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Hidden_files_are_archived_but_partial_outputs_are_not()
    {
        using var workspace = new TestWorkspace("hidden");
        var folder = Path.Combine(workspace.Root, "proj");
        Directory.CreateDirectory(folder);
        var hidden = Path.Combine(folder, ".gitignore");
        File.WriteAllText(hidden, "bin/");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        File.WriteAllText(Path.Combine(folder, ".proj.clementine-0123abcd.partial.zip"), "in progress");

        var zip = Assert.Single((await workspace.RunAsync(Op(workspace, "folder.zip"), [folder])).Outputs);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["proj/", "proj/.gitignore"], archive.Entries.Select(e => e.FullName).ToArray());
    }

    [Fact]
    public async Task Tar_writer_round_trips_long_and_unicode_names_through_system_tar()
    {
        using var workspace = new TestWorkspace("longnames");
        var folder = Path.Combine(workspace.Root, "Ü");
        var deep = Path.Combine(folder, new string('d', 120), "東京 – " + new string('n', 110) + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        File.WriteAllText(deep, "long");

        var tgz = Assert.Single((await workspace.RunAsync(Op(workspace, "folder.tar-gz"), [folder])).Outputs);

        var check = Path.Combine(workspace.Root, "check");
        Directory.CreateDirectory(check);
        await using (var file = File.OpenRead(tgz))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            System.Formats.Tar.TarFile.ExtractToDirectory(gzip, check, overwriteFiles: false);
        Assert.Equal("long", File.ReadAllText(Path.Combine(check, Path.GetRelativePath(workspace.Root, deep))));
    }

    [Fact]
    public async Task Methods_sharpcompress_cannot_decode_fall_back_to_7zip_safely()
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("fallback");
        var source = Fixtures.SourceTree(workspace);
        var dir = Path.Combine(workspace.Root, "arc");
        Directory.CreateDirectory(dir);
        var archive = Path.Combine(dir, "deflate64.7z");
        await Fixtures.SevenZipAsync(Path.GetDirectoryName(source)!, "a", "-t7z", "-m0=Deflate64", "-y", archive, Path.GetFileName(source));
        File.WriteAllText(archive + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        var output = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [archive])).Outputs);
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(output));
        Assert.Contains("ZoneId=3", File.ReadAllText(Path.Combine(output, "readme.txt") + ":Zone.Identifier"));

        var test = await workspace.RunAsync(Op(workspace, "archive.test"), [archive]);
        Assert.Contains("checked with 7-Zip", test.Text);

        var zip = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.zip"), [archive])).Outputs);
        var again = Assert.Single((await workspace.RunAsync(Op(workspace, "archive.extract"), [zip])).Outputs);
        Assert.Equal(Fixtures.Snapshot(source), Fixtures.Snapshot(again));

        // The same safety limits apply to the fallback.
        var limited = workspace.Operation(new FilesOperationProvider(new ExtractionLimits { AlwaysAllowedBytes = 100, MaxExpansionRatio = 0.01 }), "archive.extract");
        var bomb = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(limited, [archive]));
        Assert.Contains("bomb", bomb.Message);
    }

    [Fact]
    public void Extractor_refuses_to_run_out_of_entries_or_space_budget_in_isolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ClementineTests", $"extractor-{Guid.NewGuid():N}");
        try
        {
            var zip = Fixtures.Zip(Path.Combine(root, "in.zip"), ("a.txt", new string('x', 10_000)));
            using var reader = ArchiveReaders.Open(zip);
            var limits = new ExtractionLimits { AlwaysAllowedBytes = 1000, MaxExpansionRatio = 1 };
            var error = Assert.Throws<OperationException>(() =>
                new ArchiveExtractor(limits).Extract(reader, Path.Combine(root, "out"), new ExtractOptions(new FileInfo(zip).Length), null, default));
            Assert.Contains("bomb", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
