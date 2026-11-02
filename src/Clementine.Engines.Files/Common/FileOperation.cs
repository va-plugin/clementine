using Clementine.Core;

namespace Clementine.Engines.Files.Common;

/// <summary>
/// Base for this engine's operations: an input filter, an optional option schema and a body that runs on a
/// worker thread. Most bodies here are synchronous file work, so <see cref="Run"/> is the usual override.
/// </summary>
internal abstract class FileOperation : OperationBase
{
    readonly Func<InputFile, bool> _accepts;
    readonly OptionSchema? _options;
    readonly InputMode _inputMode;
    readonly int _minInputs;

    protected FileOperation(
        OperationInfo info,
        Func<InputFile, bool> accepts,
        OptionSchema? options = null,
        InputMode inputMode = InputMode.PerFile,
        int minInputs = 1)
        : base(info)
    {
        _accepts = accepts;
        _options = options;
        _inputMode = inputMode;
        _minInputs = minInputs;
    }

    public override InputMode InputMode => _inputMode;

    public override int MinInputs => _minInputs;

    public override OptionSchema? Options => _options;

    public override bool Accepts(InputFile file) => _accepts(file);

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(request, progress, cancellationToken), cancellationToken);

    /// <summary>Synchronous body, called on a thread-pool thread.</summary>
    protected virtual OperationResult Run(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Info.Id} must override Run or RunAsync.");
}

/// <summary>Builds <see cref="OperationInfo"/> records with this engine's defaults.</summary>
internal static class Infos
{
    public static OperationInfo Convert(string id, string group, string title, string description, string icon, string? extension, int order, string? variant = null) =>
        Make(id, OperationCategory.Convert, group, variant, title, description, icon, extension, order);

    public static OperationInfo Tool(string id, string group, string title, string description, string icon, string? extension, int order, string? variant = null) =>
        Make(id, OperationCategory.Tool, group, variant, title, description, icon, extension, order);

    public static OperationInfo Action(string id, string group, string title, string description, string icon, string? extension, int order, string? variant = null) =>
        Make(id, OperationCategory.Action, group, variant, title, description, icon, extension, order);

    /// <summary>Marks work that can take minutes on big inputs (archives of video folders, hashing GBs).</summary>
    public static OperationInfo Heavy(this OperationInfo info) => info with { Weight = OperationWeight.Heavy };

    static OperationInfo Make(
        string id, OperationCategory category, string group, string? variant, string title, string description, string icon, string? extension, int order) => new()
    {
        Id = id,
        Category = category,
        Group = group,
        Variant = variant,
        Title = title,
        Description = description,
        IconKey = icon,
        OutputExtension = extension,
        Order = order,
    };
}

/// <summary>Reusable input filters.</summary>
internal static class Inputs
{
    public static Func<InputFile, bool> Extensions(IEnumerable<string> extensions)
    {
        var set = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        return file => !file.IsDirectory && set.Contains(file.Extension);
    }

    public static Func<InputFile, bool> Extensions(params string[] extensions) => Extensions((IEnumerable<string>)extensions);

    public static readonly Func<InputFile, bool> Folders = file => file.IsDirectory;

    public static readonly Func<InputFile, bool> Anything = _ => true;
}
