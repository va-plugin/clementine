using System.Text.RegularExpressions;
using Clementine.Core;

namespace Clementine.TestSupport;

/// <summary>Rules every provider must satisfy. Each engine's test project calls <see cref="Validate"/>.</summary>
public static partial class ProviderContract
{
    public static readonly IReadOnlySet<string> IconKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "video", "audio", "image", "pdf", "document", "spreadsheet", "presentation", "ebook", "text", "code",
        "subtitle", "archive", "folder", "extract", "compress", "resize", "crop", "rotate", "flip", "trim", "split",
        "join", "merge", "speed", "snapshot", "frames", "mute", "volume", "normalize", "channels", "bleep", "fade",
        "reverse", "loop", "metadata", "info", "qr", "adjust", "layers", "grid", "transparent", "lock", "unlock",
        "pages", "copy", "move", "link", "terminal", "hash", "share", "pin", "rename", "open", "gif", "vj", "film",
        "music", "waveform", "palette", "erase", "convert",
    };

    /// <summary>Returns every violation (empty = OK).</summary>
    public static IReadOnlyList<string> Validate(IOperationProvider provider, EngineContext context)
    {
        var problems = new List<string>();
        var operations = provider.CreateOperations(context).ToList();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in operations)
        {
            var info = op.Info;
            var where = $"{provider.Name}:{info.Id}";
            if (!ids.Add(info.Id))
                problems.Add($"{where}: duplicate id");
            if (!IdPattern().IsMatch(info.Id))
                problems.Add($"{where}: id must be lower-case dotted segments of [a-z0-9-]");
            if (string.IsNullOrWhiteSpace(info.Group))
                problems.Add($"{where}: Group is empty");
            if (string.IsNullOrWhiteSpace(info.Title))
                problems.Add($"{where}: Title is empty");
            if (info.Group.Length > 14)
                problems.Add($"{where}: Group '{info.Group}' is longer than 14 characters (inner ring label)");
            if (info.Variant is { Length: > 22 })
                problems.Add($"{where}: Variant '{info.Variant}' is longer than 22 characters (outer ring label)");
            if (info.IconKey is not null && !IconKeys.Contains(info.IconKey))
                problems.Add($"{where}: IconKey '{info.IconKey}' is not in the shared vocabulary");
            if (info.OutputExtension is { } ext && (!ext.StartsWith('.') || ext != ext.ToLowerInvariant()))
                problems.Add($"{where}: OutputExtension must be lower-case with a leading dot");
            if (op.Options is { } schema)
            {
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in schema.Fields)
                {
                    if (!keys.Add(field.Key))
                        problems.Add($"{where}: duplicate option key '{field.Key}'");
                    if (field is ChoiceField choice && choice.Choices.All(c => c.Value != choice.Default))
                        problems.Add($"{where}: choice '{field.Key}' default '{choice.Default}' is not one of its choices");
                    if (field is NumberField n && (n.Default < n.Min || n.Default > n.Max))
                        problems.Add($"{where}: number '{field.Key}' default is outside [{n.Min}, {n.Max}]");
                }
            }
            try
            {
                op.CheckAvailability();
            }
            catch (Exception ex)
            {
                problems.Add($"{where}: CheckAvailability threw {ex.GetType().Name}");
            }
        }

        // Variant rules apply to what one wheel can show: operations of a group that accept the same kind of file.
        // (Video Trim and audio Trim share a group name but never meet, so neither needs a Variant label.)
        foreach (var group in operations.GroupBy(o => (o.Info.Category, o.Info.Group)))
        {
            var members = group.ToList();
            if (members.Count < 2)
                continue;
            foreach (var sample in SampleInputs.Value)
            {
                var together = members.Where(o => SafeAccepts(o, sample)).ToList();
                if (together.Count > 1 && together.Any(o => string.IsNullOrWhiteSpace(o.Info.Variant)))
                {
                    problems.Add($"{provider.Name}: group '{group.Key.Group}' shows {together.Count} operations for {Describe(sample)}, so each needs a Variant label");
                    break;
                }
                if (together.Count > 10)
                {
                    problems.Add($"{provider.Name}: group '{group.Key.Group}' shows {together.Count} variants for {Describe(sample)} (max 10 on the outer ring)");
                    break;
                }
            }
        }
        return problems;
    }

    /// <summary>One empty file per registered extension, plus a folder — probes for Accepts().</summary>
    static readonly Lazy<IReadOnlyList<InputFile>> SampleInputs = new(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClementineTests", "contract-samples");
        Directory.CreateDirectory(dir);
        var extensions = new[]
        {
            FileTypes.Image, FileTypes.Video, FileTypes.Audio, FileTypes.Pdf, FileTypes.Document, FileTypes.Spreadsheet,
            FileTypes.Presentation, FileTypes.Ebook, FileTypes.Text, FileTypes.Subtitle, FileTypes.Archive,
        }.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase);
        var samples = new List<InputFile>();
        foreach (var ext in extensions)
        {
            var path = Path.Combine(dir, "sample" + ext);
            if (!File.Exists(path))
                File.WriteAllBytes(path, []);
            samples.Add(InputFile.FromPath(path));
        }
        var folder = Path.Combine(dir, "folder sample");
        Directory.CreateDirectory(folder);
        samples.Add(InputFile.FromPath(folder));
        return samples;
    });

    static bool SafeAccepts(IOperation op, InputFile file)
    {
        try
        {
            return op.Accepts(file);
        }
        catch
        {
            return false;
        }
    }

    static string Describe(InputFile file) => file.IsDirectory ? "folders" : $"'{file.Extension}' files";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)+$")]
    private static partial Regex IdPattern();
}
