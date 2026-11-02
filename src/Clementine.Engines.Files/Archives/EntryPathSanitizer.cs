namespace Clementine.Engines.Files.Archives;

/// <summary>Outcome of cleaning an archive entry path.</summary>
/// <param name="Segments">Safe, Windows-legal path segments (empty for entries that name the root, like "./").</param>
/// <param name="WasAbsolute">The stored path was absolute ("/etc/x", "C:\x", "\\server\share\x") and was made relative.</param>
/// <param name="WasRenamed">Characters or names Windows can't store were replaced.</param>
internal sealed record SanitizedPath(IReadOnlyList<string> Segments, bool WasAbsolute, bool WasRenamed)
{
    public bool IsRoot => Segments.Count == 0;

    public string Relative => string.Join(Path.DirectorySeparatorChar, Segments);
}

/// <summary>
/// Turns untrusted entry paths into safe relative paths. Any ".." segment is a zip-slip attempt and is rejected;
/// absolute paths are made relative (like GNU tar); names Windows can't store (reserved device names, ':' streams,
/// trailing dots/spaces, control characters) are made legal. Trailing dots and spaces are trimmed <em>before</em>
/// the ".." check, because Windows would trim them too (".. " is "..").
/// </summary>
internal static class EntryPathSanitizer
{
    const int MaxSegmentLength = 255;

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <exception cref="UnsafeEntryPathException">The path tries to escape the destination.</exception>
    public static SanitizedPath Sanitize(string rawPath)
    {
        var path = rawPath.Replace('\\', '/');
        var wasAbsolute = false;

        // "\\?\C:\x", "\\.\C:\x" device paths and UNC "\\server\share" all start with separators.
        if (path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal))
        {
            path = path[4..];
            wasAbsolute = true;
        }
        if (path.StartsWith('/'))
        {
            path = path.TrimStart('/');
            wasAbsolute = true;
        }
        // "C:/x" is absolute; "a:b" is just a name with a colon (the colon is replaced below).
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] == '/'))
        {
            path = path[2..].TrimStart('/');
            wasAbsolute = true;
        }

        var segments = new List<string>();
        var renamed = false;
        foreach (var raw in path.Split('/'))
        {
            if (raw.Length == 0 || raw == ".")
                continue;

            var segment = ReplaceIllegalCharacters(raw, ref renamed);
            var trimmed = segment.TrimEnd('.', ' ');
            if (raw == ".." || trimmed == ".." || segment.Trim() == "..")
                throw new UnsafeEntryPathException(rawPath);
            if (trimmed.Length == 0)
            {
                // "...", "   " — not a usable name on Windows.
                trimmed = "_";
                renamed = true;
            }
            else if (trimmed.Length != segment.Length)
            {
                renamed = true;
            }

            if (IsReservedName(trimmed))
            {
                trimmed = "_" + trimmed;
                renamed = true;
            }
            if (trimmed.Length > MaxSegmentLength)
            {
                trimmed = Truncate(trimmed);
                renamed = true;
            }
            segments.Add(trimmed);
        }
        return new SanitizedPath(segments, wasAbsolute, renamed);
    }

    /// <summary>True when <paramref name="candidate"/> is <paramref name="root"/> itself or inside it.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(candidate);
        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static string ReplaceIllegalCharacters(string segment, ref bool renamed)
    {
        Span<char> buffer = segment.Length <= 256 ? stackalloc char[segment.Length] : new char[segment.Length];
        var changed = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            // Bidirectional controls let "‮gnp.exe" display as "exe.png" in Explorer; ZWJ/ZWNJ (emoji, Indic) stay.
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*'
                || c is '؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
            {
                buffer[i] = '_';
                changed = true;
            }
            else
            {
                buffer[i] = c;
            }
        }
        if (!changed)
            return segment;
        renamed = true;
        return new string(buffer);
    }

    /// <summary>"CON", "con.txt", "Lpt1.tar.gz" — Windows treats the part before the first dot as a device.</summary>
    static bool IsReservedName(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem);
    }

    static string Truncate(string segment)
    {
        var extension = Path.GetExtension(segment);
        if (extension.Length > 16)
            extension = "";
        var stem = segment[..(MaxSegmentLength - extension.Length)].TrimEnd('.', ' ');
        return stem + extension;
    }
}

/// <summary>An entry path that would escape the extraction folder (zip-slip).</summary>
internal sealed class UnsafeEntryPathException(string path) : Exception($"Unsafe archive entry path: {path}")
{
    public string EntryPath { get; } = path;
}
