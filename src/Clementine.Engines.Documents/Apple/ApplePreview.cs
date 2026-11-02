using System.IO.Compression;

namespace Clementine.Engines.Documents.Apple;

/// <summary>The preview Pages, Numbers and Keynote save inside their files.</summary>
internal sealed record ApplePreviewData(byte[] Data, bool IsPdf, string EntryName);

/// <summary>
/// Finds the preview embedded in an iWork file: "QuickLook/Preview.pdf" (iWork '09, all pages) or the preview JPEG
/// newer versions write ("preview.jpg", first page only). Works for single-file documents (zip) and macOS packages
/// (folders).
/// </summary>
internal static class ApplePreview
{
    static readonly string[] PdfEntries = ["QuickLook/Preview.pdf", "preview.pdf"];

    static readonly string[] ImageEntries =
    [
        "preview.jpg", "QuickLook/Preview.jpg", "QuickLook/Thumbnail.jpg", "preview-web.jpg", "QuickLook/Thumbnail.png", "preview-micro.jpg",
    ];

    public static ApplePreviewData Find(InputFile input)
    {
        var app = FileTypes.ExtensionOf(input.Name) switch
        {
            ".numbers" => "Numbers",
            ".key" => "Keynote",
            _ => "Pages",
        };
        var path = input.Path;
        var entries = input.IsDirectory ? ReadPackage(path) : ReadZip(path, input.Name, app);
        foreach (var name in PdfEntries)
        {
            if (entries.TryGetValue(name, out var pdf))
                return new ApplePreviewData(pdf, true, name);
        }
        var image = ImageEntries
            .Select(name => entries.TryGetValue(name, out var data) ? new ApplePreviewData(data, false, name) : null)
            .OfType<ApplePreviewData>()
            .OrderByDescending(p => p.Data.Length)
            .FirstOrDefault();
        return image ?? throw new OperationException(
            $"This {app} file doesn't contain a preview Clementine can use. Export it as PDF from {app} (on a Mac, iPad or iCloud.com) instead.");
    }

    /// <summary>The preview entries of a single-file (zip) iWork document.</summary>
    static Dictionary<string, byte[]> ReadZip(string path, string name, string app)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                var entryName = entry.FullName.Replace('\\', '/');
                if (!PdfEntries.Contains(entryName, StringComparer.OrdinalIgnoreCase) && !ImageEntries.Contains(entryName, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (entry.Length > 200L * 1024 * 1024)
                    continue;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                entries[entryName] = buffer.ToArray();
            }
        }
        catch (InvalidDataException ex)
        {
            throw new OperationException($"\"{name}\" isn't a {app} file Clementine can open.", ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new OperationException($"Couldn't read \"{name}\" — it may be open in another program.", ex.Message, ex);
        }
        return entries;
    }

    static Dictionary<string, byte[]> ReadPackage(string directory)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in PdfEntries.Concat(ImageEntries))
        {
            var file = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file))
                entries[name] = File.ReadAllBytes(file);
        }
        return entries;
    }
}
