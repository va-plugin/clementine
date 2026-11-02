namespace Clementine.Engines.Documents.Common;

/// <summary>Compares strings the way Explorer sorts names: "page 2" before "page 10", case-insensitive.</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsAsciiDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsAsciiDigit(y[j]))
                    j++;
                var numberX = x.AsSpan(startX, i - startX).TrimStart('0');
                var numberY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (numberX.Length != numberY.Length)
                    return numberX.Length.CompareTo(numberY.Length);
                var digits = numberX.CompareTo(numberY, StringComparison.Ordinal);
                if (digits != 0)
                    return digits;
                continue;
            }

            if (char.ToUpperInvariant(x[i]) != char.ToUpperInvariant(y[j]))
            {
                var byCulture = string.Compare(x, i, y, j, 1, StringComparison.CurrentCultureIgnoreCase);
                return byCulture != 0 ? byCulture : x[i].CompareTo(y[j]);
            }
            i++;
            j++;
        }
        var lengths = (x.Length - i).CompareTo(y.Length - j);
        return lengths != 0 ? lengths : string.Compare(x, y, StringComparison.Ordinal);
    }

    /// <summary>Inputs sorted by file name, naturally.</summary>
    public static IReadOnlyList<Core.InputFile> Sort(IEnumerable<Core.InputFile> files) =>
        files.OrderBy(f => f.Name, Instance).ToList();
}
