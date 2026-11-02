using Clementine.Core;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

/// <summary>
/// The first review pass's probes (they only printed what happened), turned into assertions. Its YAML bomb probes
/// are covered by <see cref="ReviewFixTests"/> and DataTests, and the extension-case probe by
/// <see cref="ReviewFixTests.Single_extracted_file_keeps_the_case_of_its_extension"/>.
/// </summary>
public class ReviewProbeTests
{
    static IOperation Op(TestWorkspace workspace, string id) => workspace.Operation(new FilesOperationProvider(), id);

    [Fact]
    public async Task File_locked_by_another_program_stops_compression_with_a_friendly_message_and_no_partial_zip()
    {
        using var workspace = new TestWorkspace("locked");
        var folder = Path.Combine(workspace.Root, "proj");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "a");
        var locked = Path.Combine(folder, "b.psd");
        File.WriteAllText(locked, "b");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "folder.zip"), [folder]));
        Assert.Equal("\"b.psd\" is open in another program that won't let other apps read it. Close it there and try again.", error.Message);
        Assert.Equal(["proj"], Directory.GetFileSystemEntries(workspace.Root).Select(Path.GetFileName).Where(n => n != "_temp"));
    }

    [Fact]
    public async Task Paths_longer_than_max_path_extract()
    {
        using var workspace = new TestWorkspace("long");
        var segments = Enumerable.Range(0, 12).Select(i => $"folder-{i:00}-" + new string('x', 40)).ToList();
        var zip = Fixtures.Zip(workspace.Path("arc/long.zip"), (string.Join('/', segments) + "/deep.txt", "deep"), ("top.txt", "top"));
        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [zip]);
        var outDir = Assert.Single(result.Outputs);
        var deep = Path.Combine(outDir, Path.Combine([.. segments, "deep.txt"]));
        Assert.True(deep.Length > 260);
        Assert.Equal("deep", File.ReadAllText(deep));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Seven_zip_with_encrypted_headers_asks_for_the_password_everywhere()
    {
        if (Fixtures.SevenZip is null)
            return;
        using var workspace = new TestWorkspace("mhe");
        var source = Fixtures.SourceTree(workspace, "Secret");
        var archive = workspace.Path("arc/locked.7z");
        await Fixtures.SevenZipAsync(Path.GetDirectoryName(source)!, "a", "-t7z", "-pS3cret", "-mhe=on", "-y", archive, "Secret");
        foreach (var id in new[] { "archive.list", "archive.zip", "archive.test", "archive.extract" })
        {
            var error = await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, id), [archive]));
            Assert.Contains("password-protected", error.Message);
        }
        var wrong = await Assert.ThrowsAsync<OperationException>(() =>
            workspace.RunAsync(Op(workspace, "archive.test.password"), [archive], new Dictionary<string, object?> { ["password"] = "wrong" }));
        Assert.Contains("password", wrong.Message);
        Assert.Equal(["locked.7z"], Directory.GetFileSystemEntries(Path.GetDirectoryName(archive)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Hostile_windows_names_are_neutralised()
    {
        using var workspace = new TestWorkspace("weird");
        var zip = Fixtures.Zip(workspace.Path("arc/weird.zip"),
            ("a.txt:evil:$DATA", "ads"),
            ("x/CON/y.txt", "con"),
            ("x/aux.", "aux"),
            ("dir./file.txt", "dotdir"),
            ("dir/file.txt", "dir"),
            ("NUL .txt", "nul"),
            ("‮gnp.exe", "rtl"));
        var result = await workspace.RunAsync(Op(workspace, "archive.extract"), [zip]);
        var outDir = Assert.Single(result.Outputs);
        var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(outDir, f), File.ReadAllText);
        Assert.Equal("ads", files["a.txt_evil_$DATA"]);
        Assert.Equal("con", files[Path.Combine("x", "_CON", "y.txt")]);
        Assert.Equal("aux", files[Path.Combine("x", "_aux")]);
        Assert.Equal("dotdir", files[Path.Combine("dir", "file.txt")]);
        Assert.Equal("dir", files[Path.Combine("dir", "file (2).txt")]);
        Assert.Equal("nul", files["_NUL .txt"]);
        Assert.Equal("rtl", files["_gnp.exe"]);
        Assert.Equal(7, files.Count);
        Assert.Contains(result.Warnings, w => w.Contains("changed to suit Windows", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.GetFiles(outDir, "*", SearchOption.AllDirectories), f => f.IndexOf(':', 2) >= 0);
    }

    [Fact]
    public async Task Compressing_thousands_of_small_files_reports_progress()
    {
        using var workspace = new TestWorkspace("smallfiles");
        var folder = Path.Combine(workspace.Root, "many");
        Directory.CreateDirectory(folder);
        for (var i = 0; i < 3000; i++)
            File.WriteAllText(Path.Combine(folder, $"f{i:0000}.txt"), "tiny " + i);
        var reports = new List<JobProgress>();
        var progress = new SyncProgress(reports.Add);
        foreach (var id in new[] { "folder.zip", "folder.tar-gz" })
        {
            reports.Clear();
            await workspace.RunAsync(Op(workspace, id), [folder], progress: progress);
            var compressing = reports.Where(r => r.Status == "Compressing…").ToList();
            Assert.True(compressing.Count >= 5, $"{id}: {compressing.Count} progress reports");
            Assert.All(compressing, r => Assert.InRange(r.Fraction ?? -1, 0, 1));
        }
    }

    sealed class SyncProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value)
        {
            lock (this)
                report(value);
        }
    }
}
