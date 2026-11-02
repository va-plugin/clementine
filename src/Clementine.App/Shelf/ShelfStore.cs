using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using Clementine.App.Interop;
using Clementine.Core;

namespace Clementine.App.Shelf;

/// <summary>A file or folder pinned to the shelf.</summary>
public sealed class ShelfItem
{
    ImageSource? _icon;
    bool _iconLoaded;

    public ShelfItem(string path) => Path = path;

    public string Path { get; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } name ? name : Path;

    public string Folder => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\')) ?? "";

    /// <summary>The last two folder names ("Projects\Renders"), enough to recognise where a file lives.</summary>
    public string FolderShort
    {
        get
        {
            var parts = Folder.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length <= 2 ? Folder : "…\\" + string.Join("\\", parts[^2..]);
        }
    }

    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    /// <summary>What screen readers announce for the shelf row.</summary>
    public override string ToString() => $"{Name}, in {Folder}";

    /// <summary>Shell icon (loaded lazily on the UI thread).</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = ShellIcons.For(Path, 32);
            }
            return _icon;
        }
    }
}

/// <summary>
/// The shelf's contents: an observable list of paths, persisted to <c>shelf.json</c> in the data folder so pinned
/// files survive a restart. UI thread only.
/// </summary>
public sealed class ShelfStore
{
    readonly string? _filePath;
    readonly ILog _log;

    /// <param name="filePath">Where to persist the list, or null to keep it in memory (snapshots, tests).</param>
    public ShelfStore(string? filePath, ILog log)
    {
        _filePath = filePath;
        _log = log;
        Load();
    }

    public static string DefaultFile => System.IO.Path.Combine(AppPaths.DataDirectory, "shelf.json");

    public ObservableCollection<ShelfItem> Items { get; } = [];

    /// <summary>Adds paths that exist and aren't already pinned; returns how many were added.</summary>
    public int Add(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;
            Items.Add(new ShelfItem(path));
            added++;
        }
        if (added > 0)
            Save();
        return added;
    }

    /// <summary>Adds paths without checking that they exist (snapshot samples).</summary>
    internal void AddUnchecked(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            Items.Add(new ShelfItem(path));
    }

    public void Remove(ShelfItem item)
    {
        if (Items.Remove(item))
            Save();
    }

    public void Clear()
    {
        Items.Clear();
        Save();
    }

    /// <summary>Drops entries whose file has been deleted or moved away.</summary>
    public void PruneMissing()
    {
        var missing = Items.Where(i => !i.Exists).ToList();
        foreach (var item in missing)
            Items.Remove(item);
        if (missing.Count > 0)
            Save();
    }

    void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
            return;
        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_filePath)) ?? [];
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(path) || Directory.Exists(path))
                    Items.Add(new ShelfItem(path));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Warn("Couldn't read the shelf", ex);
        }
    }

    void Save()
    {
        if (_filePath is null)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Items.Select(i => i.Path).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Couldn't save the shelf", ex);
        }
    }
}
