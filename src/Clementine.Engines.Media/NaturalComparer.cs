namespace Clementine.Engines.Media;

/// <summary>Orders names the way people expect: "clip 2" before "clip 10", case-insensitively.</summary>
public sealed class NaturalComparer : IComparer<string>
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
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsDigit(y[j]))
                    j++;
                var digitsX = x.AsSpan(startX, i - startX).TrimStart('0');
                var digitsY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (digitsX.Length != digitsY.Length)
                    return digitsX.Length.CompareTo(digitsY.Length);
                var byValue = digitsX.CompareTo(digitsY, StringComparison.Ordinal);
                if (byValue != 0)
                    return byValue;
                continue;
            }

            var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0)
                return byChar;
            i++;
            j++;
        }
        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(x, y);
    }
}
