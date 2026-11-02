using Clementine.Core;

namespace Clementine.Engines.Documents.Common;

/// <summary>Runs one document operation (the body of <see cref="IOperation.RunAsync"/>).</summary>
internal delegate Task<OperationResult> OperationHandler(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken);

/// <summary>Computes option defaults from the inputs (e.g. the current PDF title, the page count).</summary>
internal delegate Task<OptionValues> DynamicDefaultsHandler(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken);

/// <summary>
/// The operation type every Documents feature is built from: an extension filter, an optional availability check
/// (external tools), an optional option schema and a handler. Instances are immutable and thread-safe.
/// </summary>
internal sealed class DocumentOperation : OperationBase
{
    readonly IReadOnlySet<string> _extensions;
    readonly OperationHandler _run;

    public DocumentOperation(OperationInfo info, IReadOnlySet<string> extensions, OperationHandler run)
        : base(info)
    {
        _extensions = extensions;
        _run = run;
    }

    /// <summary>Per-file (default) or combined.</summary>
    public InputMode Mode { get; init; } = InputMode.PerFile;

    /// <summary>Minimum number of inputs for combined operations.</summary>
    public int Minimum { get; init; } = 2;

    public OptionSchema? Schema { get; init; }

    /// <summary>Availability check, e.g. "Requires LibreOffice". Null means always available.</summary>
    public Func<Availability>? Availability { get; init; }

    public DynamicDefaultsHandler? DynamicDefaults { get; init; }

    /// <summary>Also accept folders whose name ends with one of the extensions (macOS packages such as "Report.pages").</summary>
    public bool AcceptsPackages { get; init; }

    public override InputMode InputMode => Mode;

    public override int MinInputs => Mode == InputMode.Combined ? Minimum : 1;

    public override OptionSchema? Options => Schema;

    public override bool Accepts(InputFile file)
    {
        if (file.IsDirectory)
            return AcceptsPackages && _extensions.Contains(FileTypes.ExtensionOf(file.Name));
        return _extensions.Contains(file.Extension);
    }

    public override Availability CheckAvailability() => Availability?.Invoke() ?? Core.Availability.Available;

    public override Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken) =>
        DynamicDefaults is null ? base.GetDynamicDefaultsAsync(inputs, cancellationToken) : SafeDefaultsAsync(inputs, cancellationToken);

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _run(request, progress, cancellationToken);
    }

    async Task<OptionValues> SafeDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken)
    {
        try
        {
            return await DynamicDefaults!(inputs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defaults are a convenience: an unreadable file is reported properly when the job runs.
            return new OptionValues();
        }
    }
}

/// <summary>Short factories for <see cref="OperationInfo"/> so operation tables stay readable.</summary>
internal static class Info
{
    public static OperationInfo Convert(
        string id, string group, string? variant, string title, string description, string icon, string? extension, int order,
        bool heavy = false) => new()
        {
            Id = id,
            Category = OperationCategory.Convert,
            Group = group,
            Variant = variant,
            Title = title,
            Description = description,
            IconKey = icon,
            OutputExtension = extension,
            Order = order,
            Weight = heavy ? OperationWeight.Heavy : OperationWeight.Light,
        };

    public static OperationInfo Tool(
        string id, string group, string? variant, string title, string description, string icon, string? extension, int order,
        bool heavy = false) =>
        Convert(id, group, variant, title, description, icon, extension, order, heavy) with { Category = OperationCategory.Tool };
}
