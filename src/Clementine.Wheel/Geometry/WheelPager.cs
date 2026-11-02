namespace Clementine.Wheel;

/// <summary>
/// One page of the inner ring: groups [<see cref="First"/>, <see cref="First"/> + <see cref="Count"/>) plus, when the
/// groups don't all fit, a trailing "More" segment that flips to the next page.
/// </summary>
public sealed record WheelPage(int First, int Count, bool HasMore)
{
    /// <summary>Segments drawn on this page (groups plus the optional "More").</summary>
    public int SegmentCount => Count + (HasMore ? 1 : 0);

    /// <summary>Index of the "More" segment, or -1.</summary>
    public int MoreSegment => HasMore ? Count : -1;
}

/// <summary>Splits many groups into pages so segments never get too small to read.</summary>
public static class WheelPager
{
    /// <summary>Most segments on one page (the radii grow a little from 13 to this many).</summary>
    public const int MaxSegmentsPerPage = 16;

    public static IReadOnlyList<WheelPage> Paginate(int groupCount, int maxSegmentsPerPage = MaxSegmentsPerPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(groupCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSegmentsPerPage, 2);
        if (groupCount <= maxSegmentsPerPage)
            return [new WheelPage(0, groupCount, HasMore: false)];

        var perPage = maxSegmentsPerPage - 1;
        var pages = new List<WheelPage>();
        for (var first = 0; first < groupCount; first += perPage)
            pages.Add(new WheelPage(first, Math.Min(perPage, groupCount - first), HasMore: true));
        return pages;
    }
}
