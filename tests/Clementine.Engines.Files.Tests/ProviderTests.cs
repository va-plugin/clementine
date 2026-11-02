using System.Text;
using Clementine.Core;
using Clementine.Engines.Files.Operations;
using Clementine.Engines.Files.Text;
using Clementine.TestSupport;

namespace Clementine.Engines.Files.Tests;

public class ProviderTests
{
    static readonly string[] Prefixes = ["archive.", "folder.", "files.", "subtitle.", "data."];

    [Fact]
    public void Ids_use_only_this_modules_prefixes()
    {
        using var workspace = new TestWorkspace("ids");
        var operations = new FilesOperationProvider().CreateOperations(workspace.Context).ToList();
        Assert.True(operations.Count >= 40, $"only {operations.Count} operations");
        Assert.All(operations, op => Assert.Contains(Prefixes, p => op.Info.Id.StartsWith(p, StringComparison.Ordinal)));
        Assert.All(operations, op => Assert.False(string.IsNullOrWhiteSpace(op.Info.Description)));
    }

    [Fact]
    public void Wheel_offers_the_expected_groups_per_kind_of_input()
    {
        using var workspace = new TestWorkspace("wheel");
        var catalog = new OperationCatalog(workspace.Context, [new FilesOperationProvider()]);
        string[] Groups(OperationCategory category, params string[] paths) =>
            OperationCatalog.Group(catalog.ForInputs(paths.Select(InputFile.FromPath).ToList(), category)).Select(g => g.Name).ToArray();

        var zip = Fixtures.Zip(workspace.Path("a.zip"), ("a.txt", "a"));
        var folder = Path.Combine(workspace.Root, "folder");
        Directory.CreateDirectory(folder);
        var srt = workspace.WriteText("a.srt", "x");
        var json = workspace.WriteText("a.json", "{}");
        var mp4 = workspace.WriteText("clip.mp4", "x");

        Assert.Equal(["TAR.GZ", "7Z", "TAR"], Groups(OperationCategory.Convert, zip));
        Assert.Equal(["Extract", "List contents", "Test"], Groups(OperationCategory.Tool, zip));
        Assert.Equal(["ZIP", "TAR.GZ", "7Z"], Groups(OperationCategory.Convert, folder));
        Assert.Equal(["VTT", "ASS", "TXT"], Groups(OperationCategory.Convert, srt));
        Assert.Equal(["Shift timing", "Frame rate", "Clean", "Fix encoding"], Groups(OperationCategory.Tool, srt));
        Assert.Equal(["YAML", "CSV"], Groups(OperationCategory.Convert, json));
        Assert.Equal(["Archive", "Checksum"], Groups(OperationCategory.Action, mp4, folder));
        Assert.Empty(Groups(OperationCategory.Convert, mp4));
    }

    [Theory]
    [InlineData("archive.extract", true)]
    [InlineData("archive.test", true)]
    [InlineData("archive.zip", true)]
    [InlineData("folder.7z", true)]
    [InlineData("files.compress.zip", true)]
    [InlineData("files.checksum.sha256", true)]
    [InlineData("archive.list", false)]
    [InlineData("subtitle.srt", false)]
    [InlineData("data.json.yaml", false)]
    public void Long_running_operations_use_the_heavy_pool(string id, bool heavy)
    {
        using var workspace = new TestWorkspace("weights");
        var op = workspace.Operation(new FilesOperationProvider(), id);
        Assert.Equal(heavy ? OperationWeight.Heavy : OperationWeight.Light, op.Info.Weight);
    }

    [Fact]
    public void Group_variants_are_ordered_default_first()
    {
        using var workspace = new TestWorkspace("variants");
        var catalog = new OperationCatalog(workspace.Context, [new FilesOperationProvider()]);
        var file = InputFile.FromPath(workspace.WriteText("a.txt", "x"));
        var groups = OperationCatalog.Group(catalog.ForInputs([file], OperationCategory.Action));
        Assert.Equal("files.compress.zip", groups.Single(g => g.Name == "Archive").Default.Info.Id);
        Assert.Equal("files.checksum.sha256", groups.Single(g => g.Name == "Checksum").Default.Info.Id);
    }

    [Fact]
    public void Archive_action_group_replaced_the_old_Compress_group_everywhere()
    {
        using var workspace = new TestWorkspace("archive-group");
        var operations = new FilesOperationProvider().CreateOperations(workspace.Context).ToList();
        Assert.DoesNotContain(operations, op => op.Info.Group.Equals("Compress", StringComparison.OrdinalIgnoreCase));

        var archive = operations.Where(op => op.Info.Category == OperationCategory.Action && op.Info.Id.StartsWith("files.compress.", StringComparison.Ordinal)).ToList();
        Assert.Equal(["files.compress.zip", "files.compress.zip-each", "files.compress.tar-gz", "files.compress.7z"], archive.Select(op => op.Info.Id).ToArray());
        Assert.All(archive, op => Assert.Equal("Archive", op.Info.Group));
        Assert.Equal(["ZIP", "ZIP each separately", "TAR.GZ", "7Z"], archive.Select(op => op.Info.Variant ?? "").ToArray());
        // Shown in the wheel centre under "Archive": a sentence about the result, no stale "Compress ›" menu path.
        Assert.All(archive, op => Assert.DoesNotContain("Compress ›", op.Info.Description));
        Assert.All(archive, op => Assert.EndsWith(".", op.Info.Description));
    }
}

public class ChecksumTests
{
    static IOperation Op(TestWorkspace workspace, string id) => workspace.Operation(new FilesOperationProvider(), id);

    [Theory]
    [InlineData("files.checksum.sha256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("files.checksum.sha1", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [InlineData("files.checksum.md5", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("files.checksum.crc32", "352441C2")]
    public async Task Known_vectors_for_abc(string id, string expected)
    {
        using var workspace = new TestWorkspace("hash");
        var path = workspace.WriteText("abc.txt", "abc");
        var result = await workspace.RunAsync(Op(workspace, id), [path]);
        Assert.Equal(expected, result.Text);
        Assert.EndsWith("abc.txt", result.TextTitle);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task Several_files_and_folders_give_a_sha256sum_style_list()
    {
        using var workspace = new TestWorkspace("hashes");
        var a = workspace.WriteText("set/a.txt", "abc");
        var folder = Path.Combine(workspace.Root, "set", "Ünï");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "");
        Directory.CreateDirectory(Path.Combine(folder, "empty"));

        var result = await workspace.RunAsync(Op(workspace, "files.checksum.sha256"), [a, folder]);

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  a.txt\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  Ünï/sub/b.txt",
            result.Text);
        Assert.Equal("SHA-256 of 2 files", result.TextTitle);
    }

    [Fact]
    public async Task Empty_folder_has_nothing_to_hash()
    {
        using var workspace = new TestWorkspace("hash-empty");
        var folder = Path.Combine(workspace.Root, "nothing");
        Directory.CreateDirectory(folder);
        await Assert.ThrowsAsync<OperationException>(() => workspace.RunAsync(Op(workspace, "files.checksum.md5"), [folder]));
    }

    [Fact]
    public async Task Progress_stays_within_0_and_1_when_a_file_grows_while_it_is_hashed()
    {
        using var workspace = new TestWorkspace("hash-grow");
        var growing = workspace.Path("rec/growing.bin");
        File.WriteAllBytes(growing, new byte[17 << 20]); // just over one 16 MB progress step

        var fractions = new List<double>();
        var appended = false;
        var progress = new SyncProgress(p =>
        {
            if (p.Fraction is not { } fraction)
                return;
            fractions.Add(fraction);
            if (appended)
                return;
            appended = true;
            // The recorder keeps writing: the file ends up more than three times the size that was measured.
            using var writer = new FileStream(growing, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer.Write(new byte[40 << 20]);
        });

        var result = await workspace.RunAsync(Op(workspace, "files.checksum.crc32"), [growing], progress: progress);

        Assert.True(appended);
        Assert.True(fractions.Count >= 2, $"only {fractions.Count} progress reports");
        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
        Assert.Equal(1, fractions[^1]);
        Assert.Matches("^[0-9A-F]{8}$", result.Text);
    }

    sealed class SyncProgress(Action<JobProgress> report) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => report(value);
    }

    [Fact]
    public void Large_file_hash_matches_the_framework()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clementine-hash-{Guid.NewGuid():N}.bin");
        var data = Enumerable.Range(0, 3_000_000).Select(i => (byte)(i * 13)).ToArray();
        File.WriteAllBytes(path, data);
        try
        {
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), Checksums.Compute(path, ChecksumAlgorithm.Sha256, null, default));
            Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(data).ToString("X8"), Checksums.Compute(path, ChecksumAlgorithm.Crc32, null, default));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class TextDecoderTests
{
    [Theory]
    [InlineData(1252, "Café crème brûlée, naïve façade — déjà vu. Straße, Ærø, señor.")]
    [InlineData(1251, "Съешь же ещё этих мягких французских булок, да выпей чаю.")]
    [InlineData(1253, "Ξεσκεπάζω την ψυχοφθόρα βδελυγμία.")]
    [InlineData(1250, "Zażółć gęślą jaźń. Příliš žluťoučký kůň úpěl ďábelské ódy.")]
    [InlineData(1255, "דג סקרן שט בים מאוכזב ולפתע מצא חברה")]
    [InlineData(932, "いろはにほへと ちりぬるを わかよたれそ つねならむ。日本語のテキストです。")]
    public void Legacy_code_pages_are_detected(int codePage, string text)
    {
        var bytes = Encoding.GetEncoding(codePage).GetBytes(text);
        var decoded = TextDecoder.Decode(bytes);
        Assert.Equal(codePage, decoded.CodePage);
        Assert.Equal(text, decoded.Text);
    }

    [Fact]
    public void Byte_order_marks_and_bomless_utf16()
    {
        const string text = "Hello ünïcødé 東京";
        Assert.Equal(text, TextDecoder.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()).Text);
        var le = TextDecoder.Decode(Encoding.Unicode.GetBytes(text));
        Assert.Equal((1200, text), (le.CodePage, le.Text));
        var be = TextDecoder.Decode(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray());
        Assert.Equal((1201, text, true), (be.CodePage, be.Text, be.HadBom));
        var utf32 = TextDecoder.Decode(Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(text)).ToArray());
        Assert.Equal(text, utf32.Text);
    }

    [Fact]
    public void Valid_utf8_and_ascii_are_utf8()
    {
        Assert.True(TextDecoder.Decode(Encoding.UTF8.GetBytes("plain ascii")).IsUtf8);
        Assert.True(TextDecoder.Decode(Encoding.UTF8.GetBytes("ünïcødé ✓")).IsUtf8);
    }

    [Fact]
    public void Mojibake_is_repaired_only_when_asked_and_only_when_certain()
    {
        var bytes = Encoding.UTF8.GetBytes("CafÃ© â€” donâ€™t");
        Assert.Equal("CafÃ© â€” donâ€™t", TextDecoder.Decode(bytes).Text);
        var repaired = TextDecoder.Decode(bytes, repairMojibake: true);
        Assert.True(repaired.RepairedMojibake);
        Assert.Equal("Café — don’t", repaired.Text);

        // Genuine text with "Ã" that isn't double-encoded is left alone.
        Assert.False(TextDecoder.Decode(Encoding.UTF8.GetBytes("SÃO PAULO ©"), repairMojibake: true).RepairedMojibake);
    }

    [Fact]
    public void Binary_files_are_refused() =>
        Assert.Throws<OperationException>(() => TextDecoder.Decode([0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x01, 0x02, 0x03]));

    [Fact]
    public void Encoding_choices_all_resolve()
    {
        foreach (var choice in EncodingCatalog.Choices)
            Assert.NotNull(EncodingCatalog.Resolve(choice.Value));
        Assert.Throws<OperationException>(() => EncodingCatalog.Resolve("klingon-1"));
    }
}
