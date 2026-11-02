using System.Globalization;
using Clementine.Core;

namespace Clementine.Wheel;

/// <summary>
/// How the wheel centre names the selection: "clip.mp4" / "MP4 video · 24 MB" for one file, "3 videos" /
/// "1.2 GB" for several of a kind, "5 files" / "2 videos · 3 images" for a mix.
/// </summary>
public sealed record SelectionSummary(string Headline, string? Detail, FileKind? Kind, int Count)
{
    public static SelectionSummary Describe(IReadOnlyList<InputFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            return new SelectionSummary("No files", null, null, 0);

        var totalSize = files.Sum(f => f.Length);
        if (files.Count == 1)
        {
            var file = files[0];
            var kind = DescribeSingle(file);
            var detail = file.IsDirectory || file.Length == 0 ? kind : $"{kind} · {FormatSize(file.Length)}";
            return new SelectionSummary(file.Name, detail, file.Kind, 1);
        }

        var kinds = files.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        if (kinds.Count == 1)
        {
            var kind = kinds[0].Key;
            return new SelectionSummary(Noun(kind, files.Count), totalSize > 0 ? FormatSize(totalSize) : null, kind, files.Count);
        }

        var parts = kinds.Take(3).Select(g => Noun(g.Key, g.Count())).ToList();
        if (kinds.Count > 3)
            parts.Add("…");
        return new SelectionSummary($"{files.Count} files", string.Join(" · ", parts), null, files.Count);
    }

    /// <summary>"1 video", "3 videos", "2 PDFs", "4 audio files".</summary>
    public static string Noun(FileKind kind, int count)
    {
        var (singular, plural) = kind switch
        {
            FileKind.Folder => ("folder", "folders"),
            FileKind.Image => ("image", "images"),
            FileKind.Video => ("video", "videos"),
            FileKind.Audio => ("audio file", "audio files"),
            FileKind.Pdf => ("PDF", "PDFs"),
            FileKind.Document => ("document", "documents"),
            FileKind.Spreadsheet => ("spreadsheet", "spreadsheets"),
            FileKind.Presentation => ("presentation", "presentations"),
            FileKind.Ebook => ("e-book", "e-books"),
            FileKind.Text => ("text file", "text files"),
            FileKind.Subtitle => ("subtitle file", "subtitle files"),
            FileKind.Archive => ("archive", "archives"),
            _ => ("file", "files"),
        };
        return $"{count.ToString(CultureInfo.CurrentCulture)} {(count == 1 ? singular : plural)}";
    }

    /// <summary>"24 KB", "3.4 MB", "1.2 GB".</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        if (unit == 0)
            return $"{bytes.ToString(CultureInfo.CurrentCulture)} {(bytes == 1 ? "byte" : "bytes")}";
        var format = value < 10 ? "0.#" : "0";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {units[unit]}";
    }

    static string DescribeSingle(InputFile file)
    {
        if (file.IsDirectory)
            return "Folder";
        var ext = file.Extension.TrimStart('.').ToUpperInvariant();
        var noun = file.Kind switch
        {
            FileKind.Image => "image",
            FileKind.Video => "video",
            FileKind.Audio => "audio",
            FileKind.Pdf => "document",
            FileKind.Document => "document",
            FileKind.Spreadsheet => "spreadsheet",
            FileKind.Presentation => "presentation",
            FileKind.Ebook => "e-book",
            FileKind.Subtitle => "subtitles",
            FileKind.Archive => "archive",
            _ => "file",
        };
        return ext.Length == 0 ? "File" : $"{ext} {noun}";
    }
}
