using System.Collections.Concurrent;

namespace Clementine.Core;

/// <summary>
/// Chooses where outputs go and guarantees an existing file is never overwritten. Outputs are first written to a
/// hidden-ish ".name.clementine-xxxx.partial.ext" working path in the destination folder, then renamed on
/// <see cref="PendingOutput.Commit"/>, so a cancelled or failed job never leaves a half-written file behind.
/// </summary>
public sealed class OutputPlanner
{
    // Process-wide: two parallel jobs must never pick the same name.
    static readonly ConcurrentDictionary<string, byte> Reserved = new(StringComparer.OrdinalIgnoreCase);
    static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    readonly Func<ClementineSettings> _settings;
    readonly ConcurrentDictionary<string, bool> _writableCache = new(StringComparer.OrdinalIgnoreCase);

    public OutputPlanner(Func<ClementineSettings> settings) => _settings = settings;

    /// <summary>Destination folder for outputs made from <paramref name="input"/> (falls back if not writable).</summary>
    public string ResolveDirectory(InputFile input)
    {
        var settings = _settings();
        var preferred = settings.OutputMode == OutputLocationMode.CustomFolder && !string.IsNullOrWhiteSpace(settings.CustomOutputFolder)
            ? settings.CustomOutputFolder!
            : input.DirectoryPath;
        return IsWritable(preferred) ? preferred : EnsureFallback();
    }

    /// <summary>Reserves "&lt;input base name&gt;[ (suffix)]&lt;extension&gt;" in the destination folder.</summary>
    public PendingOutput BeginFile(InputFile input, string extension, string? suffix = null) =>
        BeginFile(ResolveDirectory(input), input.BaseName + FormatSuffix(suffix), extension);

    /// <summary>Reserves "&lt;baseName&gt;&lt;extension&gt;" (or "&lt;baseName&gt; (2)&lt;extension&gt;" …) in <paramref name="directory"/>.</summary>
    public PendingOutput BeginFile(string directory, string baseName, string extension)
    {
        directory = IsWritable(directory) ? directory : EnsureFallback();
        extension = NormalizeExtension(extension);
        baseName = SanitizeName(baseName);
        var final = ReserveUnique(directory, baseName, extension);
        var working = Path.Combine(directory, $".{Shorten(baseName)}.clementine-{ShortId()}.partial{extension}");
        return new PendingOutput(final, working, isDirectory: false, directory, baseName, extension);
    }

    /// <summary>Reserves a folder "&lt;input base name&gt;[ (suffix)]" for multi-file outputs (pages, frames, parts).</summary>
    public PendingOutput BeginDirectory(InputFile input, string? suffix = null) =>
        BeginDirectory(ResolveDirectory(input), input.BaseName + FormatSuffix(suffix));

    public PendingOutput BeginDirectory(string directory, string name)
    {
        directory = IsWritable(directory) ? directory : EnsureFallback();
        name = SanitizeName(name);
        var final = ReserveUnique(directory, name, "");
        var working = Path.Combine(directory, $".{Shorten(name)}.clementine-{ShortId()}.partial");
        Directory.CreateDirectory(working);
        return new PendingOutput(final, working, isDirectory: true, directory, name, "");
    }

    internal static string ReserveUnique(string directory, string baseName, string extension)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? baseName + extension : $"{baseName} ({n}){extension}";
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                continue;
            if (Reserved.TryAdd(candidate, 0))
                return candidate;
        }
    }

    internal static void Release(string path) => Reserved.TryRemove(path, out _);

    /// <summary>Removes characters Windows doesn't allow in file names.</summary>
    public static string SanitizeName(string name)
    {
        var cleaned = new string(name.Select(c => Array.IndexOf(InvalidNameChars, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "output" : cleaned;
    }

    static string FormatSuffix(string? suffix) => string.IsNullOrWhiteSpace(suffix) ? "" : $" ({suffix})";

    // Case is kept as given (Duplicate / extract keep "Photo.JPG"); uniqueness checks are case-insensitive anyway.
    static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "";
        return extension.StartsWith('.') ? extension : "." + extension;
    }

    static string Shorten(string name) => name.Length <= 40 ? name : name[..40];

    static string ShortId() => Guid.NewGuid().ToString("N")[..8];

    bool IsWritable(string directory) => _writableCache.GetOrAdd(directory, static dir =>
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".clementine-probe-{ShortId()}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    });

    static string EnsureFallback()
    {
        Directory.CreateDirectory(AppPaths.FallbackOutputDirectory);
        return AppPaths.FallbackOutputDirectory;
    }
}

/// <summary>A reserved output. Write to <see cref="WorkingPath"/>, then <see cref="Commit"/>; disposing uncommitted cleans up.</summary>
public sealed class PendingOutput : IDisposable
{
    readonly string _directory;
    readonly string _baseName;
    readonly string _extension;

    internal PendingOutput(string finalPath, string workingPath, bool isDirectory, string directory, string baseName, string extension)
    {
        FinalPath = finalPath;
        WorkingPath = workingPath;
        IsDirectory = isDirectory;
        _directory = directory;
        _baseName = baseName;
        _extension = extension;
    }

    /// <summary>Where the result will live after <see cref="Commit"/>.</summary>
    public string FinalPath { get; private set; }

    /// <summary>Write here (same folder as <see cref="FinalPath"/>, same extension, so tools pick the right format).</summary>
    public string WorkingPath { get; }

    public bool IsDirectory { get; }

    public bool IsCommitted { get; private set; }

    /// <summary>Moves the working file/folder into place and returns the final path.</summary>
    public string Commit()
    {
        if (IsCommitted)
            return FinalPath;
        if (IsDirectory ? !Directory.Exists(WorkingPath) : !File.Exists(WorkingPath))
            throw new OperationException("The conversion finished without producing an output file.");

        if (File.Exists(FinalPath) || Directory.Exists(FinalPath))
        {
            var taken = FinalPath;
            FinalPath = OutputPlanner.ReserveUnique(_directory, _baseName, _extension);
            OutputPlanner.Release(taken);
        }

        if (IsDirectory)
            Directory.Move(WorkingPath, FinalPath);
        else
            File.Move(WorkingPath, FinalPath, overwrite: false);

        IsCommitted = true;
        OutputPlanner.Release(FinalPath);
        return FinalPath;
    }

    public void Dispose()
    {
        if (IsCommitted)
            return;
        // A tool that was just stopped may still hold the file for a moment; retry briefly. If it is still locked,
        // the leftover is harmless and clearly named.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (IsDirectory && Directory.Exists(WorkingPath))
                    Directory.Delete(WorkingPath, recursive: true);
                else if (File.Exists(WorkingPath))
                    File.Delete(WorkingPath);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
        OutputPlanner.Release(FinalPath);
    }
}
