namespace Clementine.Engines.Files.Archives;

/// <summary>
/// Maps sanitized entry paths to unique paths under the destination. Windows file names are case-insensitive, so
/// "Readme.txt" and "README.txt" from a Linux tar — or a duplicate entry, or a file and a folder with the same
/// name — would collide; the later one gets " (2)" instead of silently overwriting the earlier one.
/// </summary>
internal sealed class ExtractionLayout
{
    readonly DirectoryNode _root;

    public ExtractionLayout(string root) => _root = new DirectoryNode(root);

    /// <summary>Number of entries that had to be renamed to avoid a clash.</summary>
    public int Renamed { get; private set; }

    public string ResolveDirectory(IReadOnlyList<string> segments) => Directory(segments, segments.Count).Path;

    public string ResolveFile(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
            throw new ArgumentException("A file needs a name.", nameof(segments));
        var parent = Directory(segments, segments.Count - 1);
        var name = Claim(parent, segments[^1]);
        return System.IO.Path.Combine(parent.Path, name);
    }

    DirectoryNode Directory(IReadOnlyList<string> segments, int count)
    {
        var node = _root;
        for (var i = 0; i < count; i++)
        {
            var segment = segments[i];
            if (!node.Directories.TryGetValue(segment, out var child))
            {
                var actual = Claim(node, segment);
                child = new DirectoryNode(System.IO.Path.Combine(node.Path, actual));
                node.Directories[segment] = child;
            }
            node = child;
        }
        return node;
    }

    string Claim(DirectoryNode parent, string requested)
    {
        var actual = requested;
        if (IsTaken(parent, actual))
        {
            var extension = System.IO.Path.GetExtension(requested);
            var stem = extension.Length > 0 && extension.Length < requested.Length ? requested[..^extension.Length] : requested;
            if (stem.Length == requested.Length)
                extension = "";
            for (var n = 2; IsTaken(parent, actual); n++)
                actual = $"{stem} ({n}){extension}";
            Renamed++;
        }
        parent.Taken.Add(actual);
        return actual;
    }

    /// <summary>
    /// Taken by an earlier entry — by name, or through an 8.3 short name: on volumes that generate them,
    /// "VERYLO~1.TXT" <em>is</em> an already extracted "verylongfilename.txt". The extraction folder starts empty, so
    /// anything already on disk under a "~" name is such an alias (only those names are checked, to stay cheap).
    /// </summary>
    static bool IsTaken(DirectoryNode parent, string name) =>
        parent.Taken.Contains(name)
        || (name.Contains('~') && System.IO.Path.Exists(System.IO.Path.Combine(parent.Path, name)));

    sealed class DirectoryNode(string path)
    {
        public string Path { get; } = path;

        /// <summary>Requested sub-folder name → node (case-insensitive, like NTFS).</summary>
        public Dictionary<string, DirectoryNode> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Actual names already used in this folder, by files and folders alike.</summary>
        public HashSet<string> Taken { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
