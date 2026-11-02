using System.Text.Json;

namespace Clementine.Core;

/// <summary>Loads and saves <see cref="ClementineSettings"/>. <see cref="Current"/> is replaced, never mutated, on update.</summary>
public sealed class SettingsStore
{
    readonly object _gate = new();
    readonly bool _persist;

    /// <param name="filePath">Defaults to <see cref="AppPaths.SettingsFile"/>.</param>
    /// <param name="persist">False keeps changes in memory only (CLI overrides, tests).</param>
    public SettingsStore(string? filePath = null, bool persist = true)
    {
        FilePath = filePath ?? AppPaths.SettingsFile;
        _persist = persist;
        Current = Load(FilePath);
    }

    /// <summary>In-memory store seeded with <paramref name="settings"/>.</summary>
    public static SettingsStore InMemory(ClementineSettings? settings = null)
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "clementine-unused-settings.json"), persist: false);
        store.Current = settings?.Clone() ?? new ClementineSettings();
        return store;
    }

    public string FilePath { get; }

    public ClementineSettings Current { get; private set; }

    /// <summary>Raised after every successful <see cref="Update"/>.</summary>
    public event EventHandler? Changed;

    public void Update(Action<ClementineSettings> change)
    {
        lock (_gate)
        {
            var next = Current.Clone();
            change(next);
            if (_persist)
                Save(next);
            Current = next;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    static ClementineSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ClementineSettings();
            return ClementineSettings.Deserialize(File.ReadAllText(path)) ?? new ClementineSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                File.Copy(path, $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
            }
            return new ClementineSettings();
        }
    }

    void Save(ClementineSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, ClementineSettings.JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
