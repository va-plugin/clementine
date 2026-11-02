using System.Text.RegularExpressions;

namespace Clementine.Engines.Files.Archives;

/// <summary>One file or folder to put in an archive. <c>EntryName</c> uses '/' separators; folders end with '/'.</summary>
internal sealed record ArchiveItem(string SourcePath, string EntryName, bool IsDirectory, long Length, DateTime LastWriteTimeUtc);

/// <summary>Everything gathered for an archive, plus what had to be left out.</summary>
internal sealed class CollectedItems
{
    public List<ArchiveItem> Items { get; } = [];

    public long TotalBytes { get; set; }

    public int FileCount { get; set; }

    public int SkippedLinks { get; set; }

    public int SkippedUnreadable { get; set; }

    /// <summary>
    /// Folder every entry name is relative to (all inputs share one parent and kept their names), or null.
    /// 7-Zip is run from here so it stores exactly the same paths as the ZIP/TAR writers.
    /// </summary>
    public string? BaseDirectory { get; set; }

    /// <summary>The top-level paths that were collected (for 7-Zip when there is no common base).</summary>
    public List<string> Roots { get; } = [];

    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();
        if (SkippedLinks > 0)
            warnings.Add($"{SkippedLinks:N0} shortcut link(s) or junction(s) left out — Clementine doesn't follow links when archiving.");
        if (SkippedUnreadable > 0)
            warnings.Add($"{SkippedUnreadable:N0} folder(s) couldn't be read (access denied) and were left out.");
        return warnings;
    }
}

/// <summary>
/// Expands files and folders into archive items without following symbolic links or junctions (which could loop
/// forever or pull in files from elsewhere), including hidden files, and skipping Clementine's own partial outputs.
/// </summary>
internal static partial class FileCollector
{
    static readonly EnumerationOptions Children = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Each path becomes a top-level entry named after it; folders bring everything below them.</summary>
    public static CollectedItems Collect(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var result = new CollectedItems();
        var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? commonParent = null;
        var sameParent = true;
        var renamedRoot = false;

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var parent = Path.GetDirectoryName(full);
            if (commonParent is null)
                commonParent = parent;
            else if (!string.Equals(commonParent, parent, StringComparison.OrdinalIgnoreCase))
                sameParent = false;

            var name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(name))
                name = "Drive " + full.TrimEnd(':', '\\');
            var unique = UniqueName(rootNames, name, isFile: !Directory.Exists(full));
            renamedRoot |= unique != name;
            result.Roots.Add(full);

            if (Directory.Exists(full))
            {
                var directory = new DirectoryInfo(full);
                if (IsLink(directory))
                {
                    result.SkippedLinks++;
                    continue;
                }
                AddTree(directory, unique, result, cancellationToken);
            }
            else
            {
                var file = new FileInfo(full);
                if (!file.Exists)
                    throw new OperationException($"\"{name}\" no longer exists.");
                if (IsLink(file))
                {
                    result.SkippedLinks++;
                    continue;
                }
                AddFile(file, unique, result);
            }
        }

        result.BaseDirectory = sameParent && !renamedRoot ? commonParent : null;
        return result;
    }

    /// <summary>Everything inside <paramref name="directory"/>, with names relative to it.</summary>
    public static CollectedItems CollectContents(string directory, CancellationToken cancellationToken)
    {
        var result = new CollectedItems { BaseDirectory = Path.GetFullPath(directory) };
        var root = new DirectoryInfo(directory);
        foreach (var child in SortedChildren(root, result))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Roots.Add(child.FullName);
            if (IsLink(child))
                result.SkippedLinks++;
            else if (child is DirectoryInfo sub)
                AddTree(sub, child.Name, result, cancellationToken);
            else if (child is FileInfo file)
                AddFile(file, child.Name, result);
        }
        return result;
    }

    static void AddTree(DirectoryInfo top, string topName, CollectedItems result, CancellationToken cancellationToken)
    {
        var pending = new Stack<(DirectoryInfo Directory, string Name)>();
        pending.Push((top, topName));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, name) = pending.Pop();
            result.Items.Add(new ArchiveItem(directory.FullName, name + "/", IsDirectory: true, 0, SafeTime(directory)));

            var subdirectories = new List<(DirectoryInfo, string)>();
            foreach (var child in SortedChildren(directory, result))
            {
                if (IsLink(child))
                {
                    result.SkippedLinks++;
                    continue;
                }
                var childName = $"{name}/{child.Name}";
                if (child is DirectoryInfo sub)
                    subdirectories.Add((sub, childName));
                else if (child is FileInfo file)
                    AddFile(file, childName, result);
            }
            for (var i = subdirectories.Count - 1; i >= 0; i--)
                pending.Push(subdirectories[i]);
        }
    }

    static void AddFile(FileInfo file, string name, CollectedItems result)
    {
        result.Items.Add(new ArchiveItem(file.FullName, name, IsDirectory: false, file.Length, SafeTime(file)));
        result.TotalBytes += file.Length;
        result.FileCount++;
    }

    static List<FileSystemInfo> SortedChildren(DirectoryInfo directory, CollectedItems result)
    {
        try
        {
            return directory.EnumerateFileSystemInfos("*", Children)
                .Where(c => !IsPartialOutput(c.Name))
                .OrderBy(c => c is DirectoryInfo ? 1 : 0)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            result.SkippedUnreadable++;
            return [];
        }
    }

    /// <summary>
    /// Symbolic links and junctions. Other reparse points — OneDrive/Dropbox online-only placeholders, dedup files —
    /// are ordinary files to the user and are archived (reading them downloads them).
    /// </summary>
    static bool IsLink(FileSystemInfo info)
    {
        if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        try
        {
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    static DateTime SafeTime(FileSystemInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;
        }
    }

    /// <summary>"notes.txt", then "notes (2).txt" for a second input with the same name (folders: "Shots (2)").</summary>
    static string UniqueName(HashSet<string> taken, string name, bool isFile)
    {
        var extension = isFile ? Path.GetExtension(name) : "";
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (stem.Length == name.Length)
            extension = "";
        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++)
            candidate = $"{stem} ({n}){extension}";
        return candidate;
    }

    /// <summary>Clementine's own in-progress outputs (".name.clementine-1a2b3c4d.partial…").</summary>
    internal static bool IsPartialOutput(string name) => PartialOutput().IsMatch(name);

    [GeneratedRegex(@"^\..*\.clementine-[0-9a-f]{8}\.partial", RegexOptions.IgnoreCase)]
    private static partial Regex PartialOutput();
}
