using Clementine.Engines.Files.Archives;

namespace Clementine.Engines.Files.Tests;

public class EntryPathSanitizerTests
{
    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("a/../x")] // even ".." that stays inside is refused: real archives never need it
    [InlineData("..\\x")]
    [InlineData(".. /x")]
    [InlineData("../")]
    public void Traversal_attempts_throw(string path) =>
        Assert.Throws<UnsafeEntryPathException>(() => EntryPathSanitizer.Sanitize(path));

    [Fact]
    public void Dot_runs_that_windows_would_trim_to_nothing_become_an_underscore() =>
        Assert.Equal(["a", "_"], EntryPathSanitizer.Sanitize("a/..../").Segments);

    [Theory]
    [InlineData("/etc/passwd", "etc|passwd")]
    [InlineData("C:\\Windows\\win.ini", "Windows|win.ini")]
    [InlineData("\\\\server\\share\\x.txt", "server|share|x.txt")]
    [InlineData("\\\\?\\C:\\x.txt", "x.txt")]
    public void Absolute_paths_become_relative(string path, string expected)
    {
        var result = EntryPathSanitizer.Sanitize(path);
        Assert.True(result.WasAbsolute);
        Assert.Equal(expected.Split('|'), result.Segments);
    }

    [Theory]
    [InlineData("./a/./b.txt", "a|b.txt", false)]
    [InlineData("a//b", "a|b", false)]
    [InlineData("CON", "_CON", true)]
    [InlineData("dir/nul.txt", "dir|_nul.txt", true)]
    [InlineData("LPT1.tar.gz", "_LPT1.tar.gz", true)]
    [InlineData("COM¹", "_COM¹", true)]
    [InlineData("console.txt", "console.txt", false)]
    [InlineData("a:b|c?.txt", "a_b_c_.txt", true)]
    [InlineData("C:relative.txt", "C_relative.txt", true)]
    [InlineData("trailing. . ", "trailing", true)]
    [InlineData("tab\there", "tab_here", true)]
    [InlineData("Ünïcødé 東京/файл.txt", "Ünïcødé 東京|файл.txt", false)]
    public void Names_are_made_windows_safe(string path, string expected, bool renamed)
    {
        var result = EntryPathSanitizer.Sanitize(path);
        Assert.Equal(expected.Split('|'), result.Segments);
        Assert.Equal(renamed, result.WasRenamed);
        Assert.False(result.WasAbsolute);
    }

    [Fact]
    public void Very_long_segments_are_shortened_keeping_the_extension()
    {
        var result = EntryPathSanitizer.Sanitize(new string('a', 400) + ".txt");
        var segment = Assert.Single(result.Segments);
        Assert.Equal(255, segment.Length);
        Assert.EndsWith(".txt", segment);
    }

    [Fact]
    public void Root_entries_are_recognised()
    {
        Assert.True(EntryPathSanitizer.Sanitize("./").IsRoot);
        Assert.True(EntryPathSanitizer.Sanitize("/").IsRoot);
        Assert.True(EntryPathSanitizer.Sanitize("").IsRoot);
    }

    [Fact]
    public void IsInside_checks_the_full_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.True(EntryPathSanitizer.IsInside(root, Path.Combine(root, "a", "b")));
        Assert.True(EntryPathSanitizer.IsInside(root, root));
        Assert.False(EntryPathSanitizer.IsInside(root, Path.Combine(root, "..", "rootkit")));
        Assert.False(EntryPathSanitizer.IsInside(root, root + "kit"));
    }
}

public class ExtractionLayoutTests
{
    [Fact]
    public void Case_insensitive_clashes_get_numbered()
    {
        var root = Path.Combine(Path.GetTempPath(), "layout");
        var layout = new ExtractionLayout(root);
        Assert.Equal(Path.Combine(root, "Readme.txt"), layout.ResolveFile(["Readme.txt"]));
        Assert.Equal(Path.Combine(root, "README (2).txt"), layout.ResolveFile(["README.txt"]));
        Assert.Equal(Path.Combine(root, "readme (3).txt"), layout.ResolveFile(["readme.txt"]));
        Assert.Equal(2, layout.Renamed);
    }

    [Fact]
    public void Directories_are_shared_case_insensitively_and_files_never_shadow_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "layout");
        var layout = new ExtractionLayout(root);
        Assert.Equal(Path.Combine(root, "Docs", "a.txt"), layout.ResolveFile(["Docs", "a.txt"]));
        Assert.Equal(Path.Combine(root, "Docs", "b.txt"), layout.ResolveFile(["docs", "b.txt"]));
        // A file named like an existing folder, then a folder named like an existing file.
        Assert.Equal(Path.Combine(root, "DOCS (2)"), layout.ResolveFile(["DOCS"]));
        Assert.Equal(Path.Combine(root, "notes"), layout.ResolveFile(["notes"]));
        Assert.Equal(Path.Combine(root, "notes (2)", "x.txt"), layout.ResolveFile(["notes", "x.txt"]));
        Assert.Equal(Path.Combine(root, "notes (2)"), layout.ResolveDirectory(["Notes"]));
    }
}

public class ArchiveFormatTests
{
    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, ArchiveFormat.SevenZip)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, ArchiveFormat.Rar)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, ArchiveFormat.Rar)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, ArchiveFormat.Gz)]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68, 0x39 }, ArchiveFormat.Bz2)]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, ArchiveFormat.Xz)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, ArchiveFormat.Zst)]
    public void Magic_bytes_are_recognised(byte[] head, object expected) =>
        Assert.Equal((ArchiveFormat)expected, ArchiveFormats.Sniff(head));

    [Fact]
    public void Unknown_bytes_are_not_an_archive() =>
        Assert.Null(ArchiveFormats.Sniff("Hello, world"u8));

    [Theory]
    [InlineData(".tgz", ArchiveFormat.TarGz)]
    [InlineData(".tar.zst", ArchiveFormat.TarZst)]
    [InlineData(".tbz2", ArchiveFormat.TarBz2)]
    [InlineData(".txz", ArchiveFormat.TarXz)]
    [InlineData(".RAR", ArchiveFormat.Rar)]
    public void Extensions_map_to_formats(string extension, object expected) =>
        Assert.Equal((ArchiveFormat)expected, ArchiveFormats.FromExtension(extension));

    [Fact]
    public void Old_v7_tar_headers_are_recognised_by_checksum()
    {
        var header = new byte[512];
        "file.txt"u8.CopyTo(header);
        "0000644\0"u8.CopyTo(header.AsSpan(100));
        "00000000005\0"u8.CopyTo(header.AsSpan(124));
        long sum = 0;
        for (var i = 0; i < 512; i++)
            sum += i is >= 148 and < 156 ? 32 : header[i];
        System.Text.Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
        Assert.True(ArchiveFormats.LooksLikeTar(header));
        header[0] = (byte)'g';
        Assert.False(ArchiveFormats.LooksLikeTar(header));
    }

    [Fact]
    public void Attribute_bits_identify_links_and_devices()
    {
        Assert.Equal(ArchiveEntryKind.SymbolicLink, SharpCompressReader.KindFromAttributes(unchecked((int)0xA1FF0000)));
        Assert.Equal(ArchiveEntryKind.Directory, SharpCompressReader.KindFromAttributes(0x41ED0000));
        Assert.Equal(ArchiveEntryKind.File, SharpCompressReader.KindFromAttributes(unchecked((int)0x81A40000)));
        Assert.Equal(ArchiveEntryKind.Special, SharpCompressReader.KindFromAttributes(0x21B60000));
        Assert.Equal(ArchiveEntryKind.SymbolicLink, SharpCompressReader.KindFromAttributes(0x420)); // Windows reparse point
        Assert.Equal(ArchiveEntryKind.File, SharpCompressReader.KindFromAttributes(0x20));
    }

    [Fact]
    public void Expansion_allowance_uses_the_larger_of_floor_and_ratio()
    {
        var limits = new ExtractionLimits { AlwaysAllowedBytes = 100, MaxExpansionRatio = 10 };
        Assert.Equal(100, limits.AllowedOutput(5));
        Assert.Equal(1000, limits.AllowedOutput(100));
        Assert.Equal(long.MaxValue, new ExtractionLimits { MaxExpansionRatio = double.MaxValue }.AllowedOutput(long.MaxValue / 2));
    }
}
