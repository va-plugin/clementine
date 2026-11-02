namespace Clementine.Engines.Documents.Common;

/// <summary>file:// URIs for local paths, for Edge, LibreOffice and &lt;base href&gt;.</summary>
internal static class FileUris
{
    /// <summary>
    /// "file:///C:/My%20Pages/Track%20%231.html": every segment escaped, so '#', '%' and '?' in folder or file names
    /// stay part of the path instead of becoming a fragment or query (new Uri(path) isn't reliable about that).
    /// </summary>
    public static string For(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            full = @"\\" + full[8..];
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            full = full[4..];

        string prefix;
        string rest;
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var separator = full.IndexOf('\\', 2);
            prefix = "file://" + (separator < 0 ? full[2..] : full[2..separator]) + "/";
            rest = separator < 0 ? "" : full[(separator + 1)..];
        }
        else
        {
            prefix = "file:///" + full[..2] + "/";
            rest = full.Length > 3 ? full[3..] : "";
        }
        var trailing = rest.EndsWith('\\') ? "/" : "";
        var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString);
        return prefix + string.Join('/', segments) + (rest.Length > 0 ? trailing : "");
    }
}
