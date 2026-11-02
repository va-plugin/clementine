namespace Clementine.Core;

/// <summary>Which wheel an operation appears on.</summary>
public enum OperationCategory
{
    /// <summary>Format conversions (Shift-drag wheel).</summary>
    Convert,

    /// <summary>Edits such as trim, resize, compress (Ctrl+Shift-drag wheel).</summary>
    Tool,

    /// <summary>File actions such as copy path, zip, checksum (Alt+Shift-drag wheel).</summary>
    Action,
}

/// <summary>Scheduling hint: heavy jobs (video encodes, LibreOffice) share a small concurrency pool.</summary>
public enum OperationWeight
{
    Light,
    Heavy,
}

/// <summary>Static description of an operation, used by the wheel, menus, toasts and the CLI.</summary>
public sealed record OperationInfo
{
    /// <summary>Stable, unique, lower-case dotted id, e.g. "video.prores.4444.audio". Never rename once shipped.</summary>
    public required string Id { get; init; }

    public required OperationCategory Category { get; init; }

    /// <summary>
    /// Inner-ring wheel label. Operations sharing a group become one segment; if the group holds more than one
    /// operation, they appear as variants on the outer ring. For conversions this is the target format ("ProRes", "PNG").
    /// </summary>
    public required string Group { get; init; }

    /// <summary>Outer-ring label inside the group ("4444 α · audio"). Null when the operation is alone in its group.</summary>
    public string? Variant { get; init; }

    /// <summary>Full human name for lists, toasts and the CLI ("ProRes 4444 (alpha, with audio)").</summary>
    public required string Title { get; init; }

    /// <summary>One-sentence explanation shown in the wheel centre and tooltips.</summary>
    public string? Description { get; init; }

    /// <summary>Semantic icon key from the vocabulary in docs/SPEC.md (the wheel maps it to a glyph).</summary>
    public string? IconKey { get; init; }

    /// <summary>Extension of the produced file (".mov"), or null for folder/text/multi-output operations.</summary>
    public string? OutputExtension { get; init; }

    /// <summary>Sort key: lower appears first. A group is ordered by its lowest member.</summary>
    public int Order { get; init; } = 500;

    public OperationWeight Weight { get; init; } = OperationWeight.Light;
}
