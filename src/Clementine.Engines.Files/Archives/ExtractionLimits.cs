namespace Clementine.Engines.Files.Archives;

/// <summary>
/// Guards against decompression bombs and runaway extractions. A bomb is anything that would expand beyond
/// max(<see cref="AlwaysAllowedBytes"/>, archive size × <see cref="MaxExpansionRatio"/>); deflate tops out near
/// 1032:1, so real-world archives stay well below the default ratio, while crafted ones (overlapping ZIP entries,
/// bzip2/xz/zstd of zeros) blow far past it.
/// </summary>
internal sealed record ExtractionLimits
{
    public static ExtractionLimits Default { get; } = new();

    public long MaxEntries { get; init; } = 1_000_000;

    public double MaxExpansionRatio { get; init; } = 1000;

    /// <summary>Output that is always fine regardless of ratio (tiny archives of zeros are not dangerous).</summary>
    public long AlwaysAllowedBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Stop when the destination drive would drop below this much free space.</summary>
    public long FreeSpaceReserveBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Maximum uncompressed bytes for an archive of <paramref name="archiveBytes"/> bytes.</summary>
    public long AllowedOutput(long archiveBytes)
    {
        var byRatio = archiveBytes * MaxExpansionRatio;
        return byRatio >= long.MaxValue ? long.MaxValue : Math.Max(AlwaysAllowedBytes, (long)byRatio);
    }
}
