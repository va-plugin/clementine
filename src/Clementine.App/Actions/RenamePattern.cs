using System.Globalization;
using System.IO;
using System.Text;
using Clementine.Core;

namespace Clementine.App.Actions;

/// <summary>
/// Batch-rename patterns. Tokens: <c>{name}</c> original name without extension, <c>{n}</c> counter (padded to the
/// chosen digits), <c>{date}</c> the file's modified date (yyyy-MM-dd), <c>{ext}</c> extension without the dot,
/// <c>{folder}</c> parent folder name. The extension is always kept.
/// </summary>
public static class RenamePattern
{
    public const string DefaultPattern = "{name} {n}";

    public static readonly IReadOnlyList<string> Tokens = ["{name}", "{n}", "{date}", "{ext}", "{folder}"];

    /// <summary>The new file name (with extension) for the <paramref name="index"/>-th file (0-based).</summary>
    public static string Apply(string pattern, InputFile file, int index, int start = 1, int digits = 0, DateTime? modified = null)
    {
        var number = (start + index).ToString(CultureInfo.InvariantCulture);
        if (digits > 0)
            number = number.PadLeft(digits, '0');
        var date = (modified ?? SafeModified(file)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var folderName = Path.GetFileName(file.DirectoryPath.TrimEnd(Path.DirectorySeparatorChar)) ?? "";

        var builder = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '{')
            {
                var close = pattern.IndexOf('}', i);
                if (close > i)
                {
                    var token = pattern[(i + 1)..close].ToLowerInvariant();
                    string? replacement = token switch
                    {
                        "name" => file.BaseName,
                        "n" => number,
                        "date" => date,
                        "ext" => file.Extension.TrimStart('.'),
                        "folder" => folderName,
                        _ => null,
                    };
                    if (replacement is not null)
                    {
                        builder.Append(replacement);
                        i = close;
                        continue;
                    }
                }
            }
            builder.Append(pattern[i]);
        }

        var baseName = OutputPlanner.SanitizeName(builder.ToString());
        return file.IsDirectory ? baseName : baseName + OriginalExtension(file);
    }

    /// <summary>The extension exactly as the file has it (".PNG" stays ".PNG"), including ".tar.gz" style ones.</summary>
    static string OriginalExtension(InputFile file) =>
        file.Extension.Length > 0 && file.Name.Length > file.Extension.Length ? file.Name[^file.Extension.Length..] : file.Extension;

    /// <summary>Digits needed so every counter has the same width ("01" … "12").</summary>
    public static int AutoDigits(int count, int start) => Math.Max(1, (start + Math.Max(count, 1) - 1).ToString(CultureInfo.InvariantCulture).Length);

    /// <summary>
    /// Plans the renames: target names that collide with an existing file (other than one being renamed in this batch)
    /// or with another target get " (2)", " (3)" … appended. Files whose name doesn't change are left out.
    /// </summary>
    public static IReadOnlyList<(InputFile File, string NewPath)> Plan(IReadOnlyList<InputFile> files, string pattern, int start, int digits, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        var names = files.Select((f, i) => Apply(pattern, f, i, start, digits)).ToList();
        var unchanged = files.Where((f, i) => string.Equals(Path.Combine(f.DirectoryPath, names[i]), f.Path, StringComparison.Ordinal)).ToList();
        // Files that keep their name block that name; files that move away free theirs (renames run in two phases).
        var taken = new HashSet<string>(unchanged.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var renaming = new HashSet<string>(files.Except(unchanged).Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var plan = new List<(InputFile, string)>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var name = names[i];
            var target = Path.Combine(file.DirectoryPath, name);
            if (string.Equals(target, file.Path, StringComparison.Ordinal))
                continue;
            var candidate = target;
            var extension = file.IsDirectory ? "" : OriginalExtension(file);
            var stem = name[..^extension.Length];
            for (var n = 2; taken.Contains(candidate) || (exists(candidate) && !renaming.Contains(candidate) && !string.Equals(candidate, file.Path, StringComparison.OrdinalIgnoreCase)); n++)
                candidate = Path.Combine(file.DirectoryPath, $"{stem} ({n}){extension}");
            taken.Add(candidate);
            plan.Add((file, candidate));
        }
        return plan;
    }

    static DateTime SafeModified(InputFile file)
    {
        try
        {
            return file.IsDirectory ? Directory.GetLastWriteTime(file.Path) : File.GetLastWriteTime(file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.Today;
        }
    }
}
