namespace Clementine.Core;

/// <summary>How a multi-file drop is turned into jobs.</summary>
public enum InputMode
{
    /// <summary>One job per input file (conversions, most tools).</summary>
    PerFile,

    /// <summary>One job receives every input (merge PDFs, join videos, images → PDF, zip).</summary>
    Combined,
}

/// <summary>Whether an operation can run on this machine right now (e.g. its external tool is installed).</summary>
public readonly record struct Availability(bool IsAvailable, string? Reason = null)
{
    public static Availability Available => new(true);

    public static Availability Unavailable(string reason) => new(false, reason);
}

/// <summary>A single thing Clementine can do to files. Implementations must be thread-safe and stateless per run.</summary>
public interface IOperation
{
    OperationInfo Info { get; }

    InputMode InputMode { get; }

    /// <summary>Parameters to ask for before running, or null to run immediately.</summary>
    OptionSchema? Options { get; }

    /// <summary>Fast, extension-based check. Must not open the file.</summary>
    bool Accepts(InputFile file);

    /// <summary>Whether the operation applies to this whole selection (counts, mixes of kinds).</summary>
    bool AcceptsAll(IReadOnlyList<InputFile> files);

    Availability CheckAvailability();

    /// <summary>Option defaults that depend on the inputs (e.g. trim end = clip duration). May probe files.</summary>
    Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the operation. Write outputs through <see cref="OperationRequest.Output"/>, report progress, honour
    /// cancellation, and throw <see cref="OperationException"/> with a friendly message on expected failures.
    /// Never modify or delete the input files.
    /// </summary>
    Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken);
}

/// <summary>Convenience base class with sensible defaults.</summary>
public abstract class OperationBase : IOperation
{
    protected OperationBase(OperationInfo info) => Info = info;

    public OperationInfo Info { get; }

    public virtual InputMode InputMode => InputMode.PerFile;

    /// <summary>Minimum number of files for a <see cref="InputMode.Combined"/> operation.</summary>
    public virtual int MinInputs => InputMode == InputMode.Combined ? 2 : 1;

    /// <summary>Maximum number of files for a <see cref="InputMode.Combined"/> operation.</summary>
    public virtual int MaxInputs => int.MaxValue;

    public virtual OptionSchema? Options => null;

    public abstract bool Accepts(InputFile file);

    public virtual bool AcceptsAll(IReadOnlyList<InputFile> files)
    {
        if (files.Count == 0)
            return false;
        if (InputMode == InputMode.Combined && (files.Count < MinInputs || files.Count > MaxInputs))
            return false;
        return files.All(Accepts);
    }

    public virtual Availability CheckAvailability() => Availability.Available;

    public virtual Task<OptionValues> GetDynamicDefaultsAsync(IReadOnlyList<InputFile> inputs, CancellationToken cancellationToken) =>
        Task.FromResult(new OptionValues());

    public abstract Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken);

    public override string ToString() => Info.Id;
}

/// <summary>Operation defined by delegates — handy for families of near-identical variants.</summary>
public sealed class DelegateOperation : OperationBase
{
    readonly Func<InputFile, bool> _accepts;
    readonly Func<OperationRequest, IProgress<JobProgress>, CancellationToken, Task<OperationResult>> _run;
    readonly Func<Availability>? _availability;
    readonly InputMode _inputMode;
    readonly OptionSchema? _options;

    public DelegateOperation(
        OperationInfo info,
        Func<InputFile, bool> accepts,
        Func<OperationRequest, IProgress<JobProgress>, CancellationToken, Task<OperationResult>> run,
        OptionSchema? options = null,
        InputMode inputMode = InputMode.PerFile,
        Func<Availability>? availability = null)
        : base(info)
    {
        _accepts = accepts;
        _run = run;
        _options = options;
        _inputMode = inputMode;
        _availability = availability;
    }

    public override InputMode InputMode => _inputMode;

    public override OptionSchema? Options => _options;

    public override bool Accepts(InputFile file) => _accepts(file);

    public override Availability CheckAvailability() => _availability?.Invoke() ?? Availability.Available;

    public override Task<OperationResult> RunAsync(OperationRequest request, IProgress<JobProgress> progress, CancellationToken cancellationToken) =>
        _run(request, progress, cancellationToken);
}
