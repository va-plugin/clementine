using Clementine.Core;
using Clementine.Core.Hosting;

namespace Clementine.Wheel;

/// <summary>An operation on the wheel together with whether it can run on this machine.</summary>
public sealed record WheelOption(IOperation Operation, Availability Availability)
{
    public OperationInfo Info => Operation.Info;

    public bool IsAvailable => Availability.IsAvailable;

    /// <summary>Outer-ring label: the variant name, or the title for a lone operation.</summary>
    public string Label => string.IsNullOrWhiteSpace(Info.Variant) ? Info.Title : Info.Variant!;
}

/// <summary>An inner-ring group: operations sharing <see cref="OperationInfo.Group"/>, recommended first.</summary>
public sealed class WheelGroup
{
    public WheelGroup(string name, string? iconKey, IReadOnlyList<WheelOption> options)
    {
        if (options.Count == 0)
            throw new ArgumentException("A wheel group needs at least one operation.", nameof(options));
        Name = name;
        IconKey = iconKey;
        Options = options;
    }

    public string Name { get; }

    public string? IconKey { get; }

    public IReadOnlyList<WheelOption> Options { get; }

    /// <summary>What a drop on the inner segment runs: the first operation that can run, else the first.</summary>
    public WheelOption Default => Options.FirstOrDefault(o => o.IsAvailable) ?? Options[0];

    public bool IsAvailable => Options.Any(o => o.IsAvailable);

    public bool HasVariants => Options.Count > 1;
}

/// <summary>Everything one wheel shows for a selection and mode: the groups, best first, and a summary of the files.</summary>
public sealed class WheelModel
{
    WheelModel(IReadOnlyList<InputFile> files, WheelMode mode, IReadOnlyList<WheelGroup> groups)
    {
        Files = files;
        Mode = mode;
        Groups = groups;
        Summary = SelectionSummary.Describe(files);
    }

    public IReadOnlyList<InputFile> Files { get; }

    public WheelMode Mode { get; }

    public IReadOnlyList<WheelGroup> Groups { get; }

    public SelectionSummary Summary { get; }

    /// <summary>True when no operation accepts every file (the centre then explains why).</summary>
    public bool IsEmpty => Groups.Count == 0;

    /// <summary>
    /// Asks the catalog which operations of <paramref name="mode"/> accept every file, groups them and checks each
    /// one's availability. Never throws because of a misbehaving operation.
    /// </summary>
    public static WheelModel Build(OperationCatalog catalog, IReadOnlyList<InputFile> files, WheelMode mode, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(files);
        log ??= catalog.Context.Log;
        if (files.Count == 0)
            return new WheelModel(files, mode, []);

        var operations = catalog.ForInputs(files, mode.ToCategory());
        var groups = OperationCatalog.Group(operations)
            .Select(g => new WheelGroup(
                g.Name,
                g.IconKey,
                g.Operations.Take(WheelLayout.MaxVariants).Select(o => new WheelOption(o, SafeAvailability(o, log))).ToList()))
            .ToList();
        return new WheelModel(files, mode, groups);
    }

    /// <summary>Builds a model from ready-made groups (previews and tests).</summary>
    internal static WheelModel FromGroups(IReadOnlyList<InputFile> files, WheelMode mode, IReadOnlyList<WheelGroup> groups) =>
        new(files, mode, groups);

    static Availability SafeAvailability(IOperation operation, ILog log)
    {
        try
        {
            return operation.CheckAvailability();
        }
        catch (Exception ex)
        {
            log.Warn($"{operation.Info.Id}.CheckAvailability threw", ex);
            return Availability.Unavailable("This isn't available right now.");
        }
    }
}
